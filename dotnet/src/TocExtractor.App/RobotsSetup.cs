using TocExtractor.Core.Politeness;

namespace TocExtractor.App;

/// <summary>The robots.txt decision and rate limiter for one run, and how they were reached.</summary>
public sealed record RobotsSetup(RobotsPolicy Policy, RateLimiter Limiter, string RobotsUrl)
{
    /// <summary>
    /// Fetch and parse robots.txt for <paramref name="tocUrl"/>, and build a
    /// limiter that honours any Crawl-delay it sets.
    /// </summary>
    /// <remarks>
    /// The agent robots.txt is evaluated for is the agent the browser sends,
    /// so a custom agent gets the decisions meant for it. Crawl-delay can only
    /// slow the run down: the limiter keeps the larger of the two intervals.
    /// </remarks>
    public static RobotsSetup Prepare(
        string tocUrl,
        string userAgent,
        TimeSpan minDelay,
        Func<string, string, string?> fetchRobots,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(fetchRobots);

        var robotsUrl = Robots.LocationFor(tocUrl);
        var origin = Robots.OriginOf(tocUrl);
        var body = fetchRobots(robotsUrl, userAgent);
        var policy = body is null
            ? RobotsPolicy.Missing(origin, userAgent)
            : RobotsPolicy.Parse(body, origin, userAgent);

        var limiter = new RateLimiter(minDelay);
        if (policy.CrawlDelay is { } delay)
        {
            limiter.SetHostInterval(new Uri(tocUrl), delay);
            log?.Invoke($"robots.txt requests a {delay.TotalSeconds:0.0}s crawl delay; honouring it");
        }

        return new RobotsSetup(policy, limiter, robotsUrl);
    }
}
