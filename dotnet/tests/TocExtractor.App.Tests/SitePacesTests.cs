using TocExtractor.App.Session;
using TocExtractor.Core.Politeness;

namespace TocExtractor.App.Tests;

public sealed class SitePacesTests
{
    private const string Chapter = "https://novel.example/book/chapter-5";

    [Fact]
    public void Every_site_starts_fast()
    {
        var paces = new SitePaces(Path.Combine(Scratch.Directory(), "paces.json"));

        Assert.False(paces.IsCareful(Chapter));
        Assert.Empty(paces.All);
    }

    [Fact]
    public void A_site_that_asks_for_a_check_is_read_carefully_from_then_on_and_next_time()
    {
        var path = Path.Combine(Scratch.Directory(), "paces.json");
        var paces = new SitePaces(path);

        Assert.True(paces.RecordCheck(Chapter, DateTimeOffset.Now));
        Assert.False(paces.RecordCheck(Chapter, DateTimeOffset.Now));

        var later = new SitePaces(path);
        Assert.True(later.IsCareful("https://novel.example/other-book/1"));
        Assert.Equal(2, Assert.Single(later.All).Checks);
        Assert.False(later.IsCareful("https://other.example/book"));
    }

    [Fact]
    public void The_person_can_put_a_site_back_to_fast_or_forget_it()
    {
        var path = Path.Combine(Scratch.Directory(), "paces.json");
        var paces = new SitePaces(path);
        paces.RecordCheck(Chapter, DateTimeOffset.Now);

        paces.SetCareful("https://novel.example", careful: false);
        Assert.False(new SitePaces(path).IsCareful(Chapter));

        // Asking again does not undo the person's choice.
        paces.RecordCheck(Chapter, DateTimeOffset.Now);
        Assert.False(paces.IsCareful(Chapter));

        paces.Forget("https://novel.example");
        Assert.Empty(new SitePaces(path).All);
    }

    [Fact]
    public async Task A_careful_site_gets_a_burst_then_a_page_every_12_seconds()
    {
        var paces = new SitePaces(null);
        paces.RecordCheck(Chapter, DateTimeOffset.Now);
        var now = TimeSpan.Zero;
        var limiter = new RateLimiter(TimeSpan.FromSeconds(1), () => now, (d, _) =>
        {
            now += d;
            return ValueTask.CompletedTask;
        });

        paces.Apply(limiter, Chapter);
        List<TimeSpan> times = [];
        for (var i = 0; i < 60; i++)
        {
            await limiter.AcquireAsync(new Uri(Chapter), TestContext.Current.CancellationToken);
            times.Add(now);
        }

        // The first stretch at the usual speed, then a steady page every 12 seconds.
        Assert.Equal(1, (times[1] - times[0]).TotalSeconds, 1);
        var steady = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).TakeLast(10).ToList();
        Assert.All(steady, gap => Assert.InRange(gap, 11.5, 12.5));
    }

    [Fact]
    public async Task Just_after_a_check_there_is_no_burst()
    {
        var paces = new SitePaces(null);
        paces.RecordCheck(Chapter, DateTimeOffset.Now);
        var now = TimeSpan.Zero;
        var limiter = new RateLimiter(TimeSpan.FromSeconds(1), () => now, (d, _) =>
        {
            now += d;
            return ValueTask.CompletedTask;
        });

        paces.Apply(limiter, Chapter, justChecked: true);
        await limiter.AcquireAsync(new Uri(Chapter), TestContext.Current.CancellationToken);

        Assert.InRange(now.TotalSeconds, 11.5, 12.5);
    }

    [Fact]
    public void A_broken_file_is_no_reason_not_to_run()
    {
        var path = Path.Combine(Scratch.Directory(), "paces.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(new SitePaces(path).All);
    }
}

public sealed class CarefulEstimateTests
{
    private static Scanning.ScanResult Scan(string site) => new()
    {
        NovelUrl = site + "/book",
        BookTitle = "Book",
        Chapters = [.. Enumerable.Range(1, 60).Select(n => new Scanning.ScannedChapter(n, $"Chapter {n}", $"{site}/book/chapter-{n}"))],
        Layout = new Scanning.ChapterLayout("h1", "article", "a.next", "a.prev"),
    };

    private static NovelSession Session(SitePaces paces) => new(new NovelEnvironment
    {
        StartSource = (_, _, _) => throw new InvalidOperationException("no browser in this test"),
        FetchRobots = (_, _) => null,
        BrowserProfileDirectory = Scratch.Directory(),
        SitePaces = paces,
    })
    {
        Pace = new SessionSettings { Concurrency = 1, MinDelaySeconds = 1, MaxDelaySeconds = 2 },
    };

    [Fact]
    public async Task A_careful_site_says_how_long_it_really_takes()
    {
        var paces = new SitePaces(null);
        paces.RecordCheck("https://careful.example/book", DateTimeOffset.Now);
        await using var session = Session(paces);

        var preview = session.Preview(Scan("https://careful.example"), 1, 50);

        Assert.Contains("about 10 min", preview.Summary, StringComparison.Ordinal);
        Assert.Contains("careful pace", preview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_sites_keep_the_usual_estimate()
    {
        await using var session = Session(new SitePaces(null));

        var preview = session.Preview(Scan("https://fast.example"), 1, 50);

        Assert.Contains("about 1 min", preview.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("careful", preview.Summary, StringComparison.Ordinal);
    }
}
