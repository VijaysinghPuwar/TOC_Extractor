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

    /// <summary>
    /// When the title selector matches nothing on a page whose story is
    /// there, use the page's own title rather than fail the chapter. For the
    /// desktop app, whose selectors come from its own scan of one sample
    /// chapter; a selector a person typed should still fail loudly.
    /// </summary>
    public bool TitleFallback { get; init; }

    /// <summary>
    /// Skip pictures, video, audio and web fonts while the app reads on its
    /// own. Only the text is saved, and on ad-heavy sites those are most of
    /// the page's weight. Style sheets still load, because what counts as
    /// visible text depends on them. Never while a person is using the
    /// window or a tab is showing a check.
    /// </summary>
    public bool LightPages { get; init; }

    /// <summary>
    /// When set, a tab nobody has used for this long is closed, down to one,
    /// and the one left is emptied so a page's ads stop running while it
    /// waits. Only with <see cref="GrowTo"/>, where tabs are opened on demand.
    /// </summary>
    public TimeSpan? IdleTabsCloseAfter { get; init; }

    /// <summary>
    /// Whether memory is short right now, asked before opening another tab.
    /// While it is, work waits for a tab to come free instead. Null never waits.
    /// </summary>
    public Func<bool>? MemoryTight { get; init; }
}
