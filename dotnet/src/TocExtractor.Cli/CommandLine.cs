using System.CommandLine;
using TocExtractor.Core.Exporters;

namespace TocExtractor.Cli;

/// <summary>The single source of truth for the command-line surface.</summary>
public static class CommandLine
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public static Option<string?> Profile { get; } = new("--profile")
    {
        Description = "TOML file holding the three selectors and optional defaults. "
            + "Flags you pass explicitly override it.",
    };

    public static Option<string> Toc { get; } = new("--toc")
    {
        Description = "TOC URL (must start with http/https)",
        Required = true,
    };

    public static Option<string?> Link { get; } = new("--link")
    {
        Description = "CSS selector for chapter links on the TOC page",
    };

    public static Option<string?> Title { get; } = new("--title")
    {
        Description = "CSS selector for title on a chapter page",
    };

    public static Option<string?> Content { get; } = new("--content")
    {
        Description = "CSS selector for content on a chapter page",
    };

    public static Option<int> Max { get; } = new("--max")
    {
        Description = "Max chapters to fetch (default: 20)",
        DefaultValueFactory = _ => 20,
    };

    public static Option<string> Out { get; } = new("--out")
    {
        Description = "Output folder (default: downloads)",
        DefaultValueFactory = _ => "downloads",
    };

    public static Option<bool> IncludeLinks { get; } = new("--include-links")
    {
        Description = "Include source URL in saved files",
    };

    public static Option<string[]> Formats { get; } = new("--format")
    {
        Description = $"Output format, repeatable (default: {ExporterRegistry.DefaultFormat}). "
            + $"Choices: {string.Join(", ", ExporterRegistry.Available)}. "
            + "For EPUB, export markdown and run: pandoc book.md -o book.epub",
        AllowMultipleArgumentsPerToken = false,
    };

    public static Option<bool> NoStripAds { get; } = new("--no-strip-ads")
    {
        Description = "Do NOT strip common ad markers from text",
    };

    public static Option<bool> DryRun { get; } = new("--dry-run")
    {
        Description = "List discovered chapter URLs and exit",
    };

    public static Option<bool> Force { get; } = new("--force")
    {
        Description = "Discard any saved progress and start over (resume is the default)",
    };

    public static Option<bool> DumpHtml { get; } = new("--dump-html")
    {
        Description = "Save TOC HTML to out/toc.html",
    };

    public static Option<bool> Screenshot { get; } = new("--screenshot")
    {
        Description = "Save TOC screenshot to out/toc.png",
    };

    public static Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Show debug output",
    };

    public static Option<bool> Quiet { get; } = new("--quiet", "-q")
    {
        Description = "Show warnings and errors only",
    };

    public static Option<string?> UserAgent { get; } = new("--ua")
    {
        Description = "Custom User-Agent string. Also the agent robots.txt is evaluated for.",
    };

    public static Option<string?> StorageState { get; } = new("--storage-state")
    {
        Description = "Path to Playwright storage state JSON (reuses login)",
    };

    public static Option<bool> Headful { get; } = new("--headful")
    {
        Description = "Run headed (GUI). Default is headless.",
    };

    public static Option<int> Timeout { get; } = new("--timeout")
    {
        Description = "Budget for one chapter attempt, in ms (default: 25000). "
            + "Covers navigation and both selector reads together.",
        DefaultValueFactory = _ => 25000,
    };

    public static Option<double> MinDelay { get; } = new("--min-delay")
    {
        Description = "Min delay between chapters (s)",
        DefaultValueFactory = _ => 1.2,
    };

    public static Option<double> MaxDelay { get; } = new("--max-delay")
    {
        Description = "Max delay between chapters (s)",
        DefaultValueFactory = _ => 2.5,
    };

    public static Option<int> Retries { get; } = new("--retries")
    {
        Description = "Retries per chapter on errors (default: 2)",
        DefaultValueFactory = _ => 2,
    };

    public static Option<int> WaitAfterLoad { get; } = new("--wait-after-load")
    {
        Description = "Extra settle wait per page (ms)",
        DefaultValueFactory = _ => 500,
    };

    public static Option<int> Concurrency { get; } = new("--concurrency")
    {
        Description = "Chapters fetched at once (default: 3). The per-host delay still applies.",
        DefaultValueFactory = _ => 3,
    };

    public static Option<bool> AllowPrivateHosts { get; } = new("--allow-private-hosts")
    {
        Description = "Permit hosts resolving to loopback or private ranges (for local testing)",
    };

    /// <summary>Every option, in the order the help text should present them.</summary>
    public static IReadOnlyList<Option> All { get; } =
    [
        Profile, Toc, Link, Title, Content,
        Max, Out, IncludeLinks, Formats, NoStripAds,
        DryRun, Force,
        DumpHtml, Screenshot, Verbose, Quiet,
        UserAgent, StorageState, Headful, Timeout,
        MinDelay, MaxDelay, Retries, WaitAfterLoad, Concurrency, AllowPrivateHosts,
    ];

    public static RootCommand Build()
    {
        RootCommand root = new(
            "Extract chapter text from a table-of-contents page using CSS selectors you "
            + "supply. Use only on content you own or are permitted to access.");

        foreach (var option in All)
        {
            root.Options.Add(option);
        }

        return root;
    }

    /// <summary>The long options actually typed on the command line.</summary>
    /// <remarks>
    /// Comparing a parsed value against its default cannot tell "not given"
    /// from "given, and happens to equal the default", so a profile would
    /// override a flag the user did pass. Reading the arguments answers the
    /// question directly.
    /// </remarks>
    public static HashSet<string> ExplicitFlags(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        HashSet<string> given = new(StringComparer.Ordinal);
        foreach (var token in args)
        {
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                given.Add(token.Split('=', 2)[0]);
            }
        }

        return given;
    }
}
