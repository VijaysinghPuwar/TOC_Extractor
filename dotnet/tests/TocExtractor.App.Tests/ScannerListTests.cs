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

    private static ScriptedSite TitledBook(string book, int count, Func<int, int> titled, Func<int, bool>? listed = null) =>
        new(url => url == book
            ? new
            {
                key = "k",
                chapters = Enumerable.Range(1, count).Where(n => listed?.Invoke(n) ?? true)
                    .Select(n => new { url = $"{book}/chapter-{n}", number = titled(n), title = $"Chapter {titled(n)}", key = "k" }),
            }
            : null);

    [Fact]
    public async Task A_title_one_ahead_of_the_next_address_marks_a_number_the_site_skips()
    {
        // 2.6.10 on novelfire, Bringing The Farm To Live In Another World:
        // "chapter-283" is "Chapter 284 - Seeing West Wonder King", then
        // "chapter-284" is "Chapter 284 - Special Requests". No page is
        // chapter 283; the save walked to find it and said "Missing: 283",
        // and the book file stopped at 282, on every Save.
        const string Book = "https://e.com/book/farm";
        var scan = await Scanner(TitledBook(Book, 300, n => n == 283 ? 284 : n)).ScanAsync(Book, Token);

        Assert.Null(scan.Problem);
        Assert.Equal([283], scan.Skipped);
        Assert.Equal(282, scan.Chapters.Single(c => c.Url == Book + "/chapter-282").Number);
        Assert.Equal(284, scan.Chapters.Single(c => c.Url == Book + "/chapter-283").Number);

        // The chapters after keep the numbers 2.6.10 gave them, so the
        // ranges already saved from this book still join up.
        Assert.Equal(285, scan.Chapters.Single(c => c.Url == Book + "/chapter-284").Number);
        Assert.Equal(301, scan.LastNumber);
        Assert.Contains(scan.Notes, n => n.StartsWith("The site's titles skip chapter(s) 283:", StringComparison.Ordinal));

        var plan = RangePlanner.Plan(scan, 251, 300);
        Assert.Empty(plan.Walks);
        Assert.Null(plan.Problem);
        Assert.Equal(49, plan.Requested);
        Assert.DoesNotContain(plan.Direct, link => link.Number == 283);
    }

    [Fact]
    public async Task Titles_that_run_one_ahead_from_some_chapter_on_skip_one_number_only()
    {
        // AnimeMan: "chapter-364" is "Chapter : 365", and every title after
        // stays one ahead of its address.
        const string Book = "https://e.com/book/anime";
        var scan = await Scanner(TitledBook(Book, 500, n => n < 364 ? n : n + 1)).ScanAsync(Book, Token);

        Assert.Equal([364], scan.Skipped);
        Assert.Equal(Enumerable.Range(1, 363).Concat(Enumerable.Range(365, 137)), scan.Chapters.Select(c => c.Number));
    }

    [Fact]
    public async Task A_chapter_the_list_leaves_out_is_not_taken_for_a_skipped_number()
    {
        // The site has "chapter-283" but its list does not show it: the
        // addresses either side are not neighbours, so 283 is a real
        // chapter, still to be reached by following links.
        const string Book = "https://e.com/book/gap";
        var scan = await Scanner(TitledBook(Book, 300, n => n, listed: n => n != 283)).ScanAsync(Book, Token);

        Assert.Empty(scan.Skipped);
        var plan = RangePlanner.Plan(scan, 251, 300, predict: false);
        Assert.Single(plan.Walks);
        Assert.Equal(50, plan.Requested);
    }

    [Fact]
    public async Task A_title_far_ahead_of_the_next_address_is_not_taken_for_a_slip()
    {
        // Ten numbers on from its neighbour: more than a typo, so nothing is
        // assumed about the numbers between.
        const string Book = "https://e.com/book/far";
        var scan = await Scanner(TitledBook(Book, 300, n => n < 100 ? n : n + 10)).ScanAsync(Book, Token);

        Assert.Empty(scan.Skipped);
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
    public async Task A_novel_page_busy_for_most_of_a_minute_is_still_read()
    {
        // 2.6.10 on novelfire, four times in two weeks: the novel page
        // answered 429 three times in 26 seconds and the scan ended "The
        // novel page could not be read", while the site saved other books.
        var waits = new List<TimeSpan>();
        var site = new BusySite(PagedBook(3), url => url == Paged ? NovelScanner.BusyAttempts - 1 : 0);

        var scan = await NoWaitScanner(site, waits).ScanAsync(Paged, Token);

        Assert.Null(scan.Problem);
        Assert.Equal(300, scan.Chapters.Count);
        Assert.Equal(Enumerable.Range(1, NovelScanner.BusyAttempts - 1).Select(NovelScanner.BusyWait), waits);
        Assert.True(waits.Aggregate(TimeSpan.Zero, (a, b) => a + b) >= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task A_novel_page_that_stays_busy_says_the_site_is_busy()
    {
        var site = new BusySite(PagedBook(3), url => url == Paged ? int.MaxValue : 0);

        var scan = await NoWaitScanner(site).ScanAsync(Paged, Token);

        Assert.Equal(NovelScanner.NovelPageBusy, scan.Problem);
        Assert.Empty(scan.Chapters);
    }

    [Fact]
    public async Task A_novel_page_that_will_not_load_is_still_tried_only_three_times()
    {
        // A dropped connection is not a busy site: three tries, as before.
        var tries = 0;
        var site = new FailingSite(() => tries++);

        var scan = await NoWaitScanner(site).ScanAsync(Paged, Token);

        Assert.Equal(NovelScanner.LoadAttempts, tries);
        Assert.Equal("The novel page could not be read. See the activity log for why.", scan.Problem);
    }

    private sealed class FailingSite(Action counted) : IPageProbe
    {
        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default)
        {
            counted();
            throw new PageException($"{url}: read ECONNRESET");
        }
    }

    [Fact]
    public async Task A_sample_chapter_that_stays_busy_is_not_asked_for_again_after_its_tries()
    {
        var asks = 0;
        var busy = new BusySite(PagedBook(4), url => url.Contains("/chapter-", StringComparison.Ordinal) ? int.MaxValue : 0);
        var counting = new CountingProbe(busy, url =>
        {
            if (url.Contains("/chapter-", StringComparison.Ordinal))
            {
                asks++;
            }
        });

        var scan = await NoWaitScanner(counting).ScanAsync(Paged, Token);

        Assert.StartsWith("The site is busy", scan.Problem, StringComparison.Ordinal);
        Assert.Equal(NovelScanner.BusyAttempts, asks);
    }

    private sealed class CountingProbe(IPageProbe inner, Action<string> asked) : IPageProbe
    {
        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default)
        {
            asked(url);
            return inner.ProbeAsync(url, script, settle, cancellationToken);
        }
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
    public async Task Another_extraction_of_the_same_book_waits_for_a_scan_that_is_running()
    {
        await using var host = new BrowserHost(new NovelEnvironment
        {
            StartSource = (_, _, _) => Task.FromResult<IPageSource>(new StubPageSource(Book.Pages(1))),
            FetchRobots = (_, _) => null,
            BrowserProfileDirectory = Scratch.Directory(),
            Resolver = new PublicResolver(),
        });
        const string Url = "https://e.com/book/a";
        object first = new(), second = new();
        var done = new TaskCompletionSource<ScanResult>();

        Assert.Null(host.JoinOrStartScan(Url, false, first, done.Task));
        Assert.Same(done.Task, host.JoinOrStartScan(Url, false, second, Task.FromResult(new ScanResult { NovelUrl = Url })));

        // Not for its own scan, not signed in differently, not another book.
        Assert.Same(done.Task, host.RunningScan(Url, false, second));
        Assert.Null(host.RunningScan(Url, false, first));
        Assert.Null(host.RunningScan(Url, true, second));
        Assert.Null(host.RunningScan(Url + "-2", false, second));

        // Only its owner ends it, and an ended scan is not waited for.
        host.EndScan(Url, false, second);
        Assert.NotNull(host.RunningScan(Url, false, second));
        done.SetResult(new ScanResult { NovelUrl = Url });
        Assert.Null(host.RunningScan(Url, false, second));
        host.EndScan(Url, false, first);
        var mine = new TaskCompletionSource<ScanResult>();
        Assert.Null(host.JoinOrStartScan(Url, false, second, mine.Task));
        Assert.Same(mine.Task, host.RunningScan(Url, false, first));
    }

    [Fact]
    public async Task Of_many_extractions_asking_at_once_only_one_scans_and_the_rest_wait_for_it()
    {
        // Opened together, they all waited for the browser to start and then
        // asked in the same instant: 2.6.11's first try let both scan.
        await using var host = new BrowserHost(new NovelEnvironment
        {
            StartSource = (_, _, _) => Task.FromResult<IPageSource>(new StubPageSource(Book.Pages(1))),
            FetchRobots = (_, _) => null,
            BrowserProfileDirectory = Scratch.Directory(),
            Resolver = new PublicResolver(),
        });
        const string Url = "https://e.com/book/a";
        // Threads of their own, held at one gate and let go together.
        using var gate = new ManualResetEventSlim();
        var answers = new Task<ScanResult>?[16];
        var threads = Enumerable.Range(0, answers.Length).Select(i => new Thread(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            answers[i] = host.JoinOrStartScan(Url, false, new object(), new TaskCompletionSource<ScanResult>().Task);
        })).ToList();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        gate.Set();
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }

        // One started; every other was told to wait, all for that one scan.
        Assert.Equal(1, answers.Count(answer => answer is null));
        var waitedFor = answers.Where(answer => answer is not null).Select(answer => answer!.Id).Distinct().ToList();
        Assert.Single(waitedFor);
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
