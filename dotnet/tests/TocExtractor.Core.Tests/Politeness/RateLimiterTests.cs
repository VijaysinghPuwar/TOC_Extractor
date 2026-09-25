using System.Reflection;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

/// <summary>A clock that only advances when the paired sleep is awaited.</summary>
/// <remarks>
/// Locked because the fetch loop drives it from several tasks at once and .NET
/// continuations really do run in parallel. The Python equivalent needs no lock
/// only because its event loop is single-threaded; an unsynchronised port of it
/// is a flaky test rather than a failing one, which is worse.
/// </remarks>
internal sealed class FakeClock
{
    private readonly Lock gate = new();
    private TimeSpan now;
    private readonly List<TimeSpan> sleeps = [];

    internal TimeSpan Now
    {
        get { lock (this.gate) { return this.now; } }
    }

    internal IReadOnlyList<TimeSpan> Sleeps
    {
        get { lock (this.gate) { return [.. this.sleeps]; } }
    }

    internal TimeSpan Read() => this.Now;

    internal ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            this.sleeps.Add(duration);
            this.now += duration;
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class RateLimiterTests
{
    private static readonly Uri Example = new("https://example.com/ch/1");
    private static readonly Uri Other = new("https://other.example/ch/1");

    private static (RateLimiter Limiter, FakeClock Clock) Build(double minSeconds)
    {
        var clock = new FakeClock();
        return (
            new RateLimiter(TimeSpan.FromSeconds(minSeconds), clock.Read, clock.SleepAsync),
            clock);
    }

    [Fact]
    public async Task First_request_to_a_host_does_not_wait()
    {
        var (limiter, clock) = Build(2);

        Assert.Equal(TimeSpan.Zero, await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken));
        Assert.Empty(clock.Sleeps);
    }

    [Fact]
    public async Task Second_request_waits_the_full_interval()
    {
        var (limiter, _) = Build(2);

        await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(2), await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hosts_are_limited_independently()
    {
        var (limiter, _) = Build(2);

        await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.Zero, await limiter.AcquireAsync(Other, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The invariant the class exists for. Ten workers hit one host at once. If
    /// the lock were released before the sleep they would all read the same
    /// "last request" time and fire together, turning a 2s delay into 2s ÷ 10.
    /// Serialised, the tenth lands no earlier than 18s after the first.
    /// </summary>
    [Fact]
    public async Task Concurrency_cannot_shorten_the_interval()
    {
        var (limiter, clock) = Build(2);

        async Task<TimeSpan> Worker()
        {
            await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken);
            return clock.Now;
        }

        var stamps = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Worker()));

        Assert.Equal(TimeSpan.FromSeconds(18), clock.Now);
        var ordered = stamps.Order().ToArray();
        foreach (var (earlier, later) in ordered.Zip(ordered.Skip(1)))
        {
            Assert.True(later - earlier >= TimeSpan.FromSeconds(2), $"{earlier} -> {later}");
        }
    }

    [Fact]
    public async Task Crawl_delay_can_raise_the_interval()
    {
        var (limiter, _) = Build(1);
        limiter.SetHostInterval(Example, TimeSpan.FromSeconds(5));

        await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(5), await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken));
    }

    /// <summary>A site asking to be hit faster than the user chose does not get to.</summary>
    [Fact]
    public void Crawl_delay_cannot_lower_the_configured_interval()
    {
        var (limiter, _) = Build(5);

        limiter.SetHostInterval(Example, TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromSeconds(5), limiter.IntervalFor(Example));
    }

    [Fact]
    public async Task Zero_interval_never_sleeps()
    {
        var (limiter, clock) = Build(0);

        await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken);
        await limiter.AcquireAsync(Example, TestContext.Current.CancellationToken);

        Assert.Empty(clock.Sleeps);
    }

    // -- the host key both callers use ---------------------------------------

    /// <summary>
    /// Python stores the Crawl-delay override under the authority and looks it
    /// up under the lowercased host, so an uppercase host or an explicit port
    /// files it somewhere it is never read — while the log still reports that
    /// the delay is being honoured. Deriving the key in one place is the fix;
    /// these are the inputs that exposed it.
    /// </summary>
    [Theory]
    [InlineData("https://Example.com/toc", "https://example.com/ch/1")]
    [InlineData("https://example.com:8443/toc", "https://example.com:8443/ch/1")]
    [InlineData("https://EXAMPLE.COM:8443/toc", "https://example.com:8443/ch/1")]
    [InlineData("https://example.com/toc", "https://example.com/ch/1")]
    public async Task Crawl_delay_applies_whatever_the_toc_url_looked_like(string tocUrl, string chapterUrl)
    {
        var (limiter, _) = Build(1);
        limiter.SetHostInterval(new Uri(tocUrl), TimeSpan.FromSeconds(5));

        var chapter = new Uri(chapterUrl);
        await limiter.AcquireAsync(chapter, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(5), limiter.IntervalFor(chapter));
        Assert.Equal(TimeSpan.FromSeconds(5), await limiter.AcquireAsync(chapter, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://Example.com/a", "https://example.com/b")]
    [InlineData("http://EXAMPLE.COM/a", "https://example.com/b")]
    [InlineData("https://example.com:8443/a", "https://example.com/b")]
    public void Host_key_ignores_case_scheme_and_port(string left, string right) =>
        Assert.Equal(RateLimiter.HostKey(new Uri(left)), RateLimiter.HostKey(new Uri(right)));

    [Fact]
    public void Host_key_separates_different_hosts() =>
        Assert.NotEqual(
            RateLimiter.HostKey(new Uri("https://a.example/x")),
            RateLimiter.HostKey(new Uri("https://b.example/x")));


    /// <summary>
    /// The structural half of the fix, and the half that actually holds.
    /// Python's bug was not a bad key derivation but two of them: callers
    /// passed a host string, and the two that did built it differently. Every
    /// entry point here takes a Uri, so there is no string overload through
    /// which a caller could supply a key of its own.
    /// </summary>
    [Fact]
    public void No_entry_point_accepts_a_host_string()
    {
        var stringKeyed = typeof(RateLimiter)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(string)))
            .Select(method => method.Name)
            .ToArray();

        Assert.Empty(stringKeyed);
    }
}
