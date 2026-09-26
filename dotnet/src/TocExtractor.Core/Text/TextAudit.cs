using System.Text.RegularExpressions;

namespace TocExtractor.Core.Text;

/// <summary>
/// What a saved chapter left out of its page, and whether any of it was
/// saved twice: the check that nothing was skipped or doubled.
/// </summary>
/// <remarks>
/// <para>
/// Saving a chapter removes everything in the story box that is not story:
/// ads, menus, share and report buttons. That removal is the one step that
/// could drop a paragraph without anyone noticing, so every chapter is
/// compared, line by line, with the whole story box as the page showed it.
/// </para>
/// <para>
/// On real sites, everything legitimately removed is short: counters, "More",
/// a keyboard tip, a one-line advert. Measured across 91 chapters on five
/// sites, the longest was 10 words. A story paragraph is 20 to 60. So a
/// removed line of <see cref="StoryLineWords"/> words or more is treated as
/// possibly story, and the chapter is flagged for the person to look at.
/// </para>
/// </remarks>
public sealed partial record TextAudit(
    int PageWords,
    int SavedWords,
    int LeftOutWords,
    IReadOnlyList<string> LeftOutLong,
    int Doubled)
{
    /// <summary>A removed line this long may be story rather than a site's furniture.</summary>
    public const int StoryLineWords = 20;

    /// <summary>A line this long that appears more often saved than on the page was doubled.</summary>
    private const int DoubledLineChars = 40;

    /// <summary>Most removed long lines kept for the report; one is enough to flag.</summary>
    private const int KeepLong = 5;

    public static TextAudit Clean { get; } = new(0, 0, 0, [], 0);

    /// <summary>Some line long enough to be story was on the page but is not in the saved text.</summary>
    public bool LeftOutStory => this.LeftOutLong.Count > 0;

    /// <summary>Some paragraph appears more often in the saved text than on the page.</summary>
    public bool DoubledText => this.Doubled > 0;

    /// <summary>
    /// Compare the page's whole story box with the text saved from it. Both
    /// are compared as cleaned text, so a web address or ad marker the
    /// cleaner removes on purpose is not counted.
    /// </summary>
    public static TextAudit Compare(string page, string saved)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(saved);

        var pageLines = Lines(page);
        var savedLines = Lines(saved);
        var savedText = string.Join("\n", savedLines);

        var leftOutWords = 0;
        List<string> leftOutLong = [];
        foreach (var line in pageLines)
        {
            if (Kept(line, savedText, savedLines))
            {
                continue;
            }

            var words = Words(line);
            leftOutWords += words;
            if (words >= StoryLineWords && leftOutLong.Count < KeepLong)
            {
                leftOutLong.Add(line);
            }
        }

        var doubled = 0;
        var onPage = Count(pageLines);
        foreach (var (line, times) in Count(savedLines))
        {
            if (line.Length >= DoubledLineChars && times > onPage.GetValueOrDefault(line))
            {
                doubled++;
            }
        }

        return new TextAudit(Words(page), Words(saved), leftOutWords, leftOutLong, doubled);
    }

    /// <summary>
    /// A page line counts as kept when the saved text has it, whole or as
    /// part of a longer line, or when a saved line is part of it (a line the
    /// cleaner trimmed).
    /// </summary>
    private static bool Kept(string line, string savedText, List<string> savedLines)
    {
        if (savedText.Contains(line, StringComparison.Ordinal))
        {
            return true;
        }

        // Only a saved line that is most of this one: a short saved line
        // ("He nodded.") found inside a removed paragraph must not hide it.
        foreach (var saved in savedLines)
        {
            if (saved.Length * 2 >= line.Length && line.Contains(saved, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Lines(string text)
    {
        List<string> lines = [];
        foreach (var raw in ZeroWidth().Replace(text, "").Split('\n'))
        {
            var line = Spaces().Replace(raw, " ").Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static Dictionary<string, int> Count(List<string> lines)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            counts[line] = counts.GetValueOrDefault(line) + 1;
        }

        return counts;
    }

    internal static int Words(string text)
    {
        var words = 0;
        var inWord = false;
        foreach (var c in text)
        {
            var space = char.IsWhiteSpace(c);
            if (!space && !inWord)
            {
                words++;
            }

            inWord = !space;
        }

        return words;
    }

    [GeneratedRegex("[​-‍⁠﻿]")]
    private static partial Regex ZeroWidth();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
