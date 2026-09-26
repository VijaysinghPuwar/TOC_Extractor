using System.Text.RegularExpressions;

namespace TocExtractor.App.Pipeline;

/// <summary>
/// Chapter text made ready for a text-to-speech voice: the symbols a voice
/// reads out loud ("equals equals equals", "hash") are taken out, and the
/// words are left exactly as they were.
/// </summary>
/// <remarks>
/// Only symbols are touched. A hyphen inside a word ("well-known") stays,
/// as do ordinary punctuation, quotes, brackets and "+5 strength". What goes:
/// lines drawn with symbols ("=====", "* * *", "-----"), runs of two or more
/// symbols anywhere ("--", "==", "##"), stray # _ = * ~ ^ ` | &lt; &gt;, a dash
/// standing alone between words, and a list bullet at the start of a line.
/// </remarks>
public static partial class SpeechText
{
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Trim().Length > 0 && SymbolLine().IsMatch(raw))
            {
                continue;
            }

            var line = SymbolRun().Replace(raw, " ");
            line = Bullet().Replace(line, "");
            line = LoneDash().Replace(line, " ");
            line = StraySymbol().Replace(line, " ");
            line = Spaces().Replace(line, " ").Trim();
            lines.Add(line);
        }

        // A removed divider can leave blank lines stacked up.
        return Blank().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    /// <summary>A line with no words on it, only drawing symbols.</summary>
    [GeneratedRegex(@"^[\s\-=_#*~+<>|^`•·—–.]*$")]
    private static partial Regex SymbolLine();

    [GeneratedRegex(@"[\-=_#*~<>|^`]{2,}")]
    private static partial Regex SymbolRun();

    [GeneratedRegex(@"^\s*[\-*•·]\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"(?<=\s)-(?=\s)")]
    private static partial Regex LoneDash();

    [GeneratedRegex(@"[#_=*~^`|<>]")]
    private static partial Regex StraySymbol();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Blank();
}
