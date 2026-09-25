using TocExtractor.Browser;
using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Exporters;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Models;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;

namespace TocExtractor.Cli;

/// <summary>Wires one extraction together and reports what happened.</summary>
public static class Extraction
{
    public static async Task<int> RunAsync(
        RunSettings settings,
        Func<UrlGuard, BrowserPageSourceOptions, Task<IPageSource>>? sourceFactory = null,
        Func<string, string, string?>? robotsFetcher = null,
        TextWriter? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var report = log ?? Console.Error;

        if (!settings.Selectors.Complete)
        {
            report.WriteLine(
                "missing selector(s): "
                + string.Join(", ", settings.Selectors.Missing.Select(name => "--" + name))
                + ". Pass them as flags or in a --profile file.");
            return CommandLine.ExitUsage;
        }

        var guard = new UrlGuard(
            settings.AllowPrivateHosts,
            new CachingHostResolver(SystemHostResolver.Instance));

        var verdict = guard.Check(settings.TocUrl);
        if (!verdict.Allowed)
        {
            report.WriteLine(
                $"refusing to fetch {settings.TocUrl}: {verdict.Reason?.ToWireValue()} {verdict.Detail}");
            return CommandLine.ExitUsage;
        }

        // The agent robots.txt is evaluated for is the agent the browser sends.
        // Python always evaluates for its own default while the browser
        // identifies as whatever --ua set, so a custom agent gets decisions
        // meant for a different one.
        var fetchRobots = robotsFetcher ?? RobotsHttp.Fetch;
        var body = fetchRobots(Robots.LocationFor(settings.TocUrl), settings.UserAgent);
        var robots = body is null
            ? RobotsPolicy.Missing(Robots.OriginOf(settings.TocUrl), settings.UserAgent)
            : RobotsPolicy.Parse(body, Robots.OriginOf(settings.TocUrl), settings.UserAgent);

        var limiter = new RateLimiter(settings.Fetch.MinDelay);
        if (robots.CrawlDelay is { } delay)
        {
            limiter.SetHostInterval(new Uri(settings.TocUrl), delay);
            if (!settings.Quiet)
            {
                report.WriteLine($"robots.txt requests a {delay.TotalSeconds:0.0}s crawl delay; honouring it");
            }
        }

        var browserOptions = new BrowserPageSourceOptions
        {
            Headless = !settings.Headful,
            UserAgent = settings.UserAgent == Robots.DefaultUserAgent ? null : settings.UserAgent,
            StorageStatePath = settings.StorageStatePath,
            OperationBudget = settings.Fetch.PageBudget,
            MaxPages = settings.Fetch.Concurrency,
        };

        var source = sourceFactory is null
            ? await BrowserPageSource.StartAsync(guard, browserOptions, cancellationToken).ConfigureAwait(false)
            : await sourceFactory(guard, browserOptions).ConfigureAwait(false);

        await using (source.ConfigureAwait(false))
        {
            return await ExtractAsync(settings, source, guard, robots, limiter, report, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<int> ExtractAsync(
        RunSettings settings,
        IPageSource source,
        UrlGuard guard,
        RobotsPolicy robots,
        RateLimiter limiter,
        TextWriter report,
        CancellationToken cancellationToken)
    {
        Checkpoint? checkpoint = null;
        ISink sink = new NullSink();

        void Persist(ChapterRecord record)
        {
            checkpoint?.Record(record, sink.OutputsFor(record.Index));
            checkpoint?.Save();
        }

        using var fetcher = new Fetcher(
            source, guard, sink, settings.Fetch, limiter, robots, onRecord: Persist);

        CollectedLinks collected;
        try
        {
            collected = await fetcher.CollectAsync(settings.TocUrl, settings.Selectors, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PageException exception)
        {
            report.WriteLine($"could not read the table of contents: {exception.Message}");
            return CommandLine.ExitFailed;
        }

        if (!settings.Quiet)
        {
            report.WriteLine($"Found {collected.Collection.Kept.Count} chapter link(s).");
            foreach (var reason in collected.Collection.ReasonCounts().OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                report.WriteLine($"skipped {reason.Value} link(s): {reason.Key}");
            }

            if (collected.Collection.Truncated > 0)
            {
                report.WriteLine($"Ignoring {collected.Collection.Truncated} link(s) beyond --max.");
            }
        }

        if (settings.Fetch.DryRun)
        {
            for (var i = 0; i < collected.Collection.Kept.Count; i++)
            {
                Console.WriteLine($"{i + 1:D3}  {collected.Collection.Kept[i]}");
            }

            return CommandLine.ExitOk;
        }

        if (settings.DumpHtml && collected.Toc.Html is not null)
        {
            Directory.CreateDirectory(settings.OutputDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(settings.OutputDirectory, "toc.html"), collected.Toc.Html, cancellationToken)
                .ConfigureAwait(false);
        }

        var plan = ResumePlanner.Plan(
            settings.OutputDirectory, settings.TocUrl, settings.Selectors,
            collected.Collection.Kept, settings.Force, report.WriteLine);

        Func<string, bool>? alreadyDone = null;
        if (plan is not null)
        {
            if (!plan.Usable)
            {
                report.WriteLine($"refusing to resume: {plan.Refusal}");
                report.WriteLine($"checkpoint: {plan.Checkpoint.Path}");
                return CommandLine.ExitUsage;
            }

            checkpoint = plan.Checkpoint;
            alreadyDone = checkpoint.IsDone;

            if (!settings.Quiet)
            {
                report.WriteLine(
                    $"Resuming: {plan.AlreadyDone} chapter(s) already fetched, "
                    + $"state file {plan.Checkpoint.Path}");
                if (plan.Appended.Count > 0)
                {
                    report.WriteLine(
                        $"The table of contents grew by {plan.Appended.Count} chapter(s) since that run.");
                }
            }

            if (plan.Renumbering)
            {
                report.WriteLine(
                    "New chapters were added to the start of the table of contents. Files already "
                    + "written keep their original numbers, so numbering now reflects the order "
                    + "chapters were fetched, not their order in the table of contents. Use --force "
                    + "for a run numbered by the current table of contents.");
            }
        }

        checkpoint ??= new Checkpoint
        {
            Path = Checkpoint.PathFor(settings.OutputDirectory),
            TocUrl = settings.TocUrl,
            Fingerprint = Checkpoint.FingerprintOf(settings.TocUrl, settings.Selectors),
            Selectors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = settings.Selectors.Link,
                ["title"] = settings.Selectors.Title,
                ["content"] = settings.Selectors.Content,
            },
        };

        checkpoint.LinkSet = [.. collected.Collection.Kept];

        // Snapshotted before this run starts adding entries: the exporters need
        // to know what an earlier run wrote, which is only known once the
        // checkpoint has been consulted.
        var resumed = checkpoint.AsPriorChapters();
        sink = ExporterRegistry.Build(
            settings.Formats, settings.OutputDirectory, settings.Fetch.IncludeLinks, resumed);
        fetcher.SetSink(sink);

        var result = await fetcher
            .FetchAsync(collected, settings.Selectors, alreadyDone, cancellationToken)
            .ConfigureAwait(false);
        checkpoint.Save();

        if (!settings.Quiet)
        {
            report.WriteLine($"Wrote {result.Completed.Count} chapter(s) to {settings.OutputDirectory}");
            if (result.TotalStrippedUrls > 0)
            {
                report.WriteLine(
                    $"Removed {result.TotalStrippedUrls} URL(s) from chapter text. "
                    + "Pass --include-links to keep them.");
            }
        }

        foreach (var failure in result.Failed)
        {
            report.WriteLine(
                $"chapter {failure.Index} failed after {failure.Attempts} attempt(s): {failure.Detail}");
        }

        return result.Failed.Count > 0 ? CommandLine.ExitFailed : CommandLine.ExitOk;
    }
}
