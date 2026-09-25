namespace TocExtractor.Core.Links;

/// <summary>What robots.txt said about one URL, and whether a human overrode it.</summary>
/// <remarks>
/// <paramref name="AuthenticatedOverride"/> is only ever set on the post-gate
/// path, after a person has signed in and confirmed. There is no flag that
/// produces it, by design: overriding robots is a deliberate human action, not
/// a string in a shell script that gets copied between runs.
/// </remarks>
/// <param name="Allowed">Whether the URL may be fetched.</param>
/// <param name="RuleDescription">The directive that decided, when one did.</param>
/// <param name="AuthenticatedOverride">True when a signed-in session proceeded past a Disallow.</param>
public readonly record struct RobotsDecision(
    bool Allowed,
    string? RuleDescription = null,
    bool AuthenticatedOverride = false)
{
    /// <summary>The fields this decision contributes to a manifest entry.</summary>
    public IReadOnlyDictionary<string, object?> AsManifestEntry() =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["robots_allowed"] = this.Allowed,
            ["robots_rule"] = this.RuleDescription,
            ["robots_authenticated_override"] = this.AuthenticatedOverride,
        };
}
