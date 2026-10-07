using System.Globalization;
using System.Text.Json;
using TocExtractor.App.Scanning;
using TocExtractor.App.Session;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

/// <summary>The scan's reading of chapter lists, on lists shaped like the sites that broke it.</summary>
public sealed class ScannerListTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A site of list pages and chapter pages, answered from code rather than a browser.</summary>
    private sealed class ScriptedSite(Func<string, object?> list) : IPageProbe
    {
        public List<string> Read { get; } = [];

        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default)
        {
            this.Read.Add(url);
            if (url.Contains("/chapter", StringComparison.Ordinal) && !url.Contains("/chapters", StringComparison.Ordinal))
            {
                return Task.FromResult((url, JsonSerializer.Serialize(new
                {
                    content = "#content",
                    contentChars = 5000,
                    title = "h4",
                    titleText = "Chapter 1",
                    next = "a.next",
                    prev = "a.prev",
                })));
            }

            var page = list(url) ?? new { chapters = Array.Empty<object>() };
            return Task.FromResult((url, JsonSerializer.Serialize(page)));
        }
    }

    private static NovelScanner Scanner(IPageProbe site) => new(
        site, new UrlGuard(resolver: new PublicResolver()),
        RobotsPolicy.Missing("https://e.com"), new RateLimiter(TimeSpan.Zero));

    private static object Chapter(string url, int n) => new { url, number = n, title = $"Chapter {n}", key = "k" };

    [Fact]
    public async Task Pages_a_pager_shows_only_later_are_read_before_its_repeat_of_page_one()
    {
        // Novelfire: the pager on page 1 shows "1 2 3 4 5 6 ... 14 15"; page 7
        // and on show up only on later pages. Read in the order found, page
        // 15 came first, then "?page=1" added nothing and ended paging at 772
        // of 1,472 chapters.
        const string Book = "https://e.com/book/a";
        const string List = Book + "/chapters";
        const int Pages = 15;
        static string PageUrl(int p) => List + "?page=" + p.ToString(CultureInfo.InvariantCulture);
        static object Pager(int at) => new[] { 1, 2, 3, 4, 5, 6, at - 2, at - 1, at + 1, at + 2, 14, 15 }
            .Where(p => p is >= 1 and <= Pages).Distinct()
            .Select(p => new { url = PageUrl(p), text = p.ToString(CultureInfo.InvariantCulture), pattern = List + "?page=#" });
        static object ListPage(int p) => new
        {
            key = "k",
            chapters = Enumerable.Range(((p - 1) * 100) + 1, p == Pages ? 50 : 100)
                .Select(n => Chapter($"{Book}/chapter-{n}", n)),
            pages = Pager(p),
        };

        var site = new ScriptedSite(url =>
            url == Book ? new { key = "k", chapters = new[] { Chapter(Book + "/chapter-1", 1) }, lists = new[] { new { url = List, text = "Chapters" } } }
            : url == List ? ListPage(1)
            : url.StartsWith(List + "?page=", StringComparison.Ordinal) ? ListPage(int.Parse(url[(List.Length + 6)..], CultureInfo.InvariantCulture))
            : null);

        var scan = await Scanner(site).ScanAsync(Book, Token);

        Assert.Null(scan.Problem);
        Assert.Equal(1450, scan.Chapters.Count);
        Assert.Equal(Enumerable.Range(1, 1450), scan.Chapters.Select(c => c.Number));
    }

    [Fact]
    public async Task A_list_showing_only_the_first_chapters_reaches_the_sites_own_count()
    {
        // Novellunar: "Chapters (1472)", fifty listed, the rest behind
        // buttons with no address; the addresses follow the number.
        const string Book = "https://e.com/novel/b";
        var site = new ScriptedSite(url => url == Book
            ? new { key = "k", total = 1472, chapters = Enumerable.Range(1, 50).Select(n => Chapter($"{Book}/chapter/{n}", n)) }
            : null);

        var scan = await Scanner(site).ScanAsync(Book, Token);

        Assert.Null(scan.Problem);
        Assert.Equal(1, scan.FirstNumber);
        Assert.Equal(1472, scan.LastNumber);
        Assert.Equal(Book + "/chapter/1472", scan.Chapters[^1].Url);
        Assert.Contains(scan.Notes, note => note.Contains("1,472", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_that_already_shows_nearly_every_chapter_is_not_stretched_to_the_count()
    {
        // 330 numbered chapters and a count of 334 that includes side stories.
        const string Book = "https://e.com/novel/c";
        var site = new ScriptedSite(url => url == Book
            ? new { key = "k", total = 334, chapters = Enumerable.Range(1, 330).Select(n => Chapter($"{Book}/chapter/{n}", n)) }
            : null);

        var scan = await Scanner(site).ScanAsync(Book, Token);

        Assert.Equal(330, scan.LastNumber);
    }

    [Fact]
    public async Task Another_extraction_of_the_same_book_borrows_a_recent_scan_but_a_rescan_is_fresh()
    {
        await using var host = new BrowserHost(new NovelEnvironment
        {
            StartSource = (_, _, _) => Task.FromResult<IPageSource>(new StubPageSource(Book.Pages(1))),
            FetchRobots = (_, _) => null,
            BrowserProfileDirectory = Scratch.Directory(),
            Resolver = new PublicResolver(),
        });
        var scan = new ScanResult
        {
            NovelUrl = "https://e.com/book/a",
            Chapters = [new ScannedChapter(1, "Chapter 1", "https://e.com/book/a/chapter-1")],
            Layout = new ChapterLayout("h4", "#content", null, null),
        };
        object first = new(), second = new();
        var now = DateTimeOffset.UtcNow;

        host.KeepScan(scan.NovelUrl, false, first, scan, now);

        Assert.Same(scan, host.RecentScan(scan.NovelUrl, false, second, now.AddMinutes(5)));
        Assert.Null(host.RecentScan(scan.NovelUrl, false, first, now.AddMinutes(5)));
        Assert.Null(host.RecentScan(scan.NovelUrl, true, second, now.AddMinutes(5)));
        Assert.Null(host.RecentScan(scan.NovelUrl, false, second, now + BrowserHost.ScanKeptFor));
    }
}
