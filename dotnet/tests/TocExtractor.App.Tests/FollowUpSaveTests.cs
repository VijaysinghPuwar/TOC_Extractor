using TocExtractor.App.Pipeline;
using TocExtractor.App.Scanning;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Models;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

/// <summary>
/// Saving more chapters of a book into the folder that already holds some of
/// it: the next range, the same range again, after the folder was tidied by
/// hand, and with the folder deleted while chapters are being saved. Each of
/// these used to end in an error or a book with holes, as seen in a real log.
/// </summary>
public sealed class FollowUpSaveTests
{
    private static readonly UrlGuard Guard = new(resolver: new PublicResolver());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_next_range_saves_into_the_same_folder_and_fetches_only_the_new_chapters()
    {
        var root = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(6));

        var first = await Save(Scan(), 1, 3, root, source);
        var second = await Save(Scan(), 4, 6, root, source);

        Assert.Equal(PipelineOutcome.Ok, first.Outcome);
        Assert.Equal(PipelineOutcome.Ok, second.Outcome);
        Assert.Equal(["https://e.com/ch/1", "https://e.com/ch/2", "https://e.com/ch/3", "https://e.com/ch/4", "https://e.com/ch/5", "https://e.com/ch/6"], source.UrlsLoaded);
        Assert.True(File.Exists(Path.Combine(root, "Stub Book", "Stub Book 1-3.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Stub Book", "Stub Book 4-6.txt")));
    }

    [Fact]
    public async Task A_rescan_that_reads_the_title_from_another_element_still_saves_into_the_same_folder()
    {
        var root = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(6));
        await Save(Scan(), 1, 3, root, source);

        // novelfire.net, scanned twice, gave the title as span.chapter-title
        // one time and "#content > h4" the next: the second save was refused.
        var observer = new RecordingObserver();
        var again = await Save(Scan() with { Layout = new ChapterLayout("#content > h4", "article", "a[rel=next]", "a[rel=prev]") }, 1, 6, root, source, observer);

        Assert.Equal(PipelineOutcome.Ok, again.Outcome);
        Assert.DoesNotContain(observer.Lines, line => line.Contains("refusing", StringComparison.Ordinal));
        Assert.Equal(6, source.UrlsLoaded.Count);
        Assert.Single(Directory.GetDirectories(root));
        Assert.True(File.Exists(Path.Combine(root, "Stub Book", "Stub Book 1-6.txt")));
    }

    [Theory]
    [InlineData("https://e.com/toc/chapters?page=2&sort_by=new")]
    [InlineData("https://www.e.com/toc/")]
    [InlineData("https://e.com/toc?tab=toc")]
    public async Task The_same_book_opened_from_its_chapter_list_saves_into_the_same_folder(string novelUrl)
    {
        var root = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(6));
        await Save(Scan(), 1, 3, root, source);

        var again = await Save(Scan() with { NovelUrl = novelUrl }, 1, 6, root, source);

        Assert.Equal(PipelineOutcome.Ok, again.Outcome);
        Assert.Single(Directory.GetDirectories(root));
        Assert.Equal(6, source.UrlsLoaded.Count);
    }

    [Fact]
    public async Task Another_book_with_the_same_title_still_gets_a_folder_of_its_own()
    {
        var root = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(3));
        await Save(Scan(), 1, 3, root, source);

        var other = await Save(Scan() with { NovelUrl = "https://e.com/another-book" }, 1, 3, root, source);

        Assert.Equal(PipelineOutcome.Ok, other.Outcome);
        Assert.Equal(2, Directory.GetDirectories(root).Length);
    }

    [Fact]
    public async Task Chapters_whose_files_were_deleted_by_hand_are_fetched_again()
    {
        var root = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(3));
        await Save(Scan(), 1, 3, root, source);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "Stub Book", BookFiles.ChaptersFolderName), "0*.txt"))
        {
            File.Delete(file);
        }

        var again = await Save(Scan(), 1, 3, root, source);

        Assert.Equal(PipelineOutcome.Ok, again.Outcome);
        Assert.Empty(again.Missing);
        Assert.Equal(6, source.UrlsLoaded.Count);
    }

    [Fact]
    public async Task The_book_folder_deleted_while_saving_is_made_again_and_the_book_is_whole()
    {
        var root = Scratch.Directory();
        var source = new StubPageSource(Book.Pages(5));
        var book = Path.Combine(root, "Stub Book");

        // As in the log: the folder went to the Recycle Bin half way through,
        // and the next chapter's write failed with DirectoryNotFoundException.
        var observer = new DeletingObserver(book, afterRecords: 2);
        var result = await Save(Scan(), 1, 5, root, source, observer);

        Assert.True(observer.Deleted);
        Assert.Equal(PipelineOutcome.Ok, result.Outcome);
        Assert.Empty(result.Missing);
        var text = await File.ReadAllTextAsync(Assert.Single(result.Files), Token);
        Assert.All(Enumerable.Range(1, 5), n => Assert.Contains($"The body of chapter {n},", text, StringComparison.Ordinal));
    }

    private static ScanResult Scan() => new()
    {
        NovelUrl = Book.Toc,
        BookTitle = "Stub Book",
        Chapters = [.. Enumerable.Range(1, 6).Select(n => new ScannedChapter(n, $"Chapter {n}", $"https://e.com/ch/{n}"))],
        Layout = new ChapterLayout("h1", "article", "a[rel=next]", "a[rel=prev]"),
    };

    private static Task<RangeResult> Save(ScanResult scan, int from, int to, string root, StubPageSource source, IPipelineObserver? observer = null) =>
        RangePipeline.RunAsync(
            new RangeRequest
            {
                Scan = scan,
                Plan = RangePlanner.Plan(scan, from, to),
                From = from,
                To = to,
                OutputRoot = root,
                Fetch = new FetchOptions { Concurrency = 1, MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
            },
            source, Guard, RobotsPolicy.Missing("https://e.com"), new RateLimiter(TimeSpan.Zero), observer ?? new RecordingObserver(),
            cancellationToken: Token);

    private sealed class DeletingObserver(string folder, int afterRecords) : IPipelineObserver
    {
        private int records;

        public bool Deleted { get; private set; }

        public void Record(ChapterRecord record)
        {
            if (++this.records == afterRecords)
            {
                Directory.Delete(folder, recursive: true);
                this.Deleted = true;
            }
        }
    }
}

public sealed class MergedChapterTests
{
    [Fact]
    public void A_page_holding_two_chapters_covers_both_and_the_book_has_no_gap()
    {
        // novelfire's "Chapter 31/32" page: the site's chapter-32 address
        // then held chapter 33, and the book stopped at 31 with 18 saved
        // chapters left out.
        SortedDictionary<int, SavedChapter> saved = new()
        {
            [30] = new(30, "Chapter 30: Reunion", "Text."),
            [31] = new(31, "Chapter 31/32: Passing the Imperial Examination", "Chapter 31: The Cultivation Technique Acquired\nText.\nChapter 32: Passing the Imperial Examination, Top Scholar\nText."),
            [33] = new(33, "Chapter 33 Encounter on the Road", "Text."),
            [34] = new(34, "Chapter 34: Discussing the National Situation", "Text."),
        };

        Assert.Equal(32, BookFiles.LastCovered(saved[31]));
        Assert.Equal([30, 31, 32, 33, 34], BookFiles.Covered(saved).Order());
        Assert.Equal([30, 31, 33, 34], BookFiles.FirstRun(saved).Keys);
    }

    [Theory]
    [InlineData("Chapter 31/32: Two in One", "Text.", 32)]
    [InlineData("Chapters 31-33", "Text.", 33)]
    [InlineData("Chapter 31 and 32", "Text.", 32)]
    [InlineData("Chapter 31: One", "Chapter 31: One\nText.\nChapter 32: Two\nText.", 32)]
    [InlineData("Chapter 31: One", "He read chapter 200 of the manual.\nChapter 90: notes", 31)]
    [InlineData("Chapter 31: One", "Text.\nChapter 33: Skipped one\nText.", 31)]
    [InlineData("Chapter 31/40", "Text.", 31)]
    public void Only_the_next_few_chapters_named_in_order_count_as_covered(string heading, string body, int last) =>
        Assert.Equal(last, BookFiles.LastCovered(new SavedChapter(31, heading, body)));
}

public sealed class ChapterHeadingTests
{
    [Theory]
    [InlineData("Chapter 11: Lu Yun&#039;s crazy outburst!", "Text.", "Chapter 11: Lu Yun's crazy outburst!")]
    [InlineData("Chapter 15: &quot;Star Transformation&quot;!", "Text.", "Chapter 15: \"Star Transformation\"!")]
    [InlineData("After chatting for a few minutes, Xu Ning took his leave", "Chapter 34: Discussing the National Situation\nBefore Xu Ning could finish.", "Chapter 34: Discussing the National Situation")]
    [InlineData("Prologue", "The rain had not stopped for days.", "Prologue")]
    [InlineData("Chapter 5: Five", "Chapter 6: Not this one\nText.", "Chapter 5: Five")]
    public void Headings_are_readable_and_name_their_chapter(string title, string body, string expected) =>
        Assert.Equal(expected, ChapterTitles.Tidy(title, body));
}

public sealed class SameNovelTests
{
    [Theory]
    [InlineData("https://novelfire.net/book/x", "https://novelfire.net/book/x/chapters?page=&sort_by=", true)]
    [InlineData("https://novelfire.net/book/x", "https://www.novelfire.net/book/x/", true)]
    [InlineData("https://wtr-lab.com/en/novel/1/x?tab=toc", "https://wtr-lab.com/en/novel/1/x", true)]
    [InlineData("https://novelfire.net/book/x", "https://novelfire.net/book/y", false)]
    [InlineData("https://site.example/novel.php?id=1", "https://site.example/novel.php?id=2", false)]
    [InlineData("https://novelfire.net/book/x", "https://pandanovel.co/book/x", false)]
    public void One_book_however_it_was_opened(string a, string b, bool same) =>
        Assert.Equal(same, SharedBook.SameNovel(a, b));
}

public sealed class MovedBookTests
{
    [Theory]
    [InlineData("https://pandanovel.co/book/will-of-vienna", "https://novelfire.net/book/will-of-vienna", "https://novelfire.net/book/will-of-vienna")]
    [InlineData("https://novelfire.net/book/x", "https://www.novelfire.net/book/x", null)]
    [InlineData("https://novelfire.net/book/x", "https://novelfire.net/book/x/chapters", null)]
    [InlineData("https://novelfire.net/book/x", "", null)]
    public void A_novel_page_on_another_site_is_noticed(string asked, string reached, string? moved) =>
        Assert.Equal(moved, NovelScanner.MovedSite(asked, reached));
}
