using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

public sealed class AllowanceTests
{
    private static readonly Uri Site = new("https://novel.example/book/1");

    private static (RateLimiter Limiter, Func<TimeSpan> Now, Action<TimeSpan> Pass) Clock(TimeSpan interval)
    {
        var now = TimeSpan.Zero;
        var limiter = new RateLimiter(interval, () => now, (d, _) =>
        {
            now += d;
            return ValueTask.CompletedTask;
        });
        return (limiter, () => now, d => now += d);
    }

    [Fact]
    public async Task A_burst_goes_at_the_usual_pace_then_one_page_per_refill()
    {
        var (limiter, now, _) = Clock(TimeSpan.FromSeconds(1));
        limiter.SetHostAllowance(Site, burst: 3, refillEvery: TimeSpan.FromSeconds(12));
        List<TimeSpan> times = [];
        for (var i = 0; i < 6; i++)
        {
            await limiter.AcquireAsync(Site, TestContext.Current.CancellationToken);
            times.Add(now());
        }

        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();

        // Three at one second apart, then the allowance is spent: about 12s each.
        Assert.Equal(1, gaps[0], 1);
        Assert.Equal(1, gaps[1], 1);
        Assert.All(gaps.Skip(3), gap => Assert.InRange(gap, 11, 12.5));
        var perMinute = 60 / gaps.Skip(3).Average();
        Assert.InRange(perMinute, 4.5, 5.5);
    }

    [Fact]
    public async Task Leaving_the_site_alone_refills_the_allowance()
    {
        var (limiter, now, pass) = Clock(TimeSpan.FromSeconds(1));
        limiter.SetHostAllowance(Site, burst: 3, refillEvery: TimeSpan.FromSeconds(12));
        for (var i = 0; i < 3; i++)
        {
            await limiter.AcquireAsync(Site, TestContext.Current.CancellationToken);
        }

        pass(TimeSpan.FromMinutes(5));
        var before = now();
        for (var i = 0; i < 3; i++)
        {
            await limiter.AcquireAsync(Site, TestContext.Current.CancellationToken);
        }

        Assert.InRange((now() - before).TotalSeconds, 0, 2.5);
    }

    [Fact]
    public async Task Without_an_allowance_nothing_changes()
    {
        var (limiter, now, _) = Clock(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(Site, TestContext.Current.CancellationToken);
        }

        Assert.Equal(TimeSpan.FromSeconds(8), now());
    }

    [Fact]
    public async Task Other_sites_keep_their_own_pace()
    {
        var (limiter, now, _) = Clock(TimeSpan.FromSeconds(1));
        limiter.SetHostAllowance(Site, burst: 1, refillEvery: TimeSpan.FromSeconds(30));
        var other = new Uri("https://other.example/book");
        await limiter.AcquireAsync(Site, TestContext.Current.CancellationToken);
        var before = now();
        for (var i = 0; i < 4; i++)
        {
            await limiter.AcquireAsync(other, TestContext.Current.CancellationToken);
        }

        Assert.Equal(TimeSpan.FromSeconds(3), now() - before);
    }
}
