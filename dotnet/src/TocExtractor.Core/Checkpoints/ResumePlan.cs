using TocExtractor.Core.Links;

namespace TocExtractor.Core.Checkpoints;

/// <summary>What an existing checkpoint means for the run about to start.</summary>
public sealed record ResumePlan(
    Checkpoint Checkpoint,
    TocComparison Comparison,
    int AlreadyDone,
    IReadOnlyList<string> Appended,
    bool Usable = true,
    string Refusal = "",
    bool Renumbering = false);

public static class ResumePlanner
{
    /// <summary>Ordered comparison that accepts growth at either end.</summary>
    /// <remarks>
    /// Reverse-chronological serials prepend new chapters, and treating that as
    /// a reorder would demand a restart for the single most common update
    /// pattern on the sites this targets. Removals and interior reordering stay
    /// ambiguous, because resuming into a re-indexed table of contents would
    /// mismatch chapter numbers against files already on disk.
    /// </remarks>
    public static TocComparison Compare(IReadOnlyList<string> stored, IReadOnlyList<string> current)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);

        if (stored.Count == 0)
        {
            return TocComparison.Identical;
        }

        string[] before = [.. stored.Select(UrlIdentity.Of)];
        string[] after = [.. current.Select(UrlIdentity.Of)];

        if (before.SequenceEqual(after, StringComparer.Ordinal))
        {
            return TocComparison.Identical;
        }

        if (after.Length < before.Length)
        {
            // A shorter list that is still a prefix of the stored one is the
            // shape a lowered chapter cap produces, and the shape of chapters
            // dropped off the end. Both leave every remaining chapter's number
            // exactly where it was, so resuming is safe. Python calls any
            // shrinkage divergent and demands a restart, which throws away a
            // completed run for the sake of asking for fewer chapters.
            return before.Take(after.Length).SequenceEqual(after, StringComparer.Ordinal)
                ? TocComparison.Narrowed
                : TocComparison.Diverged;
        }

        if (after.Take(before.Length).SequenceEqual(before, StringComparer.Ordinal))
        {
            return TocComparison.GrewAtEnd;
        }

        return after.Skip(after.Length - before.Length).SequenceEqual(before, StringComparer.Ordinal)
            ? TocComparison.GrewAtStart
            : TocComparison.Diverged;
    }

    /// <summary>Decide what an existing checkpoint means. Null when there is nothing to resume.</summary>
    public static ResumePlan? Plan(
        string outputDirectory,
        string tocUrl,
        SelectorSet selectors,
        IReadOnlyList<string> currentLinks,
        bool force = false,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        ArgumentNullException.ThrowIfNull(currentLinks);

        var existing = Checkpoint.Load(outputDirectory, warn);
        if (existing is null)
        {
            return null;
        }

        if (force)
        {
            existing.Discard();
            return null;
        }

        var expected = Checkpoint.FingerprintOf(tocUrl, selectors);
        if (!string.Equals(existing.Fingerprint, expected, StringComparison.Ordinal))
        {
            return new ResumePlan(
                existing, TocComparison.Diverged, existing.Completed.Count, [],
                Usable: false, Refusal: DescribeExtractionChange(existing, tocUrl, selectors));
        }

        var comparison = Compare(existing.LinkSet, currentLinks);
        var stored = existing.LinkSet.Select(UrlIdentity.Of).ToHashSet(StringComparer.Ordinal);
        string[] appended = [.. currentLinks.Where(url => !stored.Contains(UrlIdentity.Of(url)))];

        if (comparison is TocComparison.Diverged)
        {
            return new ResumePlan(
                existing, comparison, existing.Completed.Count, [],
                Usable: false, Refusal: DescribeTocChange([.. existing.LinkSet], [.. currentLinks]));
        }

        return new ResumePlan(
            existing,
            comparison,
            existing.Completed.Count,
            appended,
            Renumbering: comparison is TocComparison.GrewAtStart && existing.Completed.Count > 0);
    }

    /// <summary>Say which input changed, not that one of several might have.</summary>
    private static string DescribeExtractionChange(
        Checkpoint existing, string tocUrl, SelectorSet selectors)
    {
        List<string> changes = [];
        if (!UrlIdentity.SameChapter(existing.TocUrl, tocUrl))
        {
            changes.Add($"TOC URL was '{existing.TocUrl}', now '{tocUrl}'");
        }

        foreach (var (name, current) in new[]
        {
            ("link", selectors.Link), ("title", selectors.Title), ("content", selectors.Content),
        })
        {
            if (existing.Selectors.TryGetValue(name, out var was)
                && !string.Equals(was, current, StringComparison.Ordinal))
            {
                changes.Add($"--{name} was '{was}', now '{current}'");
            }
        }

        if (changes.Count == 0)
        {
            changes.Add("the stored run used a different TOC URL or different selectors");
        }

        return "this is not the same extraction (" + string.Join("; ", changes)
            + "), so the files already written do not match what this run would produce; "
            + "start over to discard them";
    }

    /// <summary>Say what moved, so the reader can decide whether starting over is safe.</summary>
    private static string DescribeTocChange(string[] stored, string[] current)
    {
        var before = stored.Select(UrlIdentity.Of).ToArray();
        var after = current.Select(UrlIdentity.Of).ToArray();
        var afterSet = after.ToHashSet(StringComparer.Ordinal);
        var beforeSet = before.ToHashSet(StringComparer.Ordinal);

        string[] missing = [.. before.Where(url => !afterSet.Contains(url))];
        string[] added = [.. after.Where(url => !beforeSet.Contains(url))];

        List<string> parts = [$"stored {stored.Length} link(s), found {current.Length}"];
        if (missing.Length > 0)
        {
            parts.Add($"{missing.Length} no longer present, first {missing[0]}");
        }

        if (added.Length > 0)
        {
            parts.Add($"{added.Length} new");
        }

        if (missing.Length == 0 && added.Length == 0)
        {
            parts.Add("same links in a different order");
        }

        return "the table of contents changed in a way that is not simple growth ("
            + string.Join("; ", parts)
            + "); chapter numbers would no longer line up with the files already written, "
            + "so start over to renumber";
    }
}
