using System.Globalization;
using System.Text;
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

    /// <summary>A last "word" after sentence-ending punctuation at the end of a line.</summary>
    [GeneratedRegex(@"(?<=[.!?""'\u201d\u2019\]\u3011)\u2026*~\u300d\u300f])[ \t]+(\S{1,24})[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex WatermarkTail { get; }

    /// <summary>The plain-letter form of the same tag, cut to one letter.</summary>
    private static readonly HashSet<string> WatermarkWords = new(StringComparer.Ordinal) { "R" };

    /// <summary>Where a character comes from, coarsely. Kept identical to text.py.</summary>
    private static readonly (int First, int Last, string Name)[] Families =
    [
        (0x0000, 0x02FF, "latin"), (0x1D00, 0x1DBF, "latin"), (0x1E00, 0x1EFF, "latin"),
        (0x2C60, 0x2C7F, "latin"), (0xA720, 0xA7FF, "latin"), (0xAB30, 0xAB6F, "latin"),
        (0x0370, 0x03FF, "greek"), (0x1F00, 0x1FFF, "greek"),
        (0x0400, 0x052F, "cyrillic"), (0x2DE0, 0x2DFF, "cyrillic"), (0xA640, 0xA69F, "cyrillic"),
        (0x20A0, 0x20CF, "currency"), (0x2100, 0x214F, "letterlike"), (0xFF00, 0xFFEF, "fullwidth"),
        (0x1D400, 0x1D7FF, "math"),
    ];

    /// <summary>
    /// Removes broken characters (U+FFFD) and site-name watermarks left at the
    /// end of paragraphs.
    /// </summary>
    /// <remarks>
    /// Some sites tag copied chapters with the source site's name spelled in
    /// look-alike characters from other alphabets (a bold R, a Greek A, a
    /// double-struck N and so on), often cut to a letter or two, after the
    /// last sentence of a paragraph. Spellings vary endlessly, so the tag is
    /// recognised by its shape, not its letters: a final "word" after
    /// sentence-ending punctuation, in a line of plain Latin prose, that is one
    /// or two unusual letters, or mixes alphabets or symbol sets. Real words,
    /// accented ones included, keep to one alphabet. Mirrors text.py.
    /// </remarks>
    public static string StripWatermarks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Replace("\uFFFD", string.Empty, StringComparison.Ordinal);
        return WatermarkTail.Replace(text, match =>
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
            var before = text[lineStart..match.Index];
            return IsWatermark(match.Groups[1].Value, before) ? string.Empty : match.Value;
        });
    }

    private static bool IsWatermark(string token, string before)
    {
        if (WatermarkWords.Contains(token))
        {
            return true;
        }

        if (token.All(char.IsAscii))
        {
            return false;
        }

        var letters = before.EnumerateRunes().Where(Rune.IsLetter).ToList();
        if (letters.Count == 0 || letters.Count(r => r.IsAscii) < 0.9 * letters.Count)
        {
            return false;
        }

        var shapes = Shapes(token);
        if (shapes.Any(r => !(Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) is UnicodeCategory.CurrencySymbol or UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol)))
        {
            return false;
        }

        // At least one letter; the letterlike block (U+2100-214F) holds look-alikes such as a prescription R.
        if (!shapes.Any(r => Rune.IsLetter(r) || r.Value is >= 0x2100 and <= 0x214F))
        {
            return false;
        }

        if (shapes.Count <= 2)
        {
            // Not a short word in ordinary accented letters ("J\u00f3", "L\u00fc").
            return token.EnumerateRunes().Any(r => !(r.IsAscii || r.Value is >= 0x00C0 and <= 0x00FF));
        }

        return shapes.Count <= 9 && shapes.Select(Family).Distinct(StringComparer.Ordinal).Count() >= 2;
    }

    /// <summary>The first character of each letter-with-its-marks in the token.</summary>
    private static List<Rune> Shapes(string token)
    {
        List<Rune> shapes = [];
        foreach (var rune in token.EnumerateRunes())
        {
            var mark = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            if (!(mark && shapes.Count > 0))
            {
                shapes.Add(rune);
            }
        }

        return shapes;
    }

    private static string Family(Rune rune)
    {
        foreach (var (first, last, name) in Families)
        {
            if (rune.Value >= first && rune.Value <= last)
            {
                return name;
            }
        }

        return "block-" + (rune.Value >> 7).ToString(CultureInfo.InvariantCulture);
    }

    /// <param name="text">Raw text as read out of the page.</param>
    /// <param name="removeLinks">
    /// When true (the default), URLs are deleted from the prose and counted.
    /// </param>
    /// <param name="stripAds">When true (the default), common ad markers and look-alike site watermarks are deleted.</param>
    public static CleanedText Clean(string text, bool removeLinks = true, bool stripAds = true)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (stripAds)
        {
            text = AdsBy.Replace(text, string.Empty);
            text = SponsoredContent.Replace(text, string.Empty);
            text = StripWatermarks(text);
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
