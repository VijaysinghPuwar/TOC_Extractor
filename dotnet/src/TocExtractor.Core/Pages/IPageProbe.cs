namespace TocExtractor.Core.Pages;

/// <summary>Runs a detection script on a page and returns its answer.</summary>
/// <remarks>
/// The scanner's view of a browser. Kept apart from <see cref="IPageSource"/>
/// because fetching chapters never needs it, and so the scanner can be tested
/// against canned answers without a browser.
/// </remarks>
public interface IPageProbe
{
    /// <summary>Open <paramref name="url"/>, run <paramref name="script"/> until its answer settles.</summary>
    /// <returns>The final URL after redirects, and the script's answer as JSON.</returns>
    Task<(string FinalUrl, string Json)> ProbeAsync(
        string url,
        string script,
        TimeSpan settle,
        CancellationToken cancellationToken = default);
}
