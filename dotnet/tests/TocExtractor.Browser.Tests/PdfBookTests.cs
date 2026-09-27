namespace TocExtractor.Browser.Tests;

/// <summary>The page a PDF book is printed from.</summary>
public sealed class PdfBookTests
{
    [Fact]
    public void Each_chapter_starts_with_its_heading()
    {
        var html = PdfBook.Html("Book", [("Chapter 1: Dawn", "It began."), ("Chapter 2: Noon", "It went on.")]);

        Assert.Contains("<h2>Chapter 1: Dawn</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<p>Chapter 1: Dawn</p><p>to</p><p>Chapter 2: Noon</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Chapters_without_headings_leave_no_chapter_names_for_a_voice_to_read()
    {
        var html = PdfBook.Html("Book", [("", "It began."), ("", "It went on.")]);

        Assert.DoesNotContain("<h2>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>to</p>", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Book</h1>", html, StringComparison.Ordinal);

        // Still a new page for each chapter.
        Assert.Equal(2, html.Split("<section>").Length - 1);
    }
}
