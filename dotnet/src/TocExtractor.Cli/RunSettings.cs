using TocExtractor.App.Profiles;
using System.CommandLine;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Links;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Cli;

/// <summary>Everything one run needs, after flags and profile have been merged.</summary>
public sealed record RunSettings
{
    public required string TocUrl { get; init; }

    public required SelectorSet Selectors { get; init; }

    public required string OutputDirectory { get; init; }

    public required IReadOnlyList<string> Formats { get; init; }

    public required FetchOptions Fetch { get; init; }

    public required string UserAgent { get; init; }

    public string? StorageStatePath { get; init; }

    public bool Headful { get; init; }

    public bool AllowPrivateHosts { get; init; }

    public bool Force { get; init; }

    public bool Verbose { get; init; }

    public bool Quiet { get; init; }

    public bool DumpHtml { get; init; }

    /// <summary>What the profile supplied, for the debug line that says so.</summary>
    public IReadOnlyList<string> AppliedFromProfile { get; init; } = [];

    public static RunSettings From(ParseResult parsed, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var given = CommandLine.ExplicitFlags(args);
        var profilePath = parsed.GetValue(CommandLine.Profile);
        var profile = profilePath is null ? null : ProfileLoader.Load(profilePath);
        List<string> applied = [];

        // One helper per value kind. A single generic would have to express
        // "T or its nullable form" for both reference and value types, which C#
        // cannot infer, and the explicit versions read better anyway.
        string? Text(Option<string?> option, string flag, Func<Profile, string?> fromProfile, string key)
        {
            if (given.Contains(flag) || profile is null || fromProfile(profile) is not { } value)
            {
                return parsed.GetValue(option);
            }

            applied.Add(key);
            return value;
        }

        string Required(Option<string> option, string flag, Func<Profile, string?> fromProfile, string key)
        {
            if (given.Contains(flag) || profile is null || fromProfile(profile) is not { } value)
            {
                return parsed.GetValue(option)!;
            }

            applied.Add(key);
            return value;
        }

        int Number(Option<int> option, string flag, Func<Profile, int?> fromProfile, string key)
        {
            if (given.Contains(flag) || profile is null || fromProfile(profile) is not { } value)
            {
                return parsed.GetValue(option);
            }

            applied.Add(key);
            return value;
        }

        double Fractional(Option<double> option, string flag, Func<Profile, double?> fromProfile, string key)
        {
            if (given.Contains(flag) || profile is null || fromProfile(profile) is not { } value)
            {
                return parsed.GetValue(option);
            }

            applied.Add(key);
            return value;
        }

        bool Flag(Option<bool> option, string flag, Func<Profile, bool?> fromProfile, string key)
        {
            if (given.Contains(flag) || profile is null || fromProfile(profile) is not { } value)
            {
                return parsed.GetValue(option);
            }

            applied.Add(key);
            return value;
        }

        var selectors = SelectorSet.Create(
            Text(CommandLine.Link, "--link", p => p.Link, "link") ?? "",
            Text(CommandLine.Title, "--title", p => p.Title, "title") ?? "",
            Text(CommandLine.Content, "--content", p => p.Content, "content") ?? "");

        var outputDirectory = Required(CommandLine.Out, "--out", p => p.Out, "out");
        var dryRun = parsed.GetValue(CommandLine.DryRun);
        var screenshot = parsed.GetValue(CommandLine.Screenshot);

        var formats = given.Contains("--format") || profile?.Formats is null
            ? parsed.GetValue(CommandLine.Formats) ?? []
            : Applied(profile.Formats, "formats");

        IReadOnlyList<string> Applied(IReadOnlyList<string> value, string key)
        {
            applied.Add(key);
            return value;
        }

        var minDelay = Fractional(CommandLine.MinDelay, "--min-delay", p => p.MinDelay, "min_delay");
        var maxDelay = Fractional(CommandLine.MaxDelay, "--max-delay", p => p.MaxDelay, "max_delay");

        var fetch = new FetchOptions
        {
            Concurrency = Math.Max(1, Number(CommandLine.Concurrency, "--concurrency", p => p.Concurrency, "concurrency")),
            Retries = Math.Max(0, Number(CommandLine.Retries, "--retries", p => p.Retries, "retries")),
            PageBudget = TimeSpan.FromMilliseconds(
                Math.Max(1, Number(CommandLine.Timeout, "--timeout", p => p.Timeout, "timeout"))),
            MinDelay = TimeSpan.FromSeconds(Math.Max(0, minDelay)),
            MaxDelay = TimeSpan.FromSeconds(Math.Max(Math.Max(0, minDelay), maxDelay)),
            WaitAfterLoad = TimeSpan.FromMilliseconds(
                Math.Max(0, Number(CommandLine.WaitAfterLoad, "--wait-after-load", p => p.WaitAfterLoad, "wait_after_load"))),
            IncludeLinks = Flag(CommandLine.IncludeLinks, "--include-links", p => p.IncludeLinks, "include_links"),
            StripAds = !parsed.GetValue(CommandLine.NoStripAds),
            MaxLinks = Math.Max(1, Number(CommandLine.Max, "--max", p => p.Max, "max")),
            DryRun = dryRun,
            CaptureHtml = parsed.GetValue(CommandLine.DumpHtml) && !dryRun,
            // A dry run must not create the output directory, let alone write
            // into it. Python takes the screenshot while collecting, before the
            // dry-run check, so the two flags together leave a toc.png behind
            // from a run that claims to have written nothing.
            ScreenshotPath = screenshot && !dryRun
                ? Path.Combine(outputDirectory, "toc.png")
                : null,
        };

        return new RunSettings
        {
            TocUrl = parsed.GetValue(CommandLine.Toc)!,
            Selectors = selectors,
            OutputDirectory = outputDirectory,
            Formats = formats,
            Fetch = fetch,
            UserAgent = Text(CommandLine.UserAgent, "--ua", p => p.UserAgent, "ua")
                ?? Robots.DefaultUserAgent,
            StorageStatePath = parsed.GetValue(CommandLine.StorageState),
            Headful = parsed.GetValue(CommandLine.Headful),
            AllowPrivateHosts = parsed.GetValue(CommandLine.AllowPrivateHosts),
            Force = parsed.GetValue(CommandLine.Force),
            Verbose = parsed.GetValue(CommandLine.Verbose),
            Quiet = parsed.GetValue(CommandLine.Quiet),
            DumpHtml = parsed.GetValue(CommandLine.DumpHtml),
            AppliedFromProfile = applied,
        };
    }
}
