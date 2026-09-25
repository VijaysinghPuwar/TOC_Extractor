using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Exporters;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;

namespace TocExtractor.App.Pipeline;

/// <summary>What one extraction needs to know, independent of the front end.</summary>
public sealed record PipelineRequest
{
    public required string TocUrl { get; init; }

    public required SelectorSet Selectors { get; init; }

    public required string OutputDirectory { get; init; }

    public required IReadOnlyList<string> Formats { get; init; }

    public required FetchOptions Fetch { get; init; }

    public bool Force { get; init; }

    public bool DumpHtml { get; init; }

    /// <summary>Suppresses the informational lines; warnings and failures still arrive.</summary>
    public bool Quiet { get; init; }
}

/// <summary>How a pipeline run ended, in terms a front end can map to its own vocabulary.</summary>
public enum PipelineOutcome
{
    /// <summary>Every attempted chapter was written, or a dry run listed the links.</summary>
    Ok,

    /// <summary>Refused before fetching anything: the input or saved progress cannot be used.</summary>
    Refused,

    /// <summary>Ran, but the table of contents could not be read or some chapters failed.</summary>
    Failed,
}

public sealed record PipelineResult(
    PipelineOutcome Outcome,
    CollectedLinks? Collected = null,
    RunResult? Run = null,
    ResumePlan? Resume = null);

/// <summary>Receives everything a run reports. Every member has a do-nothing default.</summary>
/// <remarks>
/// Called from thread-pool threads: the fetch loop runs chapters concurrently.
/// A UI observer marshals to its own thread; the CLI's writes a line.
/// </remarks>
public interface IPipelineObserver
{
    void Log(string line)
    {
    }

    void Collected(CollectedLinks collected)
    {
    }

    /// <summary>Links an earlier run already wrote, so a UI can mark them before fetching starts.</summary>
    void Resuming(ResumePlan plan, IReadOnlyList<string> alreadyDone)
    {
    }

    void Record(ChapterRecord record)
    {
    }

    void Failure(FailedChapter failure)
    {
    }

    /// <summary>One step of fetching a chapter. Meant for a detailed log, not a window.</summary>
    void Trace(FetchTrace trace)
    {
    }
}

/// <summary>
/// One extraction, from the table of contents to files on disk: collect the
/// links, consult saved progress, fetch, export, checkpoint.
/// </summary>
/// <remarks>
/// The command line and the desktop app both run this, so resume rules,
/// checkpointing and exporter wiring exist once. Each front end owns only how
/// it starts the browser and how it shows what happened.
/// </remarks>
public static class ExtractionPipeline
{
    public static async Task<PipelineResult> RunAsync(
        PipelineRequest request,
        IPageSource source,
        UrlGuard guard,
        RobotsPolicy robots,
        RateLimiter limiter,
        IPipelineObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(observer);

        Checkpoint? checkpoint = null;
        ISink sink = new NullSink();

        void Persist(ChapterRecord record)
        {
            checkpoint?.Record(record, sink.OutputsFor(record.Index));
            checkpoint?.Save();
            observer.Record(record);
        }

        using var fetcher = new Fetcher(
            source, guard, sink, request.Fetch, limiter, robots,
            onRecord: Persist, onFailure: observer.Failure);

        CollectedLinks collected;
        try
        {
            collected = await fetcher.CollectAsync(request.TocUrl, request.Selectors, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PageException exception)
        {
            observer.Log($"could not read the table of contents: {exception.Message}");
            return new PipelineResult(PipelineOutcome.Failed);
        }

        observer.Collected(collected);
        if (!request.Quiet)
        {
            observer.Log($"Found {collected.Collection.Kept.Count} chapter link(s).");
            foreach (var reason in collected.Collection.ReasonCounts().OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                observer.Log($"skipped {reason.Value} link(s): {reason.Key}");
            }

            if (collected.Collection.Truncated > 0)
            {
                observer.Log($"Ignoring {collected.Collection.Truncated} link(s) beyond --max.");
            }
        }

        // Before the dry-run exit: --dump-html --dry-run is the documented
        // first step against a new site, and it has to produce the file.
        if (request.DumpHtml && collected.Toc.Html is not null)
        {
            Directory.CreateDirectory(request.OutputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(request.OutputDirectory, "toc.html"), collected.Toc.Html, cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.Fetch.DryRun)
        {
            return new PipelineResult(PipelineOutcome.Ok, collected);
        }

        var plan = ResumePlanner.Plan(
            request.OutputDirectory, request.TocUrl, request.Selectors,
            collected.Collection.Kept, request.Force, observer.Log);

        Func<string, bool>? alreadyDone = null;
        if (plan is not null)
        {
            if (!plan.Usable)
            {
                observer.Log($"refusing to resume: {plan.Refusal}");
                observer.Log($"checkpoint: {plan.Checkpoint.Path}");
                return new PipelineResult(PipelineOutcome.Refused, collected, Resume: plan);
            }

            checkpoint = plan.Checkpoint;
            alreadyDone = checkpoint.IsDone;
            observer.Resuming(plan, [.. collected.Collection.Kept.Where(checkpoint.IsDone)]);

            if (!request.Quiet)
            {
                observer.Log(
                    $"Resuming: {plan.AlreadyDone} chapter(s) already fetched, "
                    + $"state file {plan.Checkpoint.Path}");
                if (plan.Appended.Count > 0)
                {
                    observer.Log(
                        $"The table of contents grew by {plan.Appended.Count} chapter(s) since that run.");
                }
            }

            if (plan.Renumbering)
            {
                observer.Log(
                    "New chapters were added to the start of the table of contents. Files already "
                    + "written keep their original numbers, so numbering now reflects the order "
                    + "chapters were fetched, not their order in the table of contents. Use --force "
                    + "for a run numbered by the current table of contents.");
            }
        }

        checkpoint ??= new Checkpoint
        {
            Path = Checkpoint.PathFor(request.OutputDirectory),
            TocUrl = request.TocUrl,
            Fingerprint = Checkpoint.FingerprintOf(request.TocUrl, request.Selectors),
            Selectors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = request.Selectors.Link,
                ["title"] = request.Selectors.Title,
                ["content"] = request.Selectors.Content,
            },
        };

        checkpoint.LinkSet = [.. collected.Collection.Kept];

        // Snapshotted before this run starts adding entries: the exporters need
        // to know what an earlier run wrote, which is only known once the
        // checkpoint has been consulted.
        var resumed = checkpoint.AsPriorChapters();
        sink = ExporterRegistry.Build(
            request.Formats, request.OutputDirectory, request.Fetch.IncludeLinks, resumed);
        fetcher.SetSink(sink);

        var result = await fetcher
            .FetchAsync(collected, request.Selectors, alreadyDone, cancellationToken)
            .ConfigureAwait(false);
        checkpoint.Save();

        if (!request.Quiet)
        {
            observer.Log($"Wrote {result.Completed.Count} chapter(s) to {request.OutputDirectory}");
            if (result.TotalStrippedUrls > 0)
            {
                observer.Log(
                    $"Removed {result.TotalStrippedUrls} URL(s) from chapter text. "
                    + "Pass --include-links to keep them.");
            }
        }

        foreach (var failure in result.Failed)
        {
            observer.Log(
                $"chapter {failure.Index} failed after {failure.Attempts} attempt(s): {failure.Detail}");
        }

        return new PipelineResult(
            result.Failed.Count > 0 ? PipelineOutcome.Failed : PipelineOutcome.Ok,
            collected,
            result,
            plan);
    }
}
