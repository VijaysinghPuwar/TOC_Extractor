using System.Text.RegularExpressions;

namespace TocExtractor.Core.Text;

/// <summary>Normalises extracted body text.</summary>
/// <remarks>
/// The substitution order is load-bearing: URLs are removed before whitespace
/// collapses, which is why removing a URL that sat on its own line leaves no
/// blank gap behind it. Reordering these changes the output of every page.
/// </remarks>
public static partial class TextCleaner
{
    [GeneratedRegex(
        @"Ads by" + Whitespace.Class + @"+\w+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdsBy { get; }

    [GeneratedRegex(
        "Sponsored" + Whitespace.Class + "+Content",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SponsoredContent { get; }

    [GeneratedRegex(@"https?://" + Whitespace.Negated + "+")]
    private static partial Regex Url { get; }

    [GeneratedRegex(Whitespace.Class + @"+\n")]
    private static partial Regex WhitespaceBeforeNewline { get; }

    [GeneratedRegex(@"\n" + Whitespace.Class + "+")]
    private static partial Regex WhitespaceAfterNewline { get; }

    /// <summary>Unreachable, and kept deliberately.</summary>
    /// <remarks>
    /// <see cref="WhitespaceBeforeNewline"/> runs first and collapses every run
    /// of newlines to a single one, so by the time this runs there is nothing
    /// three newlines long left to match. Verified by exhaustive search over
    /// every input up to length six drawn from {a, newline, space, tab, CR}:
    /// zero survivors. No v1 output in the golden contains a blank line either.
    /// <para>
    /// It stays because text.py has it and this is a port, not a cleanup. The
    /// observable consequence — paragraph breaks do not survive cleaning — is
    /// pinned by a test rather than left for a reader to infer from a rule that
    /// never fires.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRun { get; }

    /// <param name="text">Raw text as read out of the page.</param>
    /// <param name="removeLinks">
    /// When true (the default), URLs are deleted from the prose and counted.
    /// </param>
    /// <param name="stripAds">When true (the default), common ad markers are deleted.</param>
    public static CleanedText Clean(string text, bool removeLinks = true, bool stripAds = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (stripAds)
        {
            text = AdsBy.Replace(text, string.Empty);
            text = SponsoredContent.Replace(text, string.Empty);
        }

        var strippedUrls = 0;
        if (removeLinks)
        {
            strippedUrls = Url.Count(text);
            text = Url.Replace(text, string.Empty);
        }

        text = WhitespaceBeforeNewline.Replace(text, "\n");
        text = WhitespaceAfterNewline.Replace(text, "\n");
        text = BlankRun.Replace(text, "\n\n");

        return new CleanedText(Whitespace.Trim(text), strippedUrls);
    }
}
