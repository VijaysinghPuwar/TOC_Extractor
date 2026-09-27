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
/// </remarks>
public sealed class SharedLoads
{
    private readonly Dictionary<string, TaskCompletionSource<ChapterPage?>> underway = new(UrlIdentity.Comparer);
    private readonly Lock gate = new();

    /// <summary>
    /// Either the page another extraction is opening now (<paramref name="claim"/>
    /// is null), or null and a claim: this caller opens it, and completes the
    /// claim with the page so anyone waiting gets it.
    /// </summary>
    public Task<ChapterPage?>? JoinOrClaim(string url, out Claim? claim)
    {
        lock (this.gate)
        {
            if (this.underway.TryGetValue(url, out var other))
            {
                claim = null;
                return other.Task;
            }

            var mine = new TaskCompletionSource<ChapterPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.underway[url] = mine;
            claim = new Claim(this, url, mine);
            return null;
        }
    }

    private void Finish(string url, TaskCompletionSource<ChapterPage?> load, ChapterPage? page)
    {
        lock (this.gate)
        {
            if (this.underway.TryGetValue(url, out var current) && ReferenceEquals(current, load))
            {
                this.underway.Remove(url);
            }
        }

        load.TrySetResult(page);
    }

    /// <summary>One page being opened. Disposed without a page (a failure, a stop), anyone waiting opens it themselves.</summary>
    public sealed class Claim(SharedLoads owner, string url, TaskCompletionSource<ChapterPage?> load) : IDisposable
    {
        public void Complete(ChapterPage? page) => owner.Finish(url, load, page);

        public void Dispose() => owner.Finish(url, load, null);
    }
}
