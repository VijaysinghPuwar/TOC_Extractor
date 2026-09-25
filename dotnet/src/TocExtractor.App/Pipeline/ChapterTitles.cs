using System.Globalization;
using System.Text.RegularExpressions;
using TocExtractor.Core.Pages;

namespace TocExtractor.App.Pipeline;

/// <summary>Tidies chapter headings that carry nothing but a number.</summary>
/// <remarks>
/// Some sites head every chapter "Page 12": machine translation of the
/// original's chapter word. Their own lists call them chapters, and "Page 12"
/// in a file name or a book's contents reads as a page, not a chapter. A
/// heading that is only "Page N" becomes "Chapter N"; anything with words in
/// it is left exactly as the site wrote it.
/// </remarks>
public static partial class ChapterTitles
{
    public static string Tidy(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var match = BarePage().Match(title);
        return match.Success
            ? string.Create(CultureInfo.InvariantCulture, $"Chapter {int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)}")
            : title;
    }

    [GeneratedRegex(@"^\s*page\s*(\d{1,6})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BarePage();
}

/// <summary>A page source whose chapter titles are tidied on the way out.</summary>
internal sealed class TidyTitles(IPageSource inner) : IPageSource
{
    public Task<string> OpenPageAsync(string url, CancellationToken cancellationToken = default) =>
        inner.OpenPageAsync(url, cancellationToken);

    public Task<bool> HasSessionCookiesAsync(CancellationToken cancellationToken = default) =>
        inner.HasSessionCookiesAsync(cancellationToken);

    public Task<TocPage> LoadTocAsync(
        string url,
        string linkSelector,
        bool captureHtml = false,
        string? screenshotPath = null,
        CancellationToken cancellationToken = default) =>
        inner.LoadTocAsync(url, linkSelector, captureHtml, screenshotPath, cancellationToken);

    public async Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        CancellationToken cancellationToken = default) =>
        Tidy(await inner.LoadChapterAsync(url, titleSelector, contentSelector, cancellationToken).ConfigureAwait(false));

    public async Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        string nextSelector,
        CancellationToken cancellationToken) =>
        Tidy(await inner.LoadChapterAsync(url, titleSelector, contentSelector, nextSelector, cancellationToken).ConfigureAwait(false));

    // Not disposed here: the session owns the browser and closes it.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ChapterPage Tidy(ChapterPage page) => page with { Title = ChapterTitles.Tidy(page.Title) };
}
