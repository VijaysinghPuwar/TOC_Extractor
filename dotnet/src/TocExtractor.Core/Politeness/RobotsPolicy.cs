using System.Globalization;

namespace TocExtractor.Core.Politeness;

/// <summary>A parsed robots.txt for one origin.</summary>
/// <remarks>
/// <para>
/// One evaluation answers both questions this class is asked: whether a URL may
/// be fetched, and which directive decided. Python cannot do that — it defers
/// the decision to <c>urllib.robotparser</c>, which exposes no way to ask which
/// rule won, and then locates the rule separately with a second algorithm. The
/// two can disagree: with a user agent of "MyTOCExtractorBot",
/// <c>can_fetch</c> matches the specific group by substring and permits the
/// path, while <c>matched_rule</c> matches by prefix, falls back to the
/// wildcard group, and names a Disallow that never applied. Deriving both from
/// one pass makes that unrepresentable.
/// </para>
/// <para>
/// Group selection follows RFC 9309: compare the product token — the part
/// before any slash — case-insensitively, most specific first, then the
/// wildcard group.
/// </para>
/// </remarks>
public sealed class RobotsPolicy
{
    private readonly RobotsGroup? group;

    private RobotsPolicy(
        string origin,
        string userAgent,
        RobotsGroup? group,
        TimeSpan? crawlDelay,
        bool fetched)
    {
        this.Origin = origin;
        this.UserAgent = userAgent;
        this.group = group;
        this.CrawlDelay = crawlDelay;
        this.Fetched = fetched;
    }

    public string Origin { get; }

    public string UserAgent { get; }

    /// <summary>The Crawl-delay of the group that governs this agent, if it set one.</summary>
    /// <remarks>
    /// Read from the applicable group only. robots.txt precedence is
    /// winner-takes-all, so a wildcard group's Crawl-delay does not reach an
    /// agent that has a group of its own.
    /// </remarks>
    public TimeSpan? CrawlDelay { get; }

    /// <summary>False when robots.txt could not be read at all.</summary>
    public bool Fetched { get; }

    /// <summary>Every rule in the group governing this agent.</summary>
    public IReadOnlyList<RobotsRule> Rules => this.group?.Rules ?? [];

    public static RobotsPolicy Parse(
        string content,
        string origin,
        string userAgent = Robots.DefaultUserAgent)
    {
        ArgumentNullException.ThrowIfNull(content);

        var groups = RobotsGroup.ParseAll(content);
        var applicable = RobotsGroup.ApplicableTo(groups, userAgent);

        return new RobotsPolicy(origin, userAgent, applicable, applicable?.CrawlDelay, fetched: true);
    }

    /// <summary>Policy for an origin whose robots.txt could not be fetched.</summary>
    /// <remarks>RFC 9309: an unreachable or absent robots.txt imposes no restrictions.</remarks>
    public static RobotsPolicy Missing(string origin, string userAgent = Robots.DefaultUserAgent) =>
        new(origin, userAgent, group: null, crawlDelay: null, fetched: false);

    public bool CanFetch(string url) => this.Evaluate(url).Allowed;

    /// <summary>The directive that refused <paramref name="url"/>, or null if it was permitted.</summary>
    public RobotsRule? MatchedRule(string url)
    {
        var (allowed, decided) = this.Evaluate(url);
        return allowed ? null : decided;
    }

    private (bool Allowed, RobotsRule? Decided) Evaluate(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!this.Fetched || this.group is null)
        {
            return (true, null);
        }

        var path = PathOf(url);
        var best = 0;
        var allowed = true;
        RobotsRule? decided = null;

        foreach (var rule in this.group.Rules)
        {
            var specificity = rule.Specificity(path);
            if (specificity == 0)
            {
                continue;
            }

            // Longer wins; on a tie an Allow beats a Disallow, which is what
            // makes an equally specific Allow the permissive one.
            if (specificity > best || (specificity == best && !allowed && rule.Allows))
            {
                best = specificity;
                allowed = rule.Allows;
                decided = rule;
            }
        }

        return (allowed, decided);
    }

    private static string PathOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return url.Length == 0 ? "/" : url;
        }

        var path = parsed.AbsolutePath;
        return path.Length == 0 ? "/" : path;
    }
}

/// <summary>Shared robots constants and origin handling.</summary>
public static class Robots
{
    public const string DefaultUserAgent = "TOCExtractor";

    /// <summary>Scheme and authority only, which is what robots.txt is scoped to.</summary>
    public static string OriginOf(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? string.Create(CultureInfo.InvariantCulture, $"{parsed.Scheme}://{parsed.Authority}")
            : url;
    }

    public static string LocationFor(string url) => $"{OriginOf(url)}/robots.txt";
}
