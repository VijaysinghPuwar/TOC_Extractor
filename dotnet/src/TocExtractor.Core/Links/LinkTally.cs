using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Links;

/// <summary>A link that was neither kept nor rejected, which cannot be allowed.</summary>
public sealed class LinkAccountingException(string message) : InvalidOperationException(message);

/// <summary>The result of vetting one table-of-contents page's links.</summary>
/// <remarks>
/// The contract this type exists to enforce:
/// <code>raw == kept + rejected + truncated</code>
/// Every candidate is either kept or rejected with a reason, and nothing is
/// dropped for being falsy. A truthiness filter is what silently lost SVG
/// anchors in v1, and no care downstream can notice a link that was never
/// counted. The check is in the constructor rather than in a log line, so an
/// unbalanced collection cannot be built at all.
/// </remarks>
public sealed class LinkTally
{
    public LinkTally(
        int rawCount,
        IReadOnlyList<string>? kept = null,
        IReadOnlyList<RejectedLink>? rejected = null,
        int truncated = 0)
    {
        this.RawCount = rawCount;
        this.Kept = kept ?? [];
        this.Rejected = rejected ?? [];
        this.Truncated = truncated;

        var total = this.Kept.Count + this.Rejected.Count + truncated;
        if (total != rawCount)
        {
            throw new LinkAccountingException(
                $"link accounting lost {rawCount - total} of {rawCount} candidates: "
                + $"kept={this.Kept.Count} rejected={this.Rejected.Count} truncated={truncated}");
        }
    }

    public int RawCount { get; }

    public IReadOnlyList<string> Kept { get; }

    public IReadOnlyList<RejectedLink> Rejected { get; }

    /// <summary>
    /// Links dropped by a maximum, counted apart from rejections because they
    /// were vetted successfully and merely fell outside the requested range.
    /// </summary>
    public int Truncated { get; }

    /// <summary>Rejections by reason, in the form the manifest records them.</summary>
    public IReadOnlyDictionary<string, int> ReasonCounts()
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (var item in this.Rejected)
        {
            var key = item.Reason.ToWireValue();
            counts[key] = counts.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }

        return counts;
    }
}
