using System.Globalization;

namespace TocExtractor.Core.Politeness;

/// <summary>One User-agent group and the directives under it.</summary>
internal sealed class RobotsGroup
{
    private readonly List<string> agents = [];
    private readonly List<RobotsRule> rules = [];

    internal IReadOnlyList<string> Agents => this.agents;

    internal IReadOnlyList<RobotsRule> Rules => this.rules;

    internal TimeSpan? CrawlDelay { get; private set; }

    /// <summary>Split robots.txt into groups, remembering line numbers.</summary>
    internal static List<RobotsGroup> ParseAll(string content)
    {
        List<RobotsGroup> groups = [];
        RobotsGroup? current = null;
        var previousWasAgent = false;
        var lineNumber = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;

            var line = rawLine.Split('#', 2)[0].Trim();
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();

            if (key == "user-agent")
            {
                // Consecutive User-agent lines share one group; a User-agent
                // after any rule line starts a new one.
                if (current is null || !previousWasAgent)
                {
                    current = new RobotsGroup();
                    groups.Add(current);
                }

                current.agents.Add(value.ToLowerInvariant());
                previousWasAgent = true;
                continue;
            }

            previousWasAgent = false;
            if (current is null)
            {
                continue;
            }

            switch (key)
            {
                case "disallow":
                case "allow":
                    current.rules.Add(new RobotsRule(
                        key,
                        value,
                        lineNumber,
                        current.agents.Count > 0 ? current.agents[^1] : "*"));
                    break;

                case "crawl-delay":
                    if (double.TryParse(
                            value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        && seconds >= 0)
                    {
                        current.CrawlDelay = TimeSpan.FromSeconds(seconds);
                    }

                    break;

                default:
                    break;
            }
        }

        return groups;
    }

    /// <summary>The group that governs <paramref name="userAgent"/>.</summary>
    /// <remarks>
    /// robots.txt precedence is winner-takes-all: if any group names this agent,
    /// the wildcard group does not apply at all. Matching is on the product
    /// token — the part before a slash — compared case-insensitively, with the
    /// longest matching token winning, per RFC 9309. Every group for the
    /// winning agent counts, combined into one (RFC 9309 2.2.1): a site that
    /// wrote each rule under its own "User-agent: *" had all but its first
    /// rule ignored, and its chapter pages read against its wishes.
    /// </remarks>
    internal static RobotsGroup? ApplicableTo(List<RobotsGroup> groups, string userAgent)
    {
        var token = userAgent.Split('/')[0].Trim().ToLowerInvariant();

        string? best = null;
        foreach (var group in groups)
        {
            foreach (var agent in group.Agents)
            {
                if (agent != "*"
                    && string.Equals(agent, token, StringComparison.Ordinal)
                    && agent.Length > (best?.Length ?? -1))
                {
                    best = agent;
                }
            }
        }

        best ??= "*";
        var matching = groups.Where(group => group.Agents.Contains(best)).ToList();
        if (matching.Count <= 1)
        {
            return matching.FirstOrDefault();
        }

        var merged = new RobotsGroup();
        merged.agents.Add(best);
        foreach (var group in matching)
        {
            merged.rules.AddRange(group.rules);
            merged.CrawlDelay ??= group.CrawlDelay;
        }

        return merged;
    }
}
