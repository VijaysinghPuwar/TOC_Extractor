using TocExtractor.App.Pipeline;
using TocExtractor.Core.Links;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

public sealed class PipelineTests
{
    private static readonly UrlGuard Guard = new(resolver: new PublicResolver());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_dry_run_with_dump_html_writes_the_contents_page_and_nothing_else()
    {
        var output = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(2), supportsCapture: true);

        var result = await Run(source, output, dryRun: true, dumpHtml: true);

        Assert.Equal(PipelineOutcome.Ok, result.Outcome);
        Assert.Equal(["toc.html"], Directory.GetFiles(output).Select(Path.GetFileName));
        Assert.Equal([Book.Toc], source.UrlsLoaded);
    }

    [Fact]
    public async Task The_observer_sees_the_links_before_any_chapter()
    {
        var events = new OrderObserver();

        await ExtractionPipeline.RunAsync(
            Request(Scratch.Directory()), new StubPageSource(Book.Pages(2)), Guard,
            RobotsPolicy.Missing("https://e.com"), new RateLimiter(TimeSpan.Zero), events, Token);

        Assert.Equal("collected", events.Order[0]);
        Assert.Equal(["collected", "record", "record"], events.Order);
    }

    [Fact]
    public async Task An_unreadable_contents_page_fails_without_writing()
    {
        var output = Path.Combine(Scratch.Directory(), "out");
        var observer = new RecordingObserver();

        var result = await ExtractionPipeline.RunAsync(
            Request(output), new StubPageSource(new Dictionary<string, StubPage>()), Guard,
            RobotsPolicy.Missing("https://e.com"), new RateLimiter(TimeSpan.Zero), observer, Token);

        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Contains(observer.Lines, line => line.StartsWith("could not read the table of contents", StringComparison.Ordinal));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Quiet_keeps_failures_and_drops_the_chatter()
    {
        var pages = Book.Pages(2);
        pages["https://e.com/ch/2"] = new StubPage { MissingSelector = true };
        var observer = new RecordingObserver();

        await ExtractionPipeline.RunAsync(
            Request(Scratch.Directory()) with { Quiet = true }, new StubPageSource(pages), Guard,
            RobotsPolicy.Missing("https://e.com"), new RateLimiter(TimeSpan.Zero), observer, Token);

        var line = Assert.Single(observer.Lines);
        Assert.StartsWith("chapter 2 failed", line, StringComparison.Ordinal);
    }

    private static Task<PipelineResult> Run(StubPageSource source, string output, bool dryRun, bool dumpHtml) =>
        ExtractionPipeline.RunAsync(
            Request(output, dryRun) with { DumpHtml = dumpHtml },
            source,
            Guard,
            RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero),
            new RecordingObserver(),
            Token);

    private static PipelineRequest Request(string output, bool dryRun = false) => new()
    {
        TocUrl = Book.Toc,
        Selectors = SelectorSet.Create("a.ch", "h1", "article"),
        OutputDirectory = output,
        Formats = ["text"],
        Fetch = new Core.Fetching.FetchOptions
        {
            Concurrency = 1,
            DryRun = dryRun,
            CaptureHtml = dryRun,
            MinDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
            WaitAfterLoad = TimeSpan.Zero,
        },
    };

    [Fact]
    public async Task A_range_is_saved_as_one_file_in_reading_order_and_nothing_else_merged()
    {
        var chapters = Enumerable.Range(1, 5)
            .Select(n => new Scanning.ScannedChapter(n, $"Chapter {n}", $"https://e.com/ch/{n}"))
            .ToList();
        var scan = new Scanning.ScanResult
        {
            NovelUrl = Book.Toc,
            BookTitle = "Stub Book",
            Chapters = chapters,
            Layout = new Scanning.ChapterLayout("h1", "article", "a[rel=next]", "a[rel=prev]"),
        };
        var request = new RangeRequest
        {
            Scan = scan,
            Plan = Scanning.RangePlanner.Plan(scan, 2, 4),
            From = 2,
            To = 4,
            OutputRoot = Scratch.Directory(),
            Fetch = new Core.Fetching.FetchOptions { Concurrency = 1, MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
        };

        var result = await RangePipeline.RunAsync(
            request, new StubPageSource(Book.Pages(5)), Guard, RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero), new RecordingObserver(), cancellationToken: Token);

        Assert.Equal(PipelineOutcome.Ok, result.Outcome);
        Assert.False(File.Exists(Path.Combine(request.BookDirectory, "combined.txt")));
        var book = await File.ReadAllTextAsync(Assert.Single(result.Files), Token);
        var headings = book.Split('\n').Where(line => line.StartsWith("Chapter ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Chapter 2", "Chapter 3", "Chapter 4"], headings);
    }

    private sealed class OrderObserver : IPipelineObserver
    {
        private readonly Lock gate = new();

        public List<string> Order { get; } = [];

        public void Collected(Core.Fetching.CollectedLinks collected)
        {
            lock (this.gate)
            {
                this.Order.Add("collected");
            }
        }

        public void Record(Core.Models.ChapterRecord record)
        {
            lock (this.gate)
            {
                this.Order.Add("record");
            }
        }
    }
}

public sealed class ChapterTitleTests
{
    [Theory]
    [InlineData("Page 1", "Chapter 1\nThe text.", "Chapter 1")]
    [InlineData("page 185", "  Chapter 185: The Road\nText.", "Chapter 185")]
    [InlineData("Page 130", "the end of chapter 140.\n\nChapter 141 A Melee\nText.", "Page 130")]
    [InlineData("Page 128", "The middle of a long chapter.", "Page 128")]
    [InlineData("Page 7", "Chapter 8\nText.", "Page 7")]
    [InlineData("Page 12: The Storm Road", "Chapter 12\nText.", "Page 12: The Storm Road")]
    [InlineData("Chapter 3: Low Tide", "Text.", "Chapter 3: Low Tide")]
    [InlineData("The Last Page 9", "Chapter 9\nText.", "The Last Page 9")]
    public void A_page_becomes_a_chapter_only_when_its_text_opens_as_that_chapter(string heading, string body, string saved)
    {
        Assert.Equal(saved, Pipeline.ChapterTitles.Tidy(heading, body));
    }
}
