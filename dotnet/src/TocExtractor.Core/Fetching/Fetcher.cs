using System.Collections.Concurrent;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Fetching;

/// <summary>The table-of-contents page and the verdict on every link it offered.</summary>
public sealed record CollectedLinks(
    string TocUrl,
    TocPage Toc,
    LinkTally Collection,
    IReadOnlyList<RobotsDecision> Decisions)
{
    public IReadOnlyList<string> Kept => this.Collection.Kept;
}

/// <summary>The concurrent, polite, resumable fetch loop.</summary>
/// <remarks>
/// Composes the rate limiter with bounded concurrency. Those two have to
/// compose correctly or the tool quietly stops being polite, which is the one
/// property the rest of this codebase is built to guarantee — so the interval is
/// enforced inside the limiter's per-host lock rather than by spacing task
/// starts, and a test runs several workers at one host and asserts the observed
/// spacing survives.
/// </remarks>
public sealed class Fetcher : IDisposable
{
    private readonly IPageSource source;
    private readonly UrlGuard guard;
    private readonly FetchOptions options;
    private readonly RobotsPolicy? robots;
    private readonly RateLimiter limiter;
    private readonly Func<DateTimeOffset> now;
    private readonly Random rng;
    private readonly Func<TimeSpan, CancellationToken, ValueTask> sleep;
    private readonly Func<string, bool> alreadyDone;
    private readonly Action<ChapterRecord>? onRecord;
    private readonly Action<FailedChapter>? onFailure;
    private readonly Func<string, CancellationToken, Task<bool>>? onHumanCheck;

    // One person, one check at a time: workers that hit it together wait on
    // the first, then find it already passed and simply try again.
    private readonly SemaphoreSlim humanGate = new(1, 1);

    /// <summary>
    /// Serialises sink writes and the progress tally.
    /// </summary>
    /// <remarks>
    /// Python needs neither. Its event loop is single-threaded, so a coroutine
    /// step with no await between read and write cannot interleave, and the
    /// sinks mutate their state in exactly such a step. .NET continuations run
    /// on the thread pool and genuinely do run at the same time, so the same
    /// code is a data race here. This is the one place the two concurrency
    /// models differ in a way the port cannot paper over.
    /// </remarks>
    private readonly SemaphoreSlim writeGate = new(1, 1);

    private ISink sink;

    public Fetcher(
        IPageSource source,
        UrlGuard guard,
        ISink sink,
        FetchOptions? options = null,
        RateLimiter? limiter = null,
        RobotsPolicy? robots = null,
        Func<DateTimeOffset>? now = null,
        Random? rng = null,
        Func<TimeSpan, CancellationToken, ValueTask>? sleep = null,
        Func<string, bool>? alreadyDone = null,
        Action<ChapterRecord>? onRecord = null,
        Action<FailedChapter>? onFailure = null,
        Func<string, CancellationToken, Task<bool>>? onHumanCheck = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(sink);

        this.source = source;
        this.guard = guard;
        this.sink = sink;
        this.onHumanCheck = onHumanCheck;
        this.options = options ?? new FetchOptions();
        this.robots = robots;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.rng = rng ?? new Random();
        this.sleep = sleep ?? (static (duration, token) => new ValueTask(Task.Delay(duration, token)));
        this.alreadyDone = alreadyDone ?? (static _ => false);
        this.onRecord = onRecord;
        this.onFailure = onFailure;
        this.limiter = limiter ?? new RateLimiter(this.options.MinDelay, sleep: (d, t) => this.sleep(d, t));
    }

    public void Dispose()
    {
        this.writeGate.Dispose();
        this.humanGate.Dispose();
    }

    /// <summary>Swap the sink before fetching.</summary>
    /// <remarks>
    /// The text exporter needs to know which chapters an earlier run already
    /// wrote, which is only known after the checkpoint has been consulted, which
    /// needs the link set. So the sink is chosen between collecting and
    /// fetching rather than at construction.
    /// </remarks>
    public void SetSink(ISink replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        this.sink = replacement;
    }

    /// <summary>Load the table of contents and vet its links. No chapter is fetched.</summary>
    /// <remarks>
    /// Split out because resume has to be planned against the link set the page
    /// actually offers today, and that is only known after this step.
    /// </remarks>
    public async Task<CollectedLinks> CollectAsync(
        string tocUrl,
        SelectorSet selectors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        var toc = await OnlyPageErrors(
            token => this.source.LoadTocAsync(
                tocUrl,
                selectors.Link,
                this.options.CaptureHtml,
                this.options.ScreenshotPath,
                token),
            tocUrl,
            cancellationToken).ConfigureAwait(false);

        var vetted = LinkCollector.Collect(
            toc.RawLinks,
            this.guard,
            this.robots,
            this.options.SessionAuthenticated,
            this.options.MaxLinks);

        return new CollectedLinks(tocUrl, toc, vetted.Collection, vetted.Decisions);
    }

    public async Task<RunResult> RunAsync(
        string tocUrl,
        SelectorSet selectors,
        CancellationToken cancellationToken = default)
    {
        var collected = await this.CollectAsync(tocUrl, selectors, cancellationToken)
            .ConfigureAwait(false);

        if (this.options.DryRun)
        {
            // Before any sink is opened: a dry run must not create the output
            // directory, let alone write to it.
            return new RunResult(tocUrl, collected.Collection);
        }

        return await this.FetchAsync(collected, selectors, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Fetch everything in <paramref name="collected"/> that is not already done.</summary>
    public async Task<RunResult> FetchAsync(
        CollectedLinks collected,
        SelectorSet selectors,
        Func<string, bool>? alreadyDone = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentNullException.ThrowIfNull(selectors);

        var isDone = alreadyDone ?? this.alreadyDone;
        List<(int Index, string Url, RobotsDecision? Decision)> pending = [];
        List<string> skipped = [];

        for (var position = 0; position < collected.Collection.Kept.Count; position++)
        {
            var url = collected.Collection.Kept[position];
            var decision = position < collected.Decisions.Count
                ? collected.Decisions[position]
                : (RobotsDecision?)null;

            if (isDone(url))
            {
                skipped.Add(url);
                continue;
            }

            pending.Add((position + 1, url, decision));
        }

        var progress = new Progress();
        await this.sink.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var slots = new SemaphoreSlim(Math.Max(1, this.options.Concurrency));
        await Task.WhenAll(pending.Select(item =>
            this.FetchOneAsync(item.Index, item.Url, item.Decision, selectors, slots, progress, cancellationToken)))
            .ConfigureAwait(false);

        var result = new RunResult(
            collected.TocUrl,
            collected.Collection,
            progress.OrderedCompleted(),
            progress.OrderedFailed(),
            skipped);

        if (!result.AccountsForEveryLink())
        {
            // Backstop for the same class of bug the tally's constructor
            // catches: a kept link that produced neither a record nor a failure
            // has been lost, and a silent loss is the one outcome this pipeline
            // refuses.
            throw new LinkAccountingException(
                $"run accounting lost links: kept={collected.Collection.Kept.Count} "
                + $"completed={result.Completed.Count} failed={result.Failed.Count} "
                + $"skipped={result.SkippedResumed.Count}");
        }

        await this.sink.CloseAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Fetch a chosen range of chapters: those whose addresses are known
    /// directly, and those reached by walking next or previous links from a
    /// known neighbour, in one run with one set of output files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chapters are saved under their own chapter numbers, so a run of
    /// 350 to 400 writes "350 - ..." to "400 - ..." and the combined file is
    /// in reading order whichever way the chapters were reached.
    /// </para>
    /// <para>
    /// A walk is for the part of a book no permitted page lists: a site whose
    /// robots.txt allows chapter pages but not the later pages of its list.
    /// Every step is vetted exactly as a listed link would be (the URL guard,
    /// then robots.txt, with the same signed-in override) and fetched by the
    /// same per-chapter code, so retries, rate limiting and writing are
    /// unchanged. A page inside the range is saved on the visit that finds it;
    /// one outside it is a step and nothing more. The walk ends past the far
    /// end of the range, at a page with no link onward, at a link back to a
    /// page already visited, at anything refused, or at a page that fails,
    /// since a failed page names no next one.
    /// </para>
    /// </remarks>
    public async Task<RunResult> FetchRangeAsync(
        string bookUrl,
        IReadOnlyList<NumberedLink> direct,
        IReadOnlyList<WalkSpec> walks,
        SelectorSet selectors,
        Func<string, bool>? alreadyDone = null,
        Func<ChapterPage, int?>? numberOf = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(walks);
        ArgumentNullException.ThrowIfNull(selectors);

        var isDone = alreadyDone ?? this.alreadyDone;
        var progress = new Progress();
        List<string> skipped = [];
        List<string> kept = [];
        List<RejectedLink> rejected = [];
        var candidates = 0;
        HashSet<string> claimed = new(UrlIdentity.Comparer);

        await this.sink.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Direct links first, concurrently, as a contents page would be.
        var vetted = LinkCollector.Collect(
            [.. direct.Select(link => (object?)link.Url)], this.guard, this.robots, this.options.SessionAuthenticated);
        candidates += direct.Count;
        rejected.AddRange(vetted.Collection.Rejected);
        var numbers = direct.ToDictionary(link => link.Url, link => link.Number, UrlIdentity.Comparer);
        List<Task> pending = [];
        using (var slots = new SemaphoreSlim(Math.Max(1, this.options.Concurrency)))
        {
            for (var i = 0; i < vetted.Collection.Kept.Count; i++)
            {
                var url = vetted.Collection.Kept[i];
                claimed.Add(url);
                kept.Add(url);
                if (isDone(url))
                {
                    skipped.Add(url);
                    continue;
                }

                pending.Add(this.FetchOneAsync(
                    numbers[url], url, vetted.Decisions[i], selectors, slots, progress, cancellationToken));
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        // Then each walk, one page at a time: every page names the next.
        using (var single = new SemaphoreSlim(1))
        {
            foreach (var walk in walks)
            {
                var url = walk.StartUrl;
                var expected = walk.StartNumber;
                HashSet<string> seen = new(UrlIdentity.Comparer);
                for (var step = 0; url is not null && step < walk.MaxSteps; step++)
                {
                    if (!seen.Add(url))
                    {
                        break;
                    }

                    var check = LinkCollector.Collect([url], this.guard, this.robots, this.options.SessionAuthenticated);
                    if (check.Collection.Kept.Count == 0)
                    {
                        candidates++;
                        rejected.AddRange(check.Collection.Rejected);
                        break;
                    }

                    var here = check.Collection.Kept[0];
                    var guess = expected;
                    int? Decide(ChapterPage page)
                    {
                        var number = numberOf?.Invoke(page) ?? guess;
                        expected = number;
                        return walk.Saves(number) && !claimed.Contains(here) ? number : null;
                    }

                    // Saved by an earlier run: it still has to be visited to
                    // learn where it leads, but nothing is written again.
                    var savedBefore = walk.Saves(guess) && isDone(here) && !claimed.Contains(here);

                    var before = progress.Count;
                    var page = await this.FetchOneAsync(
                        guess, here, check.Decisions[0], selectors, single, progress, cancellationToken,
                        walk.StepSelector, savedBefore ? static _ => null : Decide).ConfigureAwait(false);

                    // Counted exactly once: as saved or failed on this run, or
                    // as skipped when an earlier run saved it and it loaded.
                    if (progress.Count > before)
                    {
                        if (claimed.Add(here))
                        {
                            candidates++;
                            kept.Add(here);
                        }
                    }
                    else if (savedBefore && claimed.Add(here))
                    {
                        candidates++;
                        kept.Add(here);
                        skipped.Add(here);
                    }

                    if (page is null || walk.IsPast(expected))
                    {
                        break;
                    }

                    expected += walk.Direction;
                    url = page.NextUrl;
                }
            }
        }

        var tally = new LinkTally(candidates, kept, rejected);
        var result = new RunResult(bookUrl, tally, progress.OrderedCompleted(), progress.OrderedFailed(), skipped);
        if (!result.AccountsForEveryLink())
        {
            throw new LinkAccountingException(
                $"range accounting lost chapters: kept={kept.Count} completed={result.Completed.Count} "
                + $"failed={result.Failed.Count} skipped={result.SkippedResumed.Count}");
        }

        await this.sink.CloseAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<bool> WaitForPersonAsync(string url, CancellationToken cancellationToken)
    {
        if (this.onHumanCheck is null)
        {
            return false;
        }

        await this.humanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.onHumanCheck(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.humanGate.Release();
        }
    }

    /// <returns>The page on success, or null once the failure is recorded.</returns>
    private async Task<ChapterPage?> FetchOneAsync(
        int index,
        string url,
        RobotsDecision? decision,
        SelectorSet selectors,
        SemaphoreSlim slots,
        Progress progress,
        CancellationToken cancellationToken,
        string? nextSelector = null,
        Func<ChapterPage, int?>? decide = null)
    {
        var host = new Uri(url);

        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var attempt = 0;
            while (true)
            {
                attempt++;

                // Inside the slot, so a worker holding one is the one waiting on
                // the host. Acquiring before would let more workers than the
                // concurrency limit queue on the limiter.
                await this.limiter.AcquireAsync(host, cancellationToken).ConfigureAwait(false);

                ChapterPage page;
                try
                {
                    page = await this.LoadAsync(url, selectors, nextSelector, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A source that cancelled of its own accord rather than
                    // because we asked. Left unrecorded the chapter would leave
                    // no trace at all: kept=1, completed=0, failed=0.
                    this.RecordFailure(
                        progress, index, url,
                        new PageException($"{url}: source cancelled without being asked"),
                        attempt);
                    return null;
                }
                catch (HumanCheckException exception)
                {
                    // Not the chapter's fault, and not an attempt: the site
                    // wants a person. Wait for them, then load it again.
                    if (await this.WaitForPersonAsync(url, cancellationToken).ConfigureAwait(false))
                    {
                        attempt--;
                        continue;
                    }

                    this.RecordFailure(progress, index, url, exception, attempt);
                    return null;
                }
                catch (Exception exception)
                    when (exception is PageBlockedException or SelectorNotFoundException)
                {
                    // Neither is worth retrying: the target is disallowed, or the
                    // page loaded fine and simply lacks the selector.
                    this.RecordFailure(progress, index, url, (PageException)exception, attempt);
                    return null;
                }
                catch (PageException exception)
                {
                    if (attempt > this.options.Retries)
                    {
                        this.RecordFailure(progress, index, url, exception, attempt);
                        return null;
                    }

                    await this.sleep(this.Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A walk decides after loading: the page's own title says which
                // chapter it is, and a page outside the range is only a step.
                var number = decide is null ? index : decide(page);
                if (number is { } keep)
                {
                    await this.RecordSuccessAsync(progress, keep, url, page, decision, attempt, cancellationToken)
                        .ConfigureAwait(false);
                }

                return page;
            }
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task<ChapterPage> LoadAsync(
        string url,
        SelectorSet selectors,
        string? nextSelector,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(this.options.PageBudget);

        ChapterPage page;
        try
        {
            page = await OnlyPageErrors(
                token => nextSelector is null
                    ? this.source.LoadChapterAsync(url, selectors.Title, selectors.Content, token)
                    : this.source.LoadChapterAsync(url, selectors.Title, selectors.Content, nextSelector, token),
                url,
                budget.Token,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PageTimeoutException(
                $"{url} did not settle within {this.options.PageBudget.TotalSeconds}s");
        }

        if (this.options.WaitAfterLoad > TimeSpan.Zero)
        {
            await this.sleep(this.options.WaitAfterLoad, cancellationToken).ConfigureAwait(false);
        }

        return page;
    }

    /// <summary>Exponential backoff with full jitter, capped at the maximum delay.</summary>
    /// <remarks>
    /// Full jitter rather than a fixed multiplier: retries that all failed at
    /// the same moment must not resynchronise on the way back in.
    /// </remarks>
    private TimeSpan Backoff(int attempt)
    {
        var growth = this.options.MinDelay * Math.Pow(2, attempt - 1);
        var ceiling = growth < this.options.MaxDelay ? growth : this.options.MaxDelay;
        if (ceiling <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return ceiling * this.rng.NextDouble();
    }

    private async Task RecordSuccessAsync(
        Progress progress,
        int index,
        string url,
        ChapterPage page,
        RobotsDecision? decision,
        int attempts,
        CancellationToken cancellationToken)
    {
        var cleaned = TextCleaner.Clean(
            page.Body,
            removeLinks: !this.options.IncludeLinks,
            stripAds: this.options.StripAds);

        var record = new ChapterRecord(
            index,
            url,
            page.FinalUrl,
            page.Title,
            cleaned.Text,
            cleaned.StrippedUrls,
            this.now(),
            attempts,
            decision,
            page.NextUrl);

        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress.RecordSuccess(record);
            await this.sink.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            this.onRecord?.Invoke(record);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    private void RecordFailure(Progress progress, int index, string url, PageException exception, int attempts)
    {
        var failure = new FailedChapter(index, url, ReasonFor(exception), exception.Message, attempts);
        progress.RecordFailure(failure);
        this.onFailure?.Invoke(failure);
    }

    private static string ReasonFor(PageException exception) => exception switch
    {
        PageTimeoutException => "timeout",
        PageBlockedException blocked => blocked.Reason.ToWireValue(),
        SelectorNotFoundException => "selector_not_found",
        _ => "error",
    };

    /// <summary>Guarantee that a page-source call throws only a page exception.</summary>
    /// <remarks>
    /// The interface is meant to be the boundary where implementation-specific
    /// failures stop, but nothing enforced it in Python: the runtime's own
    /// timeout escaped on the first pass and took out the whole task group
    /// instead of being retried. A browser-backed source adds a second family of
    /// exceptions, so the guarantee is made structural here rather than left to
    /// each implementation's discipline.
    /// </remarks>
    private static async Task<T> OnlyPageErrors<T>(
        Func<CancellationToken, Task<T>> operation,
        string context,
        CancellationToken operationToken,
        CancellationToken callerToken = default)
    {
        try
        {
            return await operation(operationToken).ConfigureAwait(false);
        }
        catch (PageException)
        {
            throw;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // A cancellation the caller actually asked for stays a cancellation.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new PageTimeoutException($"{context}: timed out");
        }
        catch (TimeoutException exception)
        {
            throw new PageTimeoutException($"{context}: timed out", exception);
        }
        catch (Exception exception)
        {
            throw new PageException($"{context}: {exception.GetType().Name}: {exception.Message}", exception);
        }
    }

    /// <summary>Mutable run state, guarded because .NET continuations really are concurrent.</summary>
    private sealed class Progress
    {
        private readonly ConcurrentDictionary<int, ChapterRecord> completed = new();
        private readonly ConcurrentDictionary<int, FailedChapter> failed = new();

        internal int Count => this.completed.Count + this.failed.Count;

        internal void RecordSuccess(ChapterRecord record) => this.completed[record.Index] = record;

        internal void RecordFailure(FailedChapter failure) => this.failed[failure.Index] = failure;

        internal IReadOnlyList<ChapterRecord> OrderedCompleted() =>
            [.. this.completed.OrderBy(entry => entry.Key).Select(entry => entry.Value)];

        internal IReadOnlyList<FailedChapter> OrderedFailed() =>
            [.. this.failed.OrderBy(entry => entry.Key).Select(entry => entry.Value)];
    }
}

/// <summary>Gaps between consecutive request times. Used by the composition test.</summary>
public static class Observed
{
    public static IReadOnlyList<TimeSpan> Intervals(IEnumerable<TimeSpan> times)
    {
        var ordered = times.Order().ToArray();
        return [.. ordered.Zip(ordered.Skip(1), (earlier, later) => later - earlier)];
    }
}
