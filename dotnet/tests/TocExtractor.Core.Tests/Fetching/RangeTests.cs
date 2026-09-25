using System.Text.RegularExpressions;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Links;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;

namespace TocExtractor.Core.Tests.Fetching;

/// <summary>Chapter ranges: known links fetched directly, gaps reached by walking.</summary>
public sealed partial class RangeTests
{
    private const string Book = "https://e.com/book";

    private static readonly SelectorSet Selectors = SelectorSet.Create("a", "h1", "article");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Chapters 1 to <paramref name="count"/>, each linking to its neighbours.</summary>
    private static Dictionary<string, StubPage> Chain(int count, Func<int, int>? titleNumber = null)
    {
        Dictionary<string, StubPage> pages = new(StringComparer.Ordinal);
        for (var i = 1; i <= count; i++)
        {
            pages[Url(i)] = new StubPage
            {
                Title = $"Chapter {(titleNumber ?? (n => n))(i)}",
                Body = $"Body {i}.",
                Next = i < count ? Url(i + 1) : null,
                Prev = i > 1 ? Url(i - 1) : null,
            };
        }

        return pages;
    }

    private static string Url(int n) => $"https://e.com/book/c{n}";

    private static int? NumberOf(ChapterPage page)
    {
        var match = ChapterNumber().Match(page.Title);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static (Fetcher Fetcher, StubPageSource Source) Build(Dictionary<string, StubPage> pages)
    {
        var source = new StubPageSource(pages, maxConcurrent: 3);
        var fetcher = new Fetcher(
            source,
            PermissiveGuard.Instance,
            new NullSink(),
            new FetchOptions { Concurrency = 2, MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
            new RateLimiter(TimeSpan.Zero));
        return (fetcher, source);
    }

    [Fact]
    public async Task Known_links_are_saved_under_their_chapter_numbers()
    {
        var (fetcher, _) = Build(Chain(10));
        using var _ = fetcher;

        var result = await fetcher.FetchRangeAsync(
            Book, [new(7, Url(7)), new(8, Url(8))], [], Selectors, cancellationToken: Token);

        Assert.Equal([7, 8], result.Completed.Select(r => r.Index));
    }

    [Fact]
    public async Task A_gap_is_walked_backwards_from_the_nearest_known_chapter()
    {
        // Known: 8 to 10 only, as when a site's list page shows the newest.
        var (fetcher, source) = Build(Chain(10));
        using var _ = fetcher;
        var walk = new WalkSpec(Url(8), 8, "prev", -1, 3, 7, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([3, 4, 5, 6, 7], result.Completed.Select(r => r.Index));
        Assert.DoesNotContain(Url(2), source.UrlsLoaded);
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public async Task A_gap_is_walked_forwards_and_stops_at_the_end_of_the_range()
    {
        var (fetcher, source) = Build(Chain(10));
        using var _ = fetcher;
        var walk = new WalkSpec(Url(1), 1, "next", +1, 3, 5, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([3, 4, 5], result.Completed.Select(r => r.Index));
        Assert.DoesNotContain(Url(6), source.UrlsLoaded);
        Assert.Equal(5, source.UrlsLoaded.Count);
    }

    [Fact]
    public async Task Direct_and_walked_chapters_land_in_one_run_without_repeats()
    {
        var (fetcher, source) = Build(Chain(10));
        using var _ = fetcher;
        var walk = new WalkSpec(Url(8), 8, "prev", -1, 5, 9, 100);

        var result = await fetcher.FetchRangeAsync(
            Book, [new(8, Url(8)), new(9, Url(9))], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([5, 6, 7, 8, 9], result.Completed.Select(r => r.Index));
        Assert.Single(result.Completed, r => r.Index == 8);
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public async Task The_title_decides_the_number_when_the_site_skips_one()
    {
        // The site has no chapter 5: after 4 comes 6.
        var (fetcher, _) = Build(Chain(8, n => n >= 5 ? n + 1 : n));
        using var _ = fetcher;
        var walk = new WalkSpec(Url(3), 3, "next", +1, 3, 7, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([3, 4, 6, 7], result.Completed.Select(r => r.Index));
    }

    [Fact]
    public async Task A_page_with_no_link_onward_ends_the_walk()
    {
        var pages = Chain(6);
        pages[Url(4)] = new StubPage { Title = "Chapter 4", Body = "Body 4.", Next = null };
        var (fetcher, _) = Build(pages);
        using var _ = fetcher;
        var walk = new WalkSpec(Url(2), 2, "next", +1, 2, 6, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([2, 3, 4], result.Completed.Select(r => r.Index));
    }

    [Fact]
    public async Task Links_that_go_in_circles_end_the_walk()
    {
        var pages = Chain(3);
        pages[Url(3)] = new StubPage { Title = "Chapter 3", Body = "Body 3.", Next = Url(1) };
        var (fetcher, _) = Build(pages);
        using var _ = fetcher;
        var walk = new WalkSpec(Url(1), 1, "next", +1, 1, 50, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([1, 2, 3], result.Completed.Select(r => r.Index));
    }

    [Fact]
    public async Task A_failed_step_is_reported_and_ends_the_walk()
    {
        var pages = Chain(6);
        pages[Url(4)] = new StubPage { MissingSelector = true, Next = Url(5) };
        var (fetcher, _) = Build(pages);
        using var _ = fetcher;
        var walk = new WalkSpec(Url(2), 2, "next", +1, 2, 6, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([2, 3], result.Completed.Select(r => r.Index));
        Assert.Equal(4, Assert.Single(result.Failed).Index);
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public async Task A_walk_skips_what_an_earlier_run_saved_but_still_steps_through_it()
    {
        var (fetcher, source) = Build(Chain(6));
        using var _ = fetcher;
        var walk = new WalkSpec(Url(1), 1, "next", +1, 1, 4, 100);
        HashSet<string> done = [Url(1), Url(2)];

        var result = await fetcher.FetchRangeAsync(
            Book, [], [walk], Selectors, alreadyDone: done.Contains, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([3, 4], result.Completed.Select(r => r.Index));
        Assert.Equal([Url(1), Url(2)], result.SkippedResumed);
        Assert.True(result.AccountsForEveryLink());
        Assert.Contains(Url(1), source.UrlsLoaded);
    }

    [Fact]
    public async Task A_walk_stops_at_a_step_robots_txt_disallows()
    {
        var robots = RobotsPolicy.Parse("User-agent: *\nDisallow: /book/c4\n", "https://e.com");
        var source = new StubPageSource(Chain(6), maxConcurrent: 1);
        using var fetcher = new Fetcher(
            source, PermissiveGuard.Instance, new NullSink(),
            new FetchOptions { MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
            new RateLimiter(TimeSpan.Zero), robots);
        var walk = new WalkSpec(Url(2), 2, "next", +1, 2, 6, 100);

        var result = await fetcher.FetchRangeAsync(Book, [], [walk], Selectors, numberOf: NumberOf, cancellationToken: Token);

        Assert.Equal([2, 3], result.Completed.Select(r => r.Index));
        Assert.DoesNotContain(Url(4), source.UrlsLoaded);
        Assert.Contains(result.Collection.Rejected, r => r.Reason == RejectionReason.RobotsDisallowed);
    }

    [Fact]
    public async Task A_human_check_pauses_the_run_then_the_same_chapter_is_retried()
    {
        var pages = Chain(3);
        pages[Url(2)] = new StubPage
        {
            Title = "Chapter 2", Body = "Body 2.", Next = Url(3), FailTimes = 1,
            Failure = static message => new HumanCheckException(message),
        };
        var asked = 0;
        var source = new StubPageSource(pages, maxConcurrent: 1);
        using var fetcher = new Fetcher(
            source, PermissiveGuard.Instance, new NullSink(),
            new FetchOptions { Retries = 0, MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
            new RateLimiter(TimeSpan.Zero),
            onHumanCheck: (_, _) => { asked++; return Task.FromResult(true); });

        var result = await fetcher.FetchRangeAsync(
            Book, [new(1, Url(1)), new(2, Url(2)), new(3, Url(3))], [], Selectors, cancellationToken: Token);

        Assert.Equal(1, asked);
        Assert.Equal([1, 2, 3], result.Completed.Select(r => r.Index));
        Assert.Empty(result.Failed);
        Assert.Equal(1, result.Completed.Single(r => r.Index == 2).Attempts);
    }

    [Fact]
    public async Task A_check_nobody_passes_is_reported_with_its_reason()
    {
        var pages = Chain(2);
        pages[Url(2)] = new StubPage { FailTimes = 99, Failure = static message => new HumanCheckException(message) };
        var source = new StubPageSource(pages, maxConcurrent: 1);
        using var fetcher = new Fetcher(
            source, PermissiveGuard.Instance, new NullSink(),
            new FetchOptions { MinDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, WaitAfterLoad = TimeSpan.Zero },
            new RateLimiter(TimeSpan.Zero),
            onHumanCheck: (_, _) => Task.FromResult(false));

        var result = await fetcher.FetchRangeAsync(Book, [new(1, Url(1)), new(2, Url(2))], [], Selectors, cancellationToken: Token);

        Assert.Equal([1], result.Completed.Select(r => r.Index));
        Assert.Equal(2, Assert.Single(result.Failed).Index);
    }

    [GeneratedRegex(@"Chapter (\d+)")]
    private static partial Regex ChapterNumber();
}
