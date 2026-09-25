using TocExtractor.App.Pipeline;
using TocExtractor.App.Profiles;
using TocExtractor.Core.Exporters;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Links;
using TocExtractor.Core.Politeness;

namespace TocExtractor.App.Session;

/// <summary>Everything the desktop app asks for, validated in one place.</summary>
public sealed record SessionSettings
{
    public string TocUrl { get; init; } = "";

    public string LinkSelector { get; init; } = "";

    public string TitleSelector { get; init; } = "";

    public string ContentSelector { get; init; } = "";

    public string OutputDirectory { get; init; } = AppPaths.DefaultOutputDirectory;

    public IReadOnlyList<string> Formats { get; init; } = [ExporterRegistry.DefaultFormat];

    public int MaxChapters { get; init; } = 20;

    public int Concurrency { get; init; } = 3;

    public int Retries { get; init; } = 2;

    public double MinDelaySeconds { get; init; } = 1.2;

    public double MaxDelaySeconds { get; init; } = 2.5;

    public bool IncludeLinks { get; init; }

    public bool StripAds { get; init; } = true;

    /// <summary>Discard saved progress and start over.</summary>
    public bool Force { get; init; }

    public string UserAgent { get; init; } = Robots.DefaultUserAgent;

    public TimeSpan PageBudget { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>For the test fixture server only. Never offered in the window.</summary>
    public bool AllowPrivateHosts { get; init; }

    public SelectorSet Selectors => SelectorSet.Create(this.LinkSelector, this.TitleSelector, this.ContentSelector);

    /// <summary>Every reason these settings cannot run, in the order the form shows the fields.</summary>
    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];

        if (!Uri.TryCreate(this.TocUrl.Trim(), UriKind.Absolute, out var toc)
            || (toc.Scheme != Uri.UriSchemeHttp && toc.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add("Enter the contents page address, starting with https://");
        }

        foreach (var missing in this.Selectors.Missing)
        {
            problems.Add($"Enter the {missing} selector");
        }

        if (string.IsNullOrWhiteSpace(this.OutputDirectory))
        {
            problems.Add("Choose an output folder");
        }

        if (this.Formats.Count == 0)
        {
            problems.Add("Pick at least one output format");
        }

        var unknown = this.Formats.Except(ExporterRegistry.Available, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            problems.Add($"Unknown format: {string.Join(", ", unknown)}");
        }

        if (this.MaxChapters < 1)
        {
            problems.Add("Max chapters must be at least 1");
        }

        if (this.Concurrency is < 1 or > 8)
        {
            problems.Add("Chapters at once must be between 1 and 8");
        }

        if (this.Retries is < 0 or > 10)
        {
            problems.Add("Retries must be between 0 and 10");
        }

        if (this.MinDelaySeconds < 0 || this.MaxDelaySeconds < this.MinDelaySeconds)
        {
            problems.Add("The delay range must be positive, with the maximum at least the minimum");
        }

        return problems;
    }

    public FetchOptions ToFetchOptions(bool sessionAuthenticated, bool dryRun = false) => new()
    {
        Concurrency = this.Concurrency,
        Retries = this.Retries,
        PageBudget = this.PageBudget,
        MinDelay = TimeSpan.FromSeconds(this.MinDelaySeconds),
        MaxDelay = TimeSpan.FromSeconds(this.MaxDelaySeconds),
        IncludeLinks = this.IncludeLinks,
        StripAds = this.StripAds,
        MaxLinks = this.MaxChapters,
        DryRun = dryRun,
        SessionAuthenticated = sessionAuthenticated,
    };

    public PipelineRequest ToRequest(bool sessionAuthenticated) => new()
    {
        TocUrl = this.TocUrl.Trim(),
        Selectors = this.Selectors,
        OutputDirectory = this.OutputDirectory,
        Formats = this.Formats,
        Fetch = this.ToFetchOptions(sessionAuthenticated),
        Force = this.Force,
    };

    /// <summary>These settings as a profile, in the same TOML the command line reads.</summary>
    public Profile ToProfile(string path) => new()
    {
        Path = path,
        Link = this.LinkSelector,
        Title = this.TitleSelector,
        Content = this.ContentSelector,
        Max = this.MaxChapters,
        Concurrency = this.Concurrency,
        Retries = this.Retries,
        MinDelay = this.MinDelaySeconds,
        MaxDelay = this.MaxDelaySeconds,
        IncludeLinks = this.IncludeLinks,
        Formats = this.Formats,
    };

    /// <summary>Apply a loaded profile over these settings. Values it does not set are kept.</summary>
    public SessionSettings With(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return this with
        {
            LinkSelector = profile.Link ?? this.LinkSelector,
            TitleSelector = profile.Title ?? this.TitleSelector,
            ContentSelector = profile.Content ?? this.ContentSelector,
            OutputDirectory = profile.Out ?? this.OutputDirectory,
            MaxChapters = profile.Max ?? this.MaxChapters,
            Concurrency = profile.Concurrency ?? this.Concurrency,
            Retries = profile.Retries ?? this.Retries,
            MinDelaySeconds = profile.MinDelay ?? this.MinDelaySeconds,
            MaxDelaySeconds = profile.MaxDelay ?? this.MaxDelaySeconds,
            IncludeLinks = profile.IncludeLinks ?? this.IncludeLinks,
            Formats = profile.Formats ?? this.Formats,
            UserAgent = profile.UserAgent ?? this.UserAgent,
            PageBudget = profile.Timeout is { } ms ? TimeSpan.FromMilliseconds(ms) : this.PageBudget,
        };
    }
}
