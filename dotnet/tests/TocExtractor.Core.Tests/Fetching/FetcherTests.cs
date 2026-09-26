using TocExtractor.Core.Fetching;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;
using TocExtractor.Core.Tests.Politeness;

namespace TocExtractor.Core.Tests.Fetching;

/// <summary>
/// A guard configured with the one exemption it understands, so fixture URLs
/// need no DNS.
/// </summary>
/// <remarks>
/// Not a subclass: UrlGuard is sealed, so the only way to loosen it is the
/// all-or-nothing switch a user could also pass. Python has to subclass in the
/// test module to reach the same place, and a test asserts that stays the only
/// route; sealing makes the question moot.
/// </remarks>
internal static class PermissiveGuard
{
    internal static UrlGuard Instance { get; } =
        new(allowPrivateHosts: true, resolver: new FixedResolver());
}

public sealed class FetcherTests
{
    private const string Toc = "https://e.com/toc";

    private static readonly SelectorSet Selectors = SelectorSet.Create("a.ch", "h1", "article");

    private static Dictionary<string, StubPage> Book(int chapters, string host = "https://e.com")
    {
        Dictionary<string, StubPage> pages = new(StringComparer.Ordinal)
        {
            [Toc] = new StubPage
            {
                Links = [.. Enumerable.Range(1, chapters).Select(i => (object?)$"{host}/ch/{i}")],
            },
        };

        foreach (var i in Enumerable.Range(1, chapters))
        {
            pages[$"{host}/ch/{i}"] = new StubPage { Title = $"Chapter {i}", Body = $"Body {i}." };
        }

        return pages;
    }

    private static (Fetcher Fetcher, NullSink Sink, FakeClock Clock) Build(
        IReadOnlyDictionary<string, StubPage> pages,
        FetchOptions? options = null,
        StubPageSource? source = null,
        Random? rng = null,
        RateLimiter? limiter = null,
        RobotsPolicy? robots = null,
        Action<FailedChapter>? onFailure = null)
    {
        var clock = new FakeClock();
        var opts = options ?? new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero };
        var stub = source ?? new StubPageSource(pages, clock.Read, maxConcurrent: opts.Concurrency);
        var sink = new NullSink();

        var fetcher = new Fetcher(
            stub,
            PermissiveGuard.Instance,
            sink,
            opts,
            limiter ?? new RateLimiter(opts.MinDelay, clock.Read, clock.SleepAsync),
            robots,
            now: () => DateTimeOffset.UnixEpoch,
            rng: rng,
            sleep: clock.SleepAsync,
            onFailure: onFailure);

        return (fetcher, sink, clock);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -- happy path ----------------------------------------------------------

    [Fact]
    public async Task Fetches_every_chapter_in_index_order()
    {
        var (fetcher, _, _) = Build(Book(3));
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal([1, 2, 3], result.Completed.Select(r => r.Index));
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], result.Completed.Select(r => r.Title));
    }

    [Fact]
    public async Task Result_accounts_for_every_kept_link()
    {
        var (fetcher, _, _) = Build(Book(4));
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.True(result.AccountsForEveryLink());
        Assert.Equal(4, result.Attempted);
    }

    /// <summary>The manifest needs these; plumbing them later means reopening this loop.</summary>
    [Fact]
    public async Task Stripped_url_counts_reach_the_sink()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage
        {
            Title = "One",
            Body = "See https://a.com and https://b.com for more.",
        };
        var (fetcher, sink, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal(2, result.TotalStrippedUrls);
        Assert.Equal(2, sink.Records[0].StrippedUrls);
    }

    [Fact]
    public async Task Non_string_links_are_rejected_not_fetched()
    {
        var pages = Book(1);
        pages[Toc] = new StubPage { Links = ["https://e.com/ch/1", new Dictionary<string, string>()] };
        var (fetcher, _, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Single(result.Completed);
        Assert.Equal(RejectionReason.NotAString, result.Collection.Rejected[0].Reason);
    }

    [Fact]
    public async Task Max_links_truncates_and_counts()
    {
        var (fetcher, _, _) = Build(
            Book(10), new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero, MaxLinks = 3 });
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal(3, result.Completed.Count);
        Assert.Equal(7, result.Collection.Truncated);
    }

    [Fact]
    public async Task Final_url_is_recorded_after_a_redirect()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { RedirectTo = "https://e.com/ch/1-final" };
        pages["https://e.com/ch/1-final"] = new StubPage { Title = "One", Body = "Body." };
        var (fetcher, _, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal("https://e.com/ch/1-final", result.Completed[0].FinalUrl);
        Assert.True(result.Completed[0].Redirected);
    }

    // -- retries -------------------------------------------------------------

    [Fact]
    public async Task Transient_failures_are_retried_then_succeed()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { Title = "One", Body = "Body.", FailTimes = 2 };
        var (fetcher, _, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Single(result.Completed);
        Assert.Equal(3, result.Completed[0].Attempts);
    }

    [Fact]
    public async Task Retries_are_bounded_and_the_failure_is_recorded()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { FailTimes = 99 };
        var (fetcher, _, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Empty(result.Completed);
        Assert.Equal(3, result.Failed[0].Attempts);
        Assert.Equal("timeout", result.Failed[0].Reason);
    }

    [Fact]
    public async Task Zero_retries_means_one_attempt()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { FailTimes = 99 };
        var (fetcher, _, _) = Build(
            pages, new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero, Retries = 0 });
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal(1, result.Failed[0].Attempts);
    }

    /// <summary>The page loaded fine; retrying spends attempts on a certainty.</summary>
    [Fact]
    public async Task Selector_not_found_is_not_retried()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { MissingSelector = true };
        var (fetcher, _, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal(1, result.Failed[0].Attempts);
        Assert.Equal("selector_not_found", result.Failed[0].Reason);
    }

    /// <summary>
    /// Once the selectors have found a chapter, a page without them is most
    /// likely a site's busy or error page, so it is tried once more.
    /// </summary>
    [Fact]
    public async Task A_page_without_its_story_is_tried_again_once_the_selectors_have_worked()
    {
        var pages = Book(3);
        pages["https://e.com/ch/3"] = new StubPage
        {
            Title = "Chapter 3",
            FailTimes = 1,
            Failure = static message => new SelectorNotFoundException(message),
        };
        var (fetcher, _, _) = Build(pages, new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero });
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Empty(result.Failed);
        Assert.Equal(3, result.Completed.Count);
        Assert.Equal(2, result.Completed.Single(record => record.Index == 3).Attempts);
    }

    [Fact]
    public async Task A_page_that_never_has_its_story_is_tried_only_once_more()
    {
        var pages = Book(3);
        pages["https://e.com/ch/3"] = new StubPage { MissingSelector = true };
        var (fetcher, _, _) = Build(pages, new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero });
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        var failed = Assert.Single(result.Failed);
        Assert.Equal(2, failed.Attempts);
        Assert.Equal("selector_not_found", failed.Reason);
    }

    [Fact]
    public async Task One_chapter_failing_does_not_stop_the_others()
    {
        var pages = Book(3);
        pages["https://e.com/ch/2"] = new StubPage { FailTimes = 99 };
        var (fetcher, _, _) = Build(pages);
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal([1, 3], result.Completed.Select(r => r.Index));
        Assert.Equal([2], result.Failed.Select(f => f.Index));
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public async Task Backoff_is_jittered_within_the_ceiling()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { Title = "One", Body = "B", FailTimes = 2 };
        var clock = new FakeClock();
        var stub = new StubPageSource(pages, clock.Read, maxConcurrent: 1);
        var options = new FetchOptions
        {
            Concurrency = 1,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(4),
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options,
            new RateLimiter(TimeSpan.Zero, clock.Read, clock.SleepAsync),
            rng: new Random(1), sleep: clock.SleepAsync);

        await fetcher.RunAsync(Toc, Selectors, Token);

        // Two backoffs, ceilings of 1s and 2s; full jitter keeps each within its own.
        var backoffs = clock.Sleeps;
        Assert.Equal(2, backoffs.Count);
        Assert.InRange(backoffs[0], TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.InRange(backoffs[1], TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    // -- politeness composition ----------------------------------------------

    /// <summary>
    /// The property the whole design exists to guarantee. Several workers share
    /// one host; the per-host interval must survive the concurrency.
    /// </summary>
    [Fact]
    public async Task Concurrency_does_not_defeat_the_per_host_interval()
    {
        var clock = new FakeClock();
        var pages = Book(5);
        var stub = new StubPageSource(pages, clock.Read, maxConcurrent: 5);
        var options = new FetchOptions
        {
            Concurrency = 5,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.FromSeconds(2),
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options,
            new RateLimiter(TimeSpan.FromSeconds(2), clock.Read, clock.SleepAsync),
            sleep: clock.SleepAsync);

        await fetcher.RunAsync(Toc, Selectors, Token);

        var chapterLoads = stub.Loads.Where(entry => entry.Url != Toc).Select(entry => entry.When);
        foreach (var gap in Observed.Intervals(chapterLoads))
        {
            Assert.True(gap >= TimeSpan.FromSeconds(2), $"gap of {gap} is shorter than the interval");
        }
    }

    [Fact]
    public async Task Separate_hosts_are_not_serialised_against_each_other()
    {
        var clock = new FakeClock();
        Dictionary<string, StubPage> pages = new(StringComparer.Ordinal)
        {
            [Toc] = new StubPage { Links = ["https://a.com/ch/1", "https://b.com/ch/1"] },
            ["https://a.com/ch/1"] = new StubPage { Title = "A", Body = "a" },
            ["https://b.com/ch/1"] = new StubPage { Title = "B", Body = "b" },
        };
        var stub = new StubPageSource(pages, clock.Read, maxConcurrent: 2);
        var options = new FetchOptions
        {
            Concurrency = 2,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.FromSeconds(10),
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options,
            new RateLimiter(TimeSpan.FromSeconds(10), clock.Read, clock.SleepAsync),
            sleep: clock.SleepAsync);

        await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Empty(clock.Sleeps);
    }

    /// <summary>Concurrency is a ceiling on simultaneous loads, not a target.</summary>
    /// <remarks>
    /// Real delays, so the workers genuinely overlap. With instant pages the
    /// tasks complete one at a time and the stub never sees more than one load
    /// in flight, which makes the assertion true for the wrong reason — the
    /// first version of this test passed with the semaphore removed entirely.
    /// </remarks>
    [Fact]
    public async Task Semaphore_bounds_in_flight_work()
    {
        var pages = Book(8);
        foreach (var i in Enumerable.Range(1, 8))
        {
            pages[$"https://e.com/ch/{i}"] = new StubPage
            {
                Title = $"Chapter {i}",
                Body = "b",
                Hang = TimeSpan.FromMilliseconds(40),
            };
        }

        var stub = new StubPageSource(pages, maxConcurrent: 2);
        var options = new FetchOptions
        {
            Concurrency = 2,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.Zero,
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options, new RateLimiter(TimeSpan.Zero));

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal(8, result.Completed.Count);
        Assert.True(stub.MaxObservedConcurrency <= 2, $"saw {stub.MaxObservedConcurrency} in flight");
        Assert.True(
            stub.MaxObservedConcurrency >= 2,
            "the workers never overlapped, so this test proves nothing");
    }

    // -- the budget ----------------------------------------------------------

    /// <summary>Real seconds, deliberately: the budget is enforced against the real clock.</summary>
    [Fact]
    public async Task Budget_covers_a_page_that_never_settles()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { Hang = TimeSpan.FromSeconds(30) };
        var stub = new StubPageSource(pages, maxConcurrent: 1);
        var options = new FetchOptions
        {
            Concurrency = 1,
            Retries = 0,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.Zero,
            PageBudget = TimeSpan.FromMilliseconds(150),
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options,
            new RateLimiter(TimeSpan.Zero));

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal("timeout", result.Failed[0].Reason);
    }

    /// <summary>
    /// The budget is the whole operation's deadline, and the page source gets it
    /// as a token rather than inventing its own. A source that honours the token
    /// sees it fire; one that ignored it would overrun.
    /// </summary>
    [Fact]
    public async Task Budget_reaches_the_page_source_as_a_token()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage { Hang = TimeSpan.FromSeconds(30) };
        var stub = new StubPageSource(pages, maxConcurrent: 1);
        var options = new FetchOptions
        {
            Concurrency = 1,
            Retries = 0,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.Zero,
            PageBudget = TimeSpan.FromMilliseconds(150),
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options, new RateLimiter(TimeSpan.Zero));

        var started = DateTimeOffset.UtcNow;
        await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.True(
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5),
            "the 30s hang was not cut short by the budget");
    }

    // -- cancellation --------------------------------------------------------

    [Fact]
    public async Task Cancellation_mid_run_propagates()
    {
        var pages = Book(4);
        foreach (var i in Enumerable.Range(1, 4))
        {
            pages[$"https://e.com/ch/{i}"] = new StubPage { Hang = TimeSpan.FromSeconds(5) };
        }

        var stub = new StubPageSource(pages, maxConcurrent: 1);
        var options = new FetchOptions
        {
            Concurrency = 1,
            WaitAfterLoad = TimeSpan.Zero,
            MinDelay = TimeSpan.Zero,
            PageBudget = TimeSpan.FromSeconds(30),
        };
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), options, new RateLimiter(TimeSpan.Zero));

        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.RunAsync(Toc, Selectors, stopping.Token));
    }

    /// <summary>
    /// A source that raises cancellation without being asked must still leave a
    /// record. Absorbed silently, the chapter would vanish from the accounting:
    /// kept=1, completed=0, failed=0.
    /// </summary>
    [Fact]
    public async Task Source_cancelling_of_its_own_accord_is_recorded_as_a_failure()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage
        {
            FailTimes = 99,
            Failure = _ => throw new OperationCanceledException("the source gave up"),
        };
        var (fetcher, _, _) = Build(
            pages, new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero, Retries = 0 });
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Single(result.Failed);
        Assert.True(result.AccountsForEveryLink());
    }

    // -- dry run, resume, robots ---------------------------------------------

    [Fact]
    public async Task Dry_run_fetches_nothing_and_writes_nothing()
    {
        var stub = new StubPageSource(Book(3), maxConcurrent: 1);
        var sink = new NullSink();
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, sink,
            new FetchOptions { DryRun = true, Concurrency = 1 }, new RateLimiter(TimeSpan.Zero));

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Empty(result.Completed);
        Assert.Empty(sink.Records);
        Assert.Equal([Toc], stub.UrlsLoaded);
        Assert.Equal(3, result.Collection.Kept.Count);
    }

    [Fact]
    public async Task Already_done_urls_are_skipped_not_refetched()
    {
        var stub = new StubPageSource(Book(3), maxConcurrent: 1);
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(),
            new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero },
            new RateLimiter(TimeSpan.Zero),
            alreadyDone: url => url == "https://e.com/ch/2");

        var collected = await fetcher.CollectAsync(Toc, Selectors, Token);
        var result = await fetcher.FetchAsync(collected, Selectors, cancellationToken: Token);

        Assert.Equal(["https://e.com/ch/2"], result.SkippedResumed);
        Assert.DoesNotContain("https://e.com/ch/2", stub.UrlsLoaded);
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public async Task Robots_disallow_rejects_before_any_fetch()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /ch/2\n", "https://e.com");
        var stub = new StubPageSource(Book(3), maxConcurrent: 1);
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(),
            new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero },
            new RateLimiter(TimeSpan.Zero), policy);

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.DoesNotContain("https://e.com/ch/2", stub.UrlsLoaded);
        Assert.Equal(RejectionReason.RobotsDisallowed, result.Collection.Rejected[0].Reason);
    }

    [Fact]
    public async Task Page_error_from_the_toc_is_not_swallowed()
    {
        var stub = new StubPageSource(new Dictionary<string, StubPage>(StringComparer.Ordinal));
        using var fetcher = new Fetcher(
            stub, PermissiveGuard.Instance, new NullSink(), new FetchOptions(), new RateLimiter(TimeSpan.Zero));

        await Assert.ThrowsAsync<PageException>(() => fetcher.RunAsync(Toc, Selectors, Token));
    }

    /// <summary>
    /// Anything a page source throws must arrive as a page exception, or the
    /// retry rules stop being exhaustive and it escapes the run instead.
    /// </summary>
    [Fact]
    public async Task Foreign_exceptions_are_translated_at_the_boundary()
    {
        var pages = Book(1);
        pages["https://e.com/ch/1"] = new StubPage
        {
            FailTimes = 99,
            Failure = _ => throw new InvalidOperationException("something the loop has never heard of"),
        };
        var (fetcher, _, _) = Build(
            pages, new FetchOptions { Concurrency = 1, WaitAfterLoad = TimeSpan.Zero, Retries = 0 });
        using var _guard = fetcher;

        var result = await fetcher.RunAsync(Toc, Selectors, Token);

        Assert.Equal("error", result.Failed[0].Reason);
        Assert.Contains("InvalidOperationException", result.Failed[0].Detail, StringComparison.Ordinal);
    }
}
