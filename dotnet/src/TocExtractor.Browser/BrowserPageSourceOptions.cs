namespace TocExtractor.Browser;

/// <summary>How the browser-backed page source is configured.</summary>
public sealed record BrowserPageSourceOptions
{
    public bool Headless { get; init; } = true;

    public string? UserAgent { get; init; }

    /// <summary>Path to a stored session, so a previous sign-in is reused.</summary>
    public string? StorageStatePath { get; init; }

    /// <summary>A persistent profile directory, so a manual sign-in survives between runs.</summary>
    public string? UserDataDirectory { get; init; }

    /// <summary>
    /// The cap on one whole operation: navigation and both selector reads share
    /// it rather than each getting a fresh copy.
    /// </summary>
    public TimeSpan OperationBudget { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// One page per concurrent worker. Two navigations on a shared page abort
    /// each other, which no stub can model and only a live server exposes.
    /// </summary>
    public int MaxPages { get; init; } = 1;

    /// <summary>
    /// When set, the pages opened at start are only the first ones: whenever
    /// every page is busy another is opened, up to this many. For the desktop
    /// app, where several books share one browser.
    /// </summary>
    public int? GrowTo { get; init; }
}
