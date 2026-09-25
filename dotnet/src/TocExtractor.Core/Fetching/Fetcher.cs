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
        Action<FailedChapter>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(sink);

        this.source = source;
        this.guard = guard;
        this.sink = sink;
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

    public void Dispose() => this.writeGate.Dispose();

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

    private async Task FetchOneAsync(
        int index,
        string url,
        RobotsDecision? decision,
        SelectorSet selectors,
        SemaphoreSlim slots,
        Progress progress,
        CancellationToken cancellationToken)
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
                    page = await this.LoadAsync(url, selectors, cancellationToken).ConfigureAwait(false);
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
                    return;
                }
                catch (Exception exception)
                    when (exception is PageBlockedException or SelectorNotFoundException)
                {
                    // Neither is worth retrying: the target is disallowed, or the
                    // page loaded fine and simply lacks the selector.
                    this.RecordFailure(progress, index, url, (PageException)exception, attempt);
                    return;
                }
                catch (PageException exception)
                {
                    if (attempt > this.options.Retries)
                    {
                        this.RecordFailure(progress, index, url, exception, attempt);
                        return;
                    }

                    await this.sleep(this.Backoff(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await this.RecordSuccessAsync(progress, index, url, page, decision, attempt, cancellationToken)
                    .ConfigureAwait(false);
                return;
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
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(this.options.PageBudget);

        ChapterPage page;
        try
        {
            page = await OnlyPageErrors(
                token => this.source.LoadChapterAsync(url, selectors.Title, selectors.Content, token),
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
            decision);

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
