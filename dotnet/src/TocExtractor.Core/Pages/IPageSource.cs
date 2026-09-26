namespace TocExtractor.Core.Pages;

/// <summary>A loaded table-of-contents page.</summary>
/// <param name="RequestedUrl">What was asked for.</param>
/// <param name="FinalUrl">Where the request actually ended up.</param>
/// <param name="RawLinks">
/// Exactly what the DOM produced, including any non-string values. Deliberately
/// untyped and uncounted: link vetting is the single place that decides what is
/// fetchable, so filtering here would move the accounting boundary and
/// reintroduce the silent drop it exists to stop.
/// </param>
/// <param name="Html">The page source, when capture was asked for.</param>
public sealed record TocPage(
    string RequestedUrl,
    string FinalUrl,
    IReadOnlyList<object?> RawLinks,
    string? Html = null);

/// <summary>A loaded chapter page with its two extracted fields.</summary>
public sealed record ChapterPage(
    string RequestedUrl,
    string FinalUrl,
    string Title,
    string Body,
    string? NextUrl = null)
{
    /// <summary>
    /// The whole story box as the page showed it, before anything was
    /// removed, so what was left out can be checked. Null where the source
    /// cannot say.
    /// </summary>
    public string? PageText { get; init; }
}

/// <summary>Loads pages and reads named fields out of them.</summary>
/// <remarks>
/// <para>
/// Shaped by the two page kinds this tool understands, not by any browser API.
/// If it ever starts mirroring a driver's own surface it has stopped buying
/// isolation and should be reconsidered.
/// </para>
/// <para>
/// Every method takes the caller's <see cref="CancellationToken"/> and must
/// honour it rather than imposing a timeout of its own. That is the whole
/// deadline for the operation. Python gives the navigation, each selector wait,
/// and the fetch loop's own wrapper the same configured value independently, so
/// three budgets of 25s nest inside an outer 25s and the outer one always wins
/// — a page that loads in 20s and needs 3s for its selector is reported as a
/// timeout and retried, and the selector wait can never use the budget it was
/// given. One token means one deadline.
/// </para>
/// </remarks>
public interface IPageSource : IAsyncDisposable
{
    /// <summary>Navigate and stop, returning the final URL.</summary>
    /// <remarks>
    /// Exists for the human gate: after launch the user has to see the page in
    /// order to sign in or solve a challenge. Without it the browser sits on a
    /// blank tab while the log claims the page is open.
    /// </remarks>
    Task<string> OpenPageAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Whether this context carries any cookies.</summary>
    /// <remarks>
    /// The post-gate robots downgrade is keyed on this rather than on the
    /// confirm button. Pressing Ready without signing in is not an authenticated
    /// session, and treating it as one would make the override the default path
    /// instead of a deliberate act.
    /// </remarks>
    Task<bool> HasSessionCookiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the browser carries an account cookie for <paramref name="siteUrl"/>'s site.</summary>
    /// <remarks>
    /// For a source that serves several sites at once. A source that only
    /// ever serves one answers for that one.
    /// </remarks>
    Task<bool> HasSessionCookiesAsync(string? siteUrl, CancellationToken cancellationToken = default) =>
        this.HasSessionCookiesAsync(cancellationToken);

    Task<TocPage> LoadTocAsync(
        string url,
        string linkSelector,
        bool captureHtml = false,
        string? screenshotPath = null,
        CancellationToken cancellationToken = default);

    Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="LoadChapterAsync(string, string, string, CancellationToken)"/>, also reading
    /// where the page's "next chapter" link points, in the same visit.
    /// </summary>
    /// <remarks>
    /// For following a book chapter by chapter when no permitted page lists
    /// them all. A source that cannot read links reports no next chapter, which
    /// ends the walk rather than failing it.
    /// </remarks>
    Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        string nextSelector,
        CancellationToken cancellationToken) =>
        this.LoadChapterAsync(url, titleSelector, contentSelector, cancellationToken);
}
