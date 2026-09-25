using System.Globalization;
using System.Text.RegularExpressions;
using TocExtractor.Core.Pages;

namespace TocExtractor.App.Pipeline;

/// <summary>Tidies "Page N" headings that are really chapters.</summary>
/// <remarks>
/// Some sites head chapters "Page 12". That can mean two things. On one kind
/// of site it is a machine-translated chapter word, and the text opens with
/// "Chapter 12". On another the book is cut into pages that do not line up
/// with its chapters: page 130 ends chapter 140 and starts chapter 141, and
/// page 128 is the middle of chapter 139. Calling either of those "Chapter"
/// would be wrong. So "Page N" becomes "Chapter N" only when the text itself
/// opens with "Chapter N"; otherwise the site's own heading is kept.
/// </remarks>
public static partial class ChapterTitles
{
    public static string Tidy(string title, string? body = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var page = BarePage().Match(title);
        if (!page.Success || body is null)
        {
            return title;
        }

        var number = int.Parse(page.Groups[1].Value, CultureInfo.InvariantCulture);
        var opening = OpeningChapter().Match(body);
        return opening.Success && int.Parse(opening.Groups[1].Value, CultureInfo.InvariantCulture) == number
            ? string.Create(CultureInfo.InvariantCulture, $"Chapter {number}")
            : title;
    }

    [GeneratedRegex(@"^\s*page\s*(\d{1,6})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BarePage();

    [GeneratedRegex(@"\A\s*chapter[ \t]*(\d{1,6})\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningChapter();
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

    private static ChapterPage Tidy(ChapterPage page) => page with { Title = ChapterTitles.Tidy(page.Title, page.Body) };
}
