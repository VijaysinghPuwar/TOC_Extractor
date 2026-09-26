using System.Net;
using System.Text;
using Microsoft.Playwright;

namespace TocExtractor.Browser;

/// <summary>Prints a range of chapters as one PDF book, with the browser the app already has.</summary>
/// <remarks>
/// <para>
/// Chromium lays the book out: real line breaking, hyphenation, and every
/// script a system font covers, which a PDF library would need fonts
/// embedded for. The page is built locally and printed offline. Nothing is
/// fetched, so no guard or politeness rule is involved.
/// </para>
/// <para>
/// Plain black on white. A title page, then each chapter on a new page with
/// its heading, and page numbers in the footer.
/// </para>
/// <para>
/// Each PDF starts a browser of its own for as long as it is being printed,
/// which is a few hundred MB. When many books finish together, only as many
/// print at once as the machine has room for (one on a small Mac); the rest
/// wait their turn.
/// </para>
/// </remarks>
public sealed class PdfBook
{
    /// <summary>The longest a book may take to lay out. A book of thousands of chapters can take minutes.</summary>
    private static readonly TimeSpan LayoutBudget = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim Printing = new(AtOnce(MachineBudget.MostTabs));

    /// <summary>How many PDFs print at once: one per 16 tabs the machine can carry, at least one.</summary>
    internal static int AtOnce(int mostTabs) => Math.Max(1, mostTabs / 16);

    public static async Task WriteAsync(
        string title,
        IReadOnlyList<(string Heading, string Body)> chapters,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        await Printing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrintAsync(title, chapters, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Printing.Release();
        }
    }

    private static async Task PrintAsync(
        string title,
        IReadOnlyList<(string Heading, string Body)> chapters,
        string path,
        CancellationToken cancellationToken)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);

        // The full Chromium in its new headless mode: the app downloads that
        // build only, not the separate headless shell.
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true, Channel = "chromium" }).ConfigureAwait(false);
        var page = await browser.NewPageAsync().ConfigureAwait(false);

        // Offline: a chapter's text is data, and nothing in it may reach out.
        await page.RouteAsync("**/*", route => route.AbortAsync()).ConfigureAwait(false);
        // Playwright's default of 30 seconds is too short to lay out a very
        // long book, which then failed on every try.
        await page.SetContentAsync(
            Html(title, chapters),
            new PageSetContentOptions { Timeout = (float)LayoutBudget.TotalMilliseconds }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        await page.PdfAsync(new PagePdfOptions
        {
            Path = path,
            Format = "A5",
            PrintBackground = false,
            DisplayHeaderFooter = true,
            HeaderTemplate = "<span></span>",
            FooterTemplate =
                "<div style=\"font-size:8px;width:100%;text-align:center;color:#000\"><span class=\"pageNumber\"></span></div>",
            Margin = new Margin { Top = "18mm", Bottom = "18mm", Left = "16mm", Right = "16mm" },
        }).ConfigureAwait(false);
    }

    internal static string Html(string title, IReadOnlyList<(string Heading, string Body)> chapters)
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html><head><meta charset="utf-8"><style>
            body { font-family: Georgia, "Iowan Old Style", "Noto Serif", "Songti SC", "SimSun", serif;
                   font-size: 11pt; line-height: 1.55; color: #000; background: #fff; margin: 0; }
            .cover { height: 100vh; display: flex; flex-direction: column; justify-content: center;
                     text-align: center; page-break-after: always; }
            .cover h1 { font-size: 22pt; line-height: 1.25; margin: 0 0 8mm; }
            .cover p { font-size: 10pt; margin: 0; text-align: center; }
            section { page-break-before: always; }
            h2 { font-size: 14pt; line-height: 1.3; margin: 0 0 6mm; }
            p { margin: 0 0 3.2mm; text-align: justify; hyphens: auto; orphans: 2; widows: 2; }
            </style></head><body>
            """);
        var first = chapters.Count > 0 ? chapters[0].Heading : "";
        var last = chapters.Count > 0 ? chapters[^1].Heading : "";
        html.Append("<div class=\"cover\"><h1>").Append(Encode(title)).Append("</h1><p>")
            .Append(Encode(first)).Append("</p><p>to</p><p>").Append(Encode(last)).Append("</p></div>");

        foreach (var (heading, body) in chapters)
        {
            html.Append("<section><h2>").Append(Encode(heading)).Append("</h2>");
            foreach (var paragraph in body.Split('\n'))
            {
                var text = paragraph.Trim();
                if (text.Length > 0)
                {
                    html.Append("<p>").Append(Encode(text)).Append("</p>");
                }
            }

            html.Append("</section>");
        }

        return html.Append("</body></html>").ToString();
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
