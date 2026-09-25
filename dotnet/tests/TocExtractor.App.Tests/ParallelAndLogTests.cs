using TocExtractor.App.Pipeline;
using TocExtractor.App.Scanning;
using TocExtractor.App.Session;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Models;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

public sealed class ParallelAndLogTests
{
    private static readonly UrlGuard Guard = new(resolver: new PublicResolver());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_csv_log_keeps_every_row_written_from_many_threads()
    {
        var path = Path.Combine(Scratch.Directory(), "log.csv");
        using (var log = new CsvLog(path, "#1"))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(
                () =>
                {
                    for (var i = 0; i < 250; i++)
                    {
                        log.Info("row", $"worker {worker} row {i}");
                    }
                },
                Token)));
        }

        var rows = await File.ReadAllLinesAsync(path, Token);
        Assert.Equal(CsvLog.Header, rows[0]);
        Assert.Equal(2000, rows.Length - 1);
    }

    [Fact]
    public async Task Each_row_is_on_disk_the_moment_it_is_written()
    {
        var path = Path.Combine(Scratch.Directory(), "log.csv");
        using var log = new CsvLog(path, "#2");

        log.Warning("check", "waiting for the person", 7, "https://e.com/ch/7");

        // Read while the log is still open, as after a crash.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(Token);
        Assert.Contains(",warning,#2,check,7,https://e.com/ch/7,waiting for the person", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "line break")]
    [InlineData("=HYPERLINK(1)", "'=HYPERLINK(1)")]
    [InlineData("-1", "'-1")]
    public void Fields_are_quoted_and_never_run_as_formulas(string value, string expected)
    {
        Assert.Equal(expected, CsvLog.Field(value));
    }

    [Fact]
    public async Task An_error_row_carries_the_cause_and_the_stack()
    {
        var path = Path.Combine(Scratch.Directory(), "log.csv");
        using (var log = new CsvLog(path, "app"))
        {
            try
            {
                throw new InvalidOperationException("outer", new IOException("disk gone"));
            }
            catch (InvalidOperationException exception)
            {
                log.Error("crash", exception, "during save");
            }
        }

        var text = await File.ReadAllTextAsync(path, Token);
        Assert.Contains("during save | System.InvalidOperationException: outer | caused by System.IO.IOException: disk gone | stack:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_after_the_log_is_closed_is_ignored()
    {
        var log = new CsvLog(Path.Combine(Scratch.Directory(), "log.csv"), "#1");
        log.Dispose();

        log.Info("late", "a report that arrived after the extraction closed");
        log.Dispose();
    }

    [Fact]
    public async Task Views_of_one_log_share_its_file_and_only_the_owner_closes_it()
    {
        var path = Path.Combine(Scratch.Directory(), "log.csv");
        using (var common = new CsvLog(path, "app"))
        {
            var one = common.For("#1 Book");
            var two = common.For("#2 Other");
            one.Info("saved", "one");
            one.Dispose();
            two.Info("saved", "two");
            common.Info("stop", "closing");
        }

        var rows = await File.ReadAllLinesAsync(path, Token);
        Assert.Equal(4, rows.Length);
        Assert.Contains(",#1 Book,saved,", rows[1], StringComparison.Ordinal);
        Assert.Contains(",#2 Other,saved,", rows[2], StringComparison.Ordinal);
        Assert.Contains(",app,stop,", rows[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Log_names_never_collide_within_a_second()
    {
        var folder = Scratch.Directory();
        var now = DateTimeOffset.Now;
        var first = CsvLog.NewPath(folder, now, "extraction 1 novel.example");
        File.WriteAllText(first, "");

        var second = CsvLog.NewPath(folder, now, "extraction 1 novel.example");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Books_on_one_site_share_its_pace_and_other_sites_do_not()
    {
        await using var host = new BrowserHost(Environment());

        var one = host.LimiterFor("https://novel.example/book/1", TimeSpan.FromSeconds(2));
        var two = host.LimiterFor("https://novel.example/book/2", TimeSpan.FromSeconds(2));
        var other = host.LimiterFor("https://other.example/book", TimeSpan.FromSeconds(2));

        Assert.Same(one, two);
        Assert.NotSame(one, other);
    }

    [Fact]
    public async Task A_slower_pace_asked_for_later_raises_the_shared_one()
    {
        await using var host = new BrowserHost(Environment());
        host.LimiterFor("https://novel.example/a", TimeSpan.FromSeconds(1));

        var limiter = host.LimiterFor("https://novel.example/b", TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), limiter.IntervalFor(new Uri("https://novel.example/a")));
    }

    [Fact]
    public async Task Every_extraction_shares_one_browser()
    {
        var starts = 0;
        var environment = Environment() with
        {
            StartSource = (_, _, _) =>
            {
                Interlocked.Increment(ref starts);
                return Task.FromResult<Core.Pages.IPageSource>(new StubPageSource(Book.Pages(1)));
            },
        };
        await using var host = new BrowserHost(environment);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => host.SourceAsync(TimeSpan.FromSeconds(5), Token)));

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Stopping_still_writes_the_chapters_saved_so_far_named_as_partial()
    {
        var output = Scratch.Directory();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var observer = new StopAfter(2, stop);
        var request = Request(output, 1, 5);

        var result = await RangePipeline.RunAsync(
            request, new StubPageSource(Book.Pages(5)), Guard, RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero), observer, cancellationToken: stop.Token);

        Assert.True(result.Stopped);
        var file = Path.GetFileName(Assert.Single(result.Files));
        Assert.Matches(@"^Stub Book 1-\d\.txt$", file);
        Assert.NotEqual("Stub Book 1-5.txt", file);
        Assert.NotEmpty(result.Missing);

        // Finishing the range later replaces the partial book with the whole one.
        var whole = await RangePipeline.RunAsync(
            Request(output, 1, 5), new StubPageSource(Book.Pages(5)), Guard, RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero), new RecordingObserver(), cancellationToken: Token);

        Assert.Equal(PipelineOutcome.Ok, whole.Outcome);
        Assert.Equal(["Stub Book 1-5.txt"], Directory.GetFiles(request.BookDirectory, "Stub Book*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_failed_chapter_ends_the_book_file_and_its_name_before_the_gap()
    {
        var output = Scratch.Directory();
        var pages = Book.Pages(5);
        pages["https://e.com/ch/3"] = new StubPage { MissingSelector = true };
        var observer = new RecordingObserver();

        var result = await RangePipeline.RunAsync(
            Request(output, 1, 5), new StubPageSource(pages), Guard, RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero), observer, cancellationToken: Token);

        // Asked for 1-5, chapter 3 failed: the book is 1-2, never 1-5.
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.Equal([3], result.Missing);
        var path = Assert.Single(result.Files);
        Assert.Equal("Stub Book 1-2.txt", Path.GetFileName(path));
        var text = await File.ReadAllTextAsync(path, Token);
        Assert.Contains("Chapter 2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Chapter 4", text, StringComparison.Ordinal);
        Assert.Contains(observer.Lines, line => line.Contains("holds chapters 1-2 only", StringComparison.Ordinal));

        // Filling the gap later writes 1-5, and 1-2 goes.
        var whole = await RangePipeline.RunAsync(
            Request(output, 1, 5), new StubPageSource(Book.Pages(5)), Guard, RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero), new RecordingObserver(), cancellationToken: Token);
        Assert.Equal(PipelineOutcome.Ok, whole.Outcome);
        Assert.Equal(["Stub Book 1-5.txt"], Directory.GetFiles(Path.GetDirectoryName(path)!, "Stub Book*").Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3 }, new[] { 1, 2, 3 })]
    [InlineData(new[] { 1, 2, 4, 5 }, new[] { 1, 2 })]
    [InlineData(new[] { 2, 3, 7 }, new[] { 2, 3 })]
    [InlineData(new int[0], new int[0])]
    public void The_first_unbroken_run_is_what_a_book_file_holds(int[] saved, int[] expected)
    {
        var chapters = new SortedDictionary<int, SavedChapter>(saved.ToDictionary(n => n, n => new SavedChapter(n, $"Chapter {n}", "text")));

        Assert.Equal(expected, BookFiles.FirstRun(chapters).Keys);
    }

    [Fact]
    public async Task Each_page_load_is_traced_for_the_log()
    {
        var observer = new TraceObserver();

        await RangePipeline.RunAsync(
            Request(Scratch.Directory(), 2, 3), new StubPageSource(Book.Pages(5)), Guard, RobotsPolicy.Missing("https://e.com"),
            new RateLimiter(TimeSpan.Zero), observer, cancellationToken: Token);

        Assert.Contains(observer.Traces, trace => trace is { Event: "load", Chapter: 2 });
        Assert.Contains(observer.Traces, trace => trace is { Event: "loaded", Chapter: 3 } && trace.Detail.Contains("characters", StringComparison.Ordinal));
    }

    private static NovelEnvironment Environment() => new()
    {
        StartSource = (_, _, _) => Task.FromResult<Core.Pages.IPageSource>(new StubPageSource(Book.Pages(1))),
        FetchRobots = (_, _) => null,
        BrowserProfileDirectory = Scratch.Directory(),
        Resolver = new PublicResolver(),
    };

    private static RangeRequest Request(string output, int from, int to)
    {
        var chapters = Enumerable.Range(1, 5)
            .Select(n => new ScannedChapter(n, $"Chapter {n}", $"https://e.com/ch/{n}"))
            .ToList();
        var scan = new ScanResult
        {
            NovelUrl = Book.Toc,
            BookTitle = "Stub Book",
            Chapters = chapters,
            Layout = new ChapterLayout("h1", "article", "a[rel=next]", "a[rel=prev]"),
        };
        return new RangeRequest
        {
            Scan = scan,
            Plan = RangePlanner.Plan(scan, from, to),
            From = from,
            To = to,
            OutputRoot = output,
            Fetch = new FetchOptions { Concurrency = 1, MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
        };
    }

    /// <summary>Presses Stop once a number of chapters are saved.</summary>
    private sealed class StopAfter(int count, CancellationTokenSource stop) : IPipelineObserver
    {
        private int saved;

        public void Record(ChapterRecord record)
        {
            if (Interlocked.Increment(ref this.saved) == count)
            {
                stop.Cancel();
            }
        }
    }

    private sealed class TraceObserver : IPipelineObserver
    {
        private readonly Lock gate = new();

        public List<FetchTrace> Traces { get; } = [];

        public void Trace(FetchTrace trace)
        {
            lock (this.gate)
            {
                this.Traces.Add(trace);
            }
        }
    }
}
