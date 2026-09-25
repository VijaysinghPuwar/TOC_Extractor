using TocExtractor.Core.Links;
using TocExtractor.Core.Models;

namespace TocExtractor.Core.Exporters;

/// <summary>One chapter that belongs in a merged file, and where it comes from.</summary>
/// <param name="Url">The link as today's table of contents offers it.</param>
/// <param name="Index">The chapter number, which is what names its file.</param>
/// <param name="Fresh">The record this run produced, when it produced one.</param>
/// <param name="Prior">The checkpoint entry, when an earlier run produced it.</param>
public sealed record MergeEntry(string Url, int Index, ChapterRecord? Fresh, PriorChapter? Prior);

/// <summary>Decides what a merged file must contain, and in what order.</summary>
/// <remarks>
/// <para>
/// Keyed on URL and ordered by position in today's table of contents. Python
/// keys the merge on chapter index, where fresh chapters are numbered by
/// today's position and resumed ones keep the number the checkpoint recorded.
/// After chapters are prepended those two numbering schemes collide: the
/// resumed chapter's index is already taken by a new one, the merge skips it as
/// "already present", and a chapter sitting on disk vanishes from the merged
/// file. Every existing guard passes while it happens, because the counts still
/// balance.
/// </para>
/// <para>
/// A URL cannot collide with a different chapter's URL, so building the plan
/// this way makes that failure unrepresentable rather than merely tested for.
/// </para>
/// </remarks>
public static class MergePlan
{
    /// <summary>
    /// Every chapter the merged output must contain, in table-of-contents order.
    /// </summary>
    /// <param name="result">This run's outcome, carrying today's link order.</param>
    /// <param name="fresh">Records this run produced, by chapter index.</param>
    /// <param name="resumed">Chapters an earlier run produced, by URL.</param>
    public static IReadOnlyList<MergeEntry> Build(
        RunResult result,
        IReadOnlyDictionary<int, ChapterRecord> fresh,
        IReadOnlyDictionary<string, PriorChapter> resumed)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(resumed);

        var byUrl = fresh.Values.ToDictionary(record => record.RequestedUrl, UrlIdentity.Comparer);
        var priorByUrl = new Dictionary<string, PriorChapter>(UrlIdentity.Comparer);
        foreach (var entry in resumed)
        {
            priorByUrl[entry.Key] = entry.Value;
        }

        var failed = result.Failed.Select(failure => failure.Url).ToHashSet(UrlIdentity.Comparer);

        List<MergeEntry> plan = [];
        var position = 0;
        foreach (var url in result.Collection.Kept)
        {
            position++;
            if (failed.Contains(url))
            {
                continue;
            }

            if (byUrl.TryGetValue(url, out var record))
            {
                plan.Add(new MergeEntry(url, record.Index, record, null));
                continue;
            }

            if (priorByUrl.TryGetValue(url, out var prior))
            {
                plan.Add(new MergeEntry(url, prior.Index, null, prior));
                continue;
            }

            throw new MergeIncompleteException(
                $"chapter {position} ({url}) is neither in this run's records nor in the "
                + "checkpoint, and did not fail: the merged output would silently omit it");
        }

        return plan;
    }
}

/// <summary>A merged file that would not contain everything it should.</summary>
/// <remarks>
/// The last enforcement point for one invariant: nothing is lost silently. The
/// others are the link tally's constructor, the page-exception boundary, and
/// the run accounting. Each was added after content disappeared without a
/// trace, and this one is no different — a resumed run rebuilt the merged file
/// from only the chapters it had just fetched.
/// </remarks>
public sealed class MergeIncompleteException(string message) : InvalidOperationException(message);
