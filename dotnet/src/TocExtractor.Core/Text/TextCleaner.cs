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
    // Whole words only: inside a word it took "ads by example" out of "who
    // leads by example", and "heads by", "roads by" and "threads by" with it.
    [GeneratedRegex(
        @"\bAds by" + Whitespace.Class + @"+\w+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdsBy { get; }

    [GeneratedRegex(
        @"\bSponsored" + Whitespace.Class + @"+Content\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SponsoredContent { get; }

    // The label a site prints alone on a line where an ad was (novelfire ends
    // some chapters with "Advertisements"). Only the word on its own line, so
    // "the news, advertisements, and programs" stays.
    [GeneratedRegex(
        @"(?m)^(?:(?!\n)" + Whitespace.Class + @")*Advertisements?(?:(?!\n)" + Whitespace.Class + @")*(?:\n|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdvertisementLine { get; }

    /// <summary>An HTML entity left in the text as letters: "know&amp;#8230;" as a site double-escaped it.</summary>
    [GeneratedRegex(@"&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|nbsp|amp|quot|lt|gt|apos|hellip|mdash|ndash|lsquo|rsquo|ldquo|rdquo);", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralEntity { get; }

    /// <summary>A formatting tag shown as letters: "&lt;em&gt;Almost there,&lt;/em&gt;". Only bare tags, so "&lt;The Authority...&gt;" stays.</summary>
    [GeneratedRegex(@"</?(?:em|i|b|strong|u|s|br|p)[ \t]*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LiteralTag { get; }

    /// <summary>A line that is only "&amp;nbsp;": a site's empty paragraph, escaped once too often.</summary>
    [GeneratedRegex(@"(?m)^[ \t]*(?:&(?:amp;)*nbsp;[ \t]*)+(?:\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex EntityOnlyLine { get; }

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

        // Chinese, Japanese and Korean cannot pass for Latin letters, so they
        // are never a look-alike tag: "Won't say who. 凸" is the author's.
        if (shapes.Any(IsCjk))
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

    private static bool IsCjk(Rune rune) => rune.Value is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF)
        or (>= 0x4E00 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FFFF);

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

    /// <summary>A reading site's name as it brands itself: NovelFire, PandaNovel, LightNovelPub, novelfire.net.</summary>
    private const string SiteName =
        @"(?-i:(?:[A-Z][a-z]+)*Novel(?:[A-Z][a-z]+)+|(?:[A-Z][a-z]+)+Novel\b|\b[a-z]*novel[a-z]*\.(?:com|net|org|co|me|io)\b)";

    /// <summary>Where a notice sends the reader, or names as the thief.</summary>
    [GeneratedRegex(@"\bamazon\b|\boriginal (?:site|version|source|platform|website|translation)\b|\bauthentic version\b|\bgenuine (?:story|version)\b|\breal experience\b|" + SiteName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoticeWhere { get; }

    /// <summary>A vaguer "where", which counts only beside a call to read or report.</summary>
    [GeneratedRegex(@"\belsewhere\b|\banother (?:site|website)\b|\bother (?:sites|websites)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoticeElsewhere { get; }

    // "Lifted" alone is a story's eyes and hands ("she lifted her eyes"), and
    // "head to" a turned head: with "reading the book" they removed story
    // sentences. A notice's taking is "lifted from", its call "head to" a site.
    [GeneratedRegex(@"\b(?:stolen|steal\w*|pilfer\w*|purloin\w*|unlawful\w*|illicit\w*|misappropriat\w*|unauthori[sz]ed|theft|infringement|violation)\b|\b(?:taken|lifted) (?:without|from)\b|\bwithout (?:the author['’]?s? )?(?:consent|permission|approval|authori[sz]ation)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoticeTheft { get; }

    [GeneratedRegex(@"\b(?:report\w*|visit\w*|support\w*|check it out|(?<!\b(?:his|her|their|its|my|your|our|the|a) )head to|help (?:the author|them|out)|ensure the author|read(?:ing)? (?:it|this|the|their|on|from))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoticeAction { get; }

    [GeneratedRegex(@"\b(?:story|stories|tale|narrative|novel|content|book|chapter|text|work|translation)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoticeSubject { get; }

    /// <summary>A notice asks: "kindly file a report", "please report it", "if found on Amazon".</summary>
    [GeneratedRegex(@"\b(?:please|kindly)\b|\bif (?:you )?(?:found|find|see|seen|spot|spotted|encounter\w*|come across|came across)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoticeRequest { get; }

    /// <summary>A site's tag after the last sentence of a paragraph: " NovelFire", " View the correct content at NovelFire".</summary>
    [GeneratedRegex(@"(?<=[.!?~…""'”’\])])[ \t]+((?:[^.!?\n""“”]{0,60}?[ \t])?)" + SiteName + @"\.?[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SiteTail { get; }

    [GeneratedRegex(@"\b(?:content|read|visit|view|find|chapter|version|latest|available|exclusive|update|on|at)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteTailWords { get; }

    /// <summary>The site's name put in place of the word "novel": "who loves reading NovelFires".</summary>
    [GeneratedRegex(@"\b([A-Za-z][a-z'’]*)([ \t]+)((?:[A-Z][a-z]+)*Novel(?:[A-Z][a-z]+?)+)(s?)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SiteForNovel { get; }

    /// <summary>Words before a site's name that mean the site itself is meant ("on NovelFire"), not a novel.</summary>
    private static readonly HashSet<string> SiteItself = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "at", "from", "to", "via", "by", "visit", "visiting", "and", "or", "with", "for",
    };

    /// <summary>A sentence's first word that makes the name after it a thing, not the site: "The NovelFire I'd been reading".</summary>
    private static readonly HashSet<string> Determiners = new(StringComparer.Ordinal)
    {
        "The", "A", "An", "This", "That", "These", "Those", "My", "His", "Her", "Their", "Your", "Our", "Its", "Every", "Each", "Some",
    };

    /// <summary>Where a sentence ends and the next begins, inside one paragraph.</summary>
    [GeneratedRegex(@"[.!?…~][""'”’)\]]*[ \t]+(?=[A-Z“""])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak { get; }

    /// <summary>
    /// Removes the notices a reading site writes into its chapters: "This tale
    /// has been pilfered from NovelFire. If found on Amazon, kindly file a
    /// report.", "Visit NovelFire for the authentic version.", a bare
    /// "NovelFire" after a paragraph's last sentence, and the site's name put
    /// in place of the word "novel".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The notices are written in endless variations (stolen, pilfered,
    /// purloined, unlawfully lifted, misappropriated...), a new one every few
    /// chapters, so they are recognised by what they are made of, not matched
    /// against a list. A notice is a short sentence with no dialogue in it that
    /// names where to read or who took the text (Amazon, the original site, a
    /// "...Novel..." site name) together with taking or a call to read or
    /// report it; or that says the story was taken and asks for a report.
    /// Story sentences that only mention something stolen, or only a report,
    /// have one of those and not both.
    /// </para>
    /// <para>
    /// A notice is removed whether it is a paragraph of its own or was added to
    /// the end of one. Mirrors text.py.
    /// </para>
    /// </remarks>
    public static string StripSiteNotices(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!(text.Contains("novel", StringComparison.OrdinalIgnoreCase)
            || NoticeWhere.IsMatch(text)
            || NoticeElsewhere.IsMatch(text)
            || NoticeTheft.IsMatch(text)))
        {
            return text;
        }

        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var carriage = raw.EndsWith('\r');
            var line = carriage ? raw[..^1] : raw;

            // Notice sentences after the story go first, so a story sentence
            // sharing their paragraph is kept; then a paragraph that is all
            // notice goes whole.
            var whole = line;
            for (var i = 0; i < 3; i++)
            {
                var cut = CutTrailingNotice(line);
                if (cut == line)
                {
                    break;
                }

                line = cut;
            }

            if (IsSiteNotice(line.Trim())
                || (line != whole && IsSiteNotice(whole.Trim()) && NoticeSignal(line)))
            {
                continue;
            }

            line = SiteTail.Replace(line, match =>
                match.Groups[1].Length == 0 || SiteTailWords.IsMatch(match.Groups[1].Value) ? string.Empty : match.Value);
            line = SiteForNovel.Replace(line, match =>
                (char.IsLower(match.Groups[1].Value[0]) ? !SiteItself.Contains(match.Groups[1].Value) : Determiners.Contains(match.Groups[1].Value))
                    ? match.Groups[1].Value + match.Groups[2].Value + "novel" + match.Groups[4].Value
                    : match.Value);
            kept.Add(carriage ? line + "\r" : line);
        }

        return string.Join('\n', kept);
    }

    /// <summary>Whether one sentence or paragraph is a site's notice. See <see cref="StripSiteNotices"/>.</summary>
    internal static bool IsSiteNotice(string text)
    {
        if (text.Length is < 15 or > 260
            || text.IndexOfAny(['"', '“', '”']) >= 0
            || text[0] is '[' or '【' or '\''
            || text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 45)
        {
            return false;
        }

        var theft = NoticeTheft.IsMatch(text);
        var action = NoticeAction.IsMatch(text);

        // Amazon is a rainforest as well as a shop, and a report is news as
        // well as a request: "a report given by 'One'... an SS+ dungeon in the
        // south of Amazon" was a story paragraph, and it was removed. Named
        // alone, Amazon counts beside taking, or a call that asks.
        var where = NoticeWhere.Matches(text);
        var onlyAmazon = where.All(m => m.Value.Equals("amazon", StringComparison.OrdinalIgnoreCase));
        return (where.Count > 0 && (theft || (action && (!onlyAmazon || NoticeRequest.IsMatch(text)))))
            || (action && NoticeElsewhere.IsMatch(text) && NoticeSubject.IsMatch(text))
            || (theft && action && text.Length <= 160 && NoticeSubject.IsMatch(text));
    }

    /// <summary>Whether a sentence carries any part of a notice: a site, elsewhere, taking, or a call to act.</summary>
    /// <remarks>A short lead-in about the story itself ("Love this story?") belongs to the notice after it.</remarks>
    private static bool NoticeSignal(string text) =>
        NoticeWhere.IsMatch(text) || NoticeElsewhere.IsMatch(text) || NoticeTheft.IsMatch(text) || NoticeAction.IsMatch(text)
        || (text.Trim().Length <= 40 && NoticeSubject.IsMatch(text));

    /// <summary>The paragraph without a notice added after its story: the last sentence or two, when they are one.</summary>
    private static string CutTrailingNotice(string line)
    {
        var breaks = SentenceBreak.Matches(line);
        for (var k = Math.Max(0, breaks.Count - 2); k < breaks.Count; k++)
        {
            var at = breaks[k].Index + breaks[k].Length;
            var head = line[..at].TrimEnd();
            if (head.Length > 0 && IsSiteNotice(line[at..].Trim()))
            {
                return head;
            }
        }

        return line;
    }

    /// <summary>
    /// Entities a site left in its text as letters: "&amp;nbsp;", "know&amp;#8230;",
    /// "it&amp;amp;#8217;s", and formatting tags it shows as letters
    /// ("&lt;em&gt;Almost there,&lt;/em&gt;"). The page shows them as written and a
    /// voice reads them aloud ("ampersand hash eight two one seven"). A fixed
    /// set, so both implementations decode the same; decoded twice for a
    /// double escape. Mirrors text.py.
    /// </summary>
    internal static string DecodeLiteralEntities(string text)
    {
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return text.Contains('<', StringComparison.Ordinal) ? LiteralTag.Replace(text, string.Empty) : text;
        }

        text = EntityOnlyLine.Replace(text, string.Empty);
        for (var pass = 0; pass < 2; pass++)
        {
            var decoded = LiteralEntity.Replace(text, match => Entity(match.Value));
            if (decoded == text)
            {
                break;
            }

            text = decoded;
        }

        return LiteralTag.Replace(text, string.Empty);
    }

    private static string Entity(string entity) => entity switch
    {
        "&nbsp;" => " ",
        "&amp;" => "&",
        "&quot;" => "\"",
        "&lt;" => "<",
        "&gt;" => ">",
        "&apos;" => "'",
        "&hellip;" => "\u2026",
        "&mdash;" => "\u2014",
        "&ndash;" => "\u2013",
        "&lsquo;" => "\u2018",
        "&rsquo;" => "\u2019",
        "&ldquo;" => "\u201c",
        "&rdquo;" => "\u201d",
        _ => Numeric(entity),
    };

    private static string Numeric(string entity)
    {
        var hex = entity[2] is 'x' or 'X';
        var digits = entity[(hex ? 3 : 2)..^1];
        if (!int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || code is 0 or (>= 0xD800 and <= 0xDFFF) or > 0x10FFFF || (code < 0x20 && code is not ('\t' or '\n')))
        {
            return entity;
        }

        return code == 0xA0 ? " " : char.ConvertFromUtf32(code);
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
            text = DecodeLiteralEntities(text);
            text = AdsBy.Replace(text, string.Empty);
            text = SponsoredContent.Replace(text, string.Empty);
            text = AdvertisementLine.Replace(text, string.Empty);
            text = StripSiteNotices(text);
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
