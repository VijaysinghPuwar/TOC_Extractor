namespace TocExtractor.Core.Politeness;

/// <summary>The outcome of checking one candidate URL.</summary>
/// <param name="Url">The candidate, trimmed, or its description when it was not a string.</param>
/// <param name="Allowed">Whether it may be fetched.</param>
/// <param name="Reason">Why not, when it may not. Always set when <c>Allowed</c> is false.</param>
/// <param name="Detail">Whatever names the specific cause: a scheme, a host, a resolved address.</param>
public readonly record struct UrlVerdict(
    string Url,
    bool Allowed,
    RejectionReason? Reason = null,
    string Detail = "")
{
    internal static UrlVerdict Allow(string url) => new(url, true);

    internal static UrlVerdict Reject(string url, RejectionReason reason, string detail = "") =>
        new(url, false, reason, detail);
}
