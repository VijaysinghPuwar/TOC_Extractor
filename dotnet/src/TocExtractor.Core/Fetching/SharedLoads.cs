using TocExtractor.Core.Links;
using TocExtractor.Core.Pages;

namespace TocExtractor.Core.Fetching;

/// <summary>
/// The chapter pages being opened right now by the extractions saving one
/// book, so two walking the same stretch open each page once between them.
/// </summary>
/// <remarks>
/// Chapters 1-50 and 51-100 of a book that lists only chapter 1 are both
/// reached by following next links from chapter 1. Started together, the
/// second asked for each page while the first was still opening it, so every
/// page was opened twice and both went at half the site's pace (measured:
/// 156 pages opened for 100 chapters). Now the second waits for the first's
/// page and uses it.
///
/// Keyed by the link each walk follows as well as the address: a page opened
/// by a walk going backward carries its previous link where one going
/// forward expects the next, and taking it would send the forward walk back
/// over chapters it has passed.
///
/// The last few pages opened are kept too. Two walks from one chapter rarely
/// stay in step: measured, the one that started second overtook the other by
/// two pages and every page it had just finished was opened again, fifty
/// extra requests to the site for one book.
/// </remarks>
public sealed class SharedLoads
{
    /// <summary>Pages kept per direction once opened: enough for a walk a few pages behind, a few MB at most.</summary>
    internal const int Recent = 16;

    private readonly Dictionary<string, (Dictionary<string, ChapterPage> Pages, Queue<string> Order)> recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TaskCompletionSource<ChapterPage?>>> underway = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>
    /// Either the page another extraction is opening now (<paramref name="claim"/>
    /// is null), or null and a claim: this caller opens it, and completes the
    /// claim with the page so anyone waiting gets it. Only a walk following
    /// the same <paramref name="step"/> link is joined.
    /// </summary>
    public Task<ChapterPage?>? JoinOrClaim(string url, string step, out Claim? claim)
    {
        lock (this.gate)
        {
            if (!this.underway.TryGetValue(step, out var loads))
            {
                loads = new Dictionary<string, TaskCompletionSource<ChapterPage?>>(UrlIdentity.Comparer);
                this.underway[step] = loads;
            }

            if (loads.TryGetValue(url, out var other))
            {
                claim = null;
                return other.Task;
            }

            if (this.recent.TryGetValue(step, out var kept) && kept.Pages.TryGetValue(url, out var opened))
            {
                claim = null;
                return Task.FromResult<ChapterPage?>(opened);
            }

            var mine = new TaskCompletionSource<ChapterPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            loads[url] = mine;
            claim = new Claim(this, url, step, mine);
            return null;
        }
    }

    private void Finish(string url, string step, TaskCompletionSource<ChapterPage?> load, ChapterPage? page)
    {
        lock (this.gate)
        {
            if (this.underway.TryGetValue(step, out var loads)
                && loads.TryGetValue(url, out var current) && ReferenceEquals(current, load))
            {
                loads.Remove(url);
                if (loads.Count == 0)
                {
                    this.underway.Remove(step);
                }

                if (page is not null)
                {
                    this.Keep(url, step, page);
                }
            }
        }

        load.TrySetResult(page);
    }

    private void Keep(string url, string step, ChapterPage page)
    {
        if (!this.recent.TryGetValue(step, out var kept))
        {
            kept = (new Dictionary<string, ChapterPage>(UrlIdentity.Comparer), new Queue<string>());
            this.recent[step] = kept;
        }

        if (kept.Pages.TryAdd(url, page))
        {
            kept.Order.Enqueue(url);
            if (kept.Order.Count > Recent)
            {
                kept.Pages.Remove(kept.Order.Dequeue());
            }
        }
        else
        {
            kept.Pages[url] = page;
        }
    }

    /// <summary>One page being opened. Disposed without a page (a failure, a stop), anyone waiting opens it themselves.</summary>
    public sealed class Claim(SharedLoads owner, string url, string step, TaskCompletionSource<ChapterPage?> load) : IDisposable
    {
        public void Complete(ChapterPage? page) => owner.Finish(url, step, load, page);

        public void Dispose() => owner.Finish(url, step, load, null);
    }
}
