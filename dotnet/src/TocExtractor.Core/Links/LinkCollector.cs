using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Links;

/// <summary>A vetted link set and the robots decision recorded for each kept link.</summary>
public sealed record VettedLinks(LinkTally Collection, IReadOnlyList<RobotsDecision> Decisions);

/// <summary>Turns raw DOM output into a vetted list of chapter URLs.</summary>
public static class LinkCollector
{
    /// <summary>
    /// Collects chapter links in the page, resolving each against the document
    /// base.
    /// </summary>
    /// <remarks>
    /// Reading <c>href</c> off the element and falling back to the property has
    /// two failure modes: an anchor's <c>href</c> property is already absolute,
    /// so a host-side join never runs for real anchors; and an SVG anchor's is
    /// an SVGAnimatedString rather than a string. Resolving the attribute
    /// against <c>document.baseURI</c> returns a plain absolute string for
    /// ordinary anchors, SVG anchors and stray elements carrying an href alike,
    /// and keeps resolution in the one place that knows the document's base.
    /// </remarks>
    public const string Script = """
        elements => elements.map(el => {
            const raw = el.getAttribute('href');
            if (raw === null) return null;
            try {
                return new URL(raw, document.baseURI).href;
            } catch (e) {
                return raw;
            }
        })
        """;

    /// <summary>Vet raw DOM values into fetchable URLs.</summary>
    /// <param name="raw">
    /// Exactly what the DOM produced, including non-string values. Nothing is
    /// filtered before this point, because filtering would move the accounting
    /// boundary and reintroduce the silent drop it exists to stop.
    /// </param>
    /// <param name="guard">The scheme and address policy.</param>
    /// <param name="robots">The robots policy, or null when there is none to apply.</param>
    /// <param name="sessionAuthenticated">
    /// Whether the human gate has been passed with evidence of a session. False
    /// makes a robots Disallow a hard rejection; true keeps the link and carries
    /// the matched rule so the caller can warn and record the override.
    /// </param>
    /// <param name="maxLinks">Keep at most this many, counting the rest as truncated.</param>
    public static VettedLinks Collect(
        IReadOnlyList<object?> raw,
        UrlGuard guard,
        RobotsPolicy? robots = null,
        bool sessionAuthenticated = false,
        int? maxLinks = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(guard);

        List<string> kept = [];
        List<RejectedLink> rejected = [];
        List<RobotsDecision> decisions = [];
        // Identity, not the raw string: /ch1 and /ch1#top are one request.
        HashSet<string> seen = new(UrlIdentity.Comparer);

        foreach (var candidate in raw)
        {
            var verdict = guard.Check(candidate);
            if (!verdict.Allowed)
            {
                rejected.Add(new RejectedLink(
                    verdict.Url,
                    verdict.Reason ?? RejectionReason.Malformed,
                    verdict.Detail));
                continue;
            }

            var url = verdict.Url;
            if (!seen.Add(url))
            {
                rejected.Add(new RejectedLink(url, RejectionReason.Duplicate));
                continue;
            }

            var decision = ApplyRobots(url, robots, sessionAuthenticated);
            if (!decision.Allowed)
            {
                rejected.Add(new RejectedLink(
                    url, RejectionReason.RobotsDisallowed, decision.RuleDescription ?? ""));
                continue;
            }

            kept.Add(url);
            decisions.Add(decision);
        }

        var truncated = 0;
        if (maxLinks is int limit && kept.Count > limit)
        {
            truncated = kept.Count - limit;
            kept.RemoveRange(limit, truncated);
            decisions.RemoveRange(limit, decisions.Count - limit);
        }

        return new VettedLinks(
            new LinkTally(raw.Count, kept, rejected, truncated),
            decisions);
    }

    private static RobotsDecision ApplyRobots(
        string url,
        RobotsPolicy? robots,
        bool sessionAuthenticated)
    {
        if (robots is null || robots.CanFetch(url))
        {
            return new RobotsDecision(Allowed: true);
        }

        var rule = robots.MatchedRule(url);
        var description = rule?.Describe() ?? "robots.txt disallows this path";

        // The escape hatch. A site frequently disallows the very paths a login
        // unlocks, so refusing hard here would fail the signed-in flow on day
        // one and train people to disable the check permanently.
        return sessionAuthenticated
            ? new RobotsDecision(Allowed: true, description, AuthenticatedOverride: true)
            : new RobotsDecision(Allowed: false, description);
    }
}
