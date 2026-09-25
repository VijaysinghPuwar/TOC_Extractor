namespace TocExtractor.Core.Fetching;

/// <summary>Everything the fetch loop needs that is not a collaborator.</summary>
public sealed record FetchOptions
{
    public int Concurrency { get; init; } = 3;

    public int Retries { get; init; } = 2;

    /// <summary>
    /// The wall-clock cap on one chapter attempt, end to end.
    /// </summary>
    /// <remarks>
    /// Named a budget rather than a timeout on purpose. It covers navigation,
    /// both selector reads, and anything else the page source does for one
    /// attempt — the page source is handed a token carrying this deadline and
    /// must not impose one of its own. Python calls the same value "timeout" and
    /// then applies it separately to the navigation, to each selector wait, and
    /// to the whole operation, so the three nest and only the outermost is ever
    /// reached.
    /// </remarks>
    public TimeSpan PageBudget { get; init; } = TimeSpan.FromSeconds(25);

    public TimeSpan MinDelay { get; init; } = TimeSpan.FromSeconds(1.2);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Extra settle wait after a page loads, outside the budget above.</summary>
    public TimeSpan WaitAfterLoad { get; init; } = TimeSpan.FromSeconds(0.5);

    public bool IncludeLinks { get; init; }

    public bool StripAds { get; init; } = true;

    public int? MaxLinks { get; init; }

    public bool DryRun { get; init; }

    public bool CaptureHtml { get; init; }

    public string? ScreenshotPath { get; init; }

    /// <summary>Whether the human gate was passed with evidence of a session.</summary>
    public bool SessionAuthenticated { get; init; }
}
