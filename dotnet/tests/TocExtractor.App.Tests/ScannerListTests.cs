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
    public async Task Titles_that_fall_behind_their_addresses_are_numbered_by_place_and_named_briefly()
    {
        // Novelfire: "chapter-196" is "Chapter 195 (Part 2)", and every later
        // title runs a few behind its address, to "chapter-938" titled 934.
        const string Book = "https://e.com/book/c";
        static int Titled(int n) => n <= 195 ? n : n <= 197 ? 195 : n - 3;
        var site = new ScriptedSite(url => url == Book
            ? new { key = "k", chapters = Enumerable.Range(1, 300).Select(n => new { url = $"{Book}/chapter-{n}", number = Titled(n), title = $"Chapter {Titled(n)}", key = "k" }) }
            : null);

        var scan = await Scanner(site).ScanAsync(Book, Token);

        Assert.Equal(Enumerable.Range(1, 300), scan.Chapters.Select(c => c.Number));
        Assert.All(scan.Chapters, c => Assert.EndsWith("/chapter-" + c.Number.ToString(CultureInfo.InvariantCulture), c.Url, StringComparison.Ordinal));
        var note = Assert.Single(scan.Notes, n => n.StartsWith("Some chapters repeat", StringComparison.Ordinal));
        Assert.EndsWith("and 102 more.", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_that_stops_early_still_reaches_the_count_the_site_states()
    {
        // 2.6.7 on novelfire: the third list page counted no chapters, paging
        // stopped, and a book the site counts as 938 chapters scanned as 195.
        const string Book = "https://e.com/book/d";
        const string List = Book + "/chapters";
        var site = new ScriptedSite(url =>
            url == Book ? new { key = "k", total = 938, chapters = new[] { Chapter(Book + "/chapter-1", 1) }, lists = new[] { new { url = List, text = "Chapters" } } }
            : url == List ? new
            {
                key = "k",
                total = 938,
                chapters = Enumerable.Range(1, 195).Select(n => Chapter($"{Book}/chapter-{n}", n)),
                pages = new[] { new { url = List + "?page=3", text = "3", pattern = List + "?page=#" }, new { url = List + "?page=4", text = "4", pattern = List + "?page=#" } },
            }
            : null);

        var scan = await Scanner(site).ScanAsync(Book, Token);

        Assert.Null(scan.Problem);
        Assert.Equal(938, scan.LastNumber);
        Assert.Equal(Book + "/chapter-938", scan.Chapters[^1].Url);
        Assert.Contains(scan.Notes, n => n.StartsWith("The site counts 938 chapters and its list shows 195", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_short_scan_the_rest_of_cannot_be_built_says_chapters_are_missing()
    {
        // Addresses that do not follow the number: the rest cannot be built,
        // so the scan must not pass for the whole book.
        const string Book = "https://e.com/book/e";
        var site = new ScriptedSite(url => url == Book
            ? new { key = "k", total = 500, chapters = Enumerable.Range(1, 40).Select(n => Chapter($"{Book}/chapter-{n}-{(n * 7919) % 1000}x", n)) }
            : null);

        var scan = await Scanner(site).ScanAsync(Book, Token);

        Assert.Equal(40, scan.Chapters.Count);
        Assert.Contains(scan.Notes, n => n.StartsWith("The site counts 500 chapters, but the scan found 40", StringComparison.Ordinal));
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

    /// <summary>A site that answers "busy" to the pages it is told to, a set number of times each.</summary>
    private sealed class BusySite(IPageProbe inner, Func<string, int> busyTimes) : IPageProbe
    {
        private readonly Dictionary<string, int> refused = [];

        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default)
        {
            this.refused.TryGetValue(url, out var so);
            if (so < busyTimes(url))
            {
                this.refused[url] = so + 1;
                throw new PageException($"{url}: the site answered HTTP 429, busy");
            }

            return inner.ProbeAsync(url, script, settle, cancellationToken);
        }
    }

    private static NovelScanner NoWaitScanner(IPageProbe site, List<TimeSpan>? waits = null) => new(
        site, new UrlGuard(resolver: new PublicResolver()),
        RobotsPolicy.Missing("https://e.com"), new RateLimiter(TimeSpan.Zero))
    {
        Delay = (wait, _) =>
        {
            waits?.Add(wait);
            return Task.CompletedTask;
        },
    };

    private const string Paged = "https://e.com/book/r";
    private const string PagedList = Paged + "/chapters";

    private static ScriptedSite PagedBook(int pages) => new(url =>
    {
        static string PageUrl(int p) => PagedList + "?page=" + p.ToString(CultureInfo.InvariantCulture);
        object ListPage(int p) => new
        {
            key = "k",
            chapters = Enumerable.Range(((p - 1) * 100) + 1, 100).Select(n => Chapter($"{Paged}/chapter-{n}", n)),
            pages = Enumerable.Range(Math.Max(1, p - 2), 5).Where(q => q <= pages)
                .Select(q => new { url = PageUrl(q), text = q.ToString(CultureInfo.InvariantCulture), pattern = PagedList + "?page=#" }),
        };

        return url == Paged ? new { key = "k", chapters = new[] { Chapter(Paged + "/chapter-1", 1) }, lists = new[] { new { url = PagedList, text = "Chapters" } } }
            : url == PagedList ? ListPage(1)
            : url.StartsWith(PagedList + "?page=", StringComparison.Ordinal) ? ListPage(int.Parse(url[(PagedList.Length + 6)..], CultureInfo.InvariantCulture))
            : null;
    });

    [Fact]
    public async Task A_list_page_the_site_was_busy_for_is_tried_again_and_the_whole_list_read()
    {
        // 2.6.9 on novelfire, Reborn As Noble: list page 13 of 17 answered 429,
        // was read as a page with no chapters, and paging stopped there.
        var waits = new List<TimeSpan>();
        var site = new BusySite(PagedBook(17), url => url.EndsWith("?page=13", StringComparison.Ordinal) ? 2 : 0);

        var scan = await NoWaitScanner(site, waits).ScanAsync(Paged, Token);

        Assert.Null(scan.Problem);
        Assert.Equal(Enumerable.Range(1, 1700), scan.Chapters.Select(c => c.Number));
        Assert.Equal([NovelScanner.BusyWait(1), NovelScanner.BusyWait(2)], waits);
    }

    [Fact]
    public async Task A_sample_chapter_the_site_stays_busy_for_says_busy_not_unsupported()
    {
        // The sample chapter answered 429 on every try, and the scan said
        // "This site may not be supported yet" about a site it supports.
        var site = new BusySite(PagedBook(4), url => url.Contains("/chapter-", StringComparison.Ordinal) ? int.MaxValue : 0);

        var scan = await NoWaitScanner(site).ScanAsync(Paged, Token);

        Assert.NotNull(scan.Problem);
        Assert.StartsWith("The site is busy", scan.Problem, StringComparison.Ordinal);
        Assert.Contains("chapter 201", scan.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("not be supported", scan.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sample_chapter_busy_once_is_read_on_the_next_try()
    {
        var site = new BusySite(PagedBook(4), url => url.Contains("/chapter-", StringComparison.Ordinal) ? 1 : 0);

        var scan = await NoWaitScanner(site).ScanAsync(Paged, Token);

        Assert.Null(scan.Problem);
        Assert.Equal(400, scan.Chapters.Count);
        Assert.NotNull(scan.Layout);
    }

    [Theory]
    [InlineData("https://e.com/a: the site answered HTTP 429, busy", true)]
    [InlineData("https://e.com/a: the site answered HTTP 503, busy", true)]
    [InlineData("https://e.com/a: read ECONNRESET", false)]
    [InlineData("https://e.com/a: the site has no page here (HTTP 404)", false)]
    public void Only_a_busy_answer_counts_as_busy(string message, bool busy)
    {
        Assert.Equal(busy, NovelScanner.IsBusy(new PageException(message)));
        Assert.True(NovelScanner.IsTransient(new PageException(message)));
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
