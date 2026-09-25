using TocExtractor.Browser;

namespace TocExtractor.App.Pipeline;

/// <summary>The PDF writer the desktop app uses: Chromium's own print to PDF.</summary>
public sealed class ChromiumPdfWriter : IPdfWriter
{
    public Task WriteAsync(
        string title,
        IReadOnlyList<(string Heading, string Body)> chapters,
        string path,
        CancellationToken cancellationToken) =>
        PdfBook.WriteAsync(title, chapters, path, cancellationToken);
}
