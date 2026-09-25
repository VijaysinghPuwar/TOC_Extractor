using TocExtractor.App;
using TocExtractor.App.Pipeline;
using TocExtractor.Browser;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;

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

        var fetchRobots = robotsFetcher ?? RobotsHttp.Fetch;
        var (robots, limiter, _) = RobotsSetup.Prepare(
            settings.TocUrl,
            settings.UserAgent,
            settings.Fetch.MinDelay,
            fetchRobots,
            settings.Quiet ? null : report.WriteLine);

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
        var request = new PipelineRequest
        {
            TocUrl = settings.TocUrl,
            Selectors = settings.Selectors,
            OutputDirectory = settings.OutputDirectory,
            Formats = settings.Formats,
            Fetch = settings.Fetch,
            Force = settings.Force,
            DumpHtml = settings.DumpHtml,
            Quiet = settings.Quiet,
        };

        var result = await ExtractionPipeline
            .RunAsync(request, source, guard, robots, limiter, new WriterObserver(report), cancellationToken)
            .ConfigureAwait(false);

        if (settings.Fetch.DryRun && result.Outcome == PipelineOutcome.Ok && result.Collected is { } collected)
        {
            for (var i = 0; i < collected.Collection.Kept.Count; i++)
            {
                Console.WriteLine($"{i + 1:D3}  {collected.Collection.Kept[i]}");
            }
        }

        return result.Outcome switch
        {
            PipelineOutcome.Ok => CommandLine.ExitOk,
            PipelineOutcome.Refused => CommandLine.ExitUsage,
            _ => CommandLine.ExitFailed,
        };
    }

    private sealed class WriterObserver(TextWriter writer) : IPipelineObserver
    {
        public void Log(string line) => writer.WriteLine(line);
    }
}
