using System.Globalization;
using System.Text.RegularExpressions;
using TocExtractor.App.Session;

namespace TocExtractor.App.Scanning;

/// <summary>One chapter the scan found an address for.</summary>
public sealed record ScannedChapter(int Number, string Title, string Url);

/// <summary>Where the parts of a chapter page are, as CSS selectors found by the scan.</summary>
public sealed record ChapterLayout(
    string TitleSelector,
    string ContentSelector,
    string? NextSelector,
    string? PrevSelector);

/// <summary>What a scan of a novel's page found, and whether a download can go ahead.</summary>
public sealed record ScanResult
{
    public required string NovelUrl { get; init; }

    public string BookTitle { get; init; } = "";

    /// <summary>Every chapter with a known address, in chapter order.</summary>
    public IReadOnlyList<ScannedChapter> Chapters { get; init; } = [];

    public ChapterLayout? Layout { get; init; }

    /// <summary>Things worth knowing, in plain words: pages robots.txt closed, lists followed.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public Obstacle Obstacle { get; init; }

    /// <summary>Why a download cannot go ahead, or null when it can.</summary>
    public string? Problem { get; init; }

    /// <summary>How many pages the scan read.</summary>
    public int PagesRead { get; init; }

    /// <summary>
    /// Chapter numbers are places in the whole book, not the numbers in the
    /// titles: the book restarts its numbering in each arc or part ("Arc 9:
    /// Chapter 38"). Chapters reached by following links are then numbered by
    /// counting, and addresses are never built from a number.
    /// </summary>
    public bool PositionalNumbers { get; init; }

    public bool Ready => this.Problem is null && this.Chapters.Count > 0 && this.Layout is not null;

    public int FirstNumber => this.Chapters.Count > 0 ? this.Chapters[0].Number : 0;

    public int LastNumber => this.Chapters.Count > 0 ? this.Chapters[^1].Number : 0;

    /// <summary>
    /// How many chapters the book appears to have: the span of numbers seen,
    /// which counts chapters the site lists nowhere but that lie between
    /// ones it does.
    /// </summary>
    public int ChapterSpan => this.Chapters.Count == 0 ? 0 : this.LastNumber - this.FirstNumber + 1;
}

/// <summary>Reads a chapter number out of a title such as "Chapter 350: The Tide".</summary>
public static partial class ChapterNumbers
{
    public static int? FromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = Named().Match(ZeroWidth().Replace(title, ""));
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    [GeneratedRegex(@"(?:chapter|chap|ch\.?|episode|ep\.?|page|part|第)\s*[#:.-]?\s*(\d{1,6})", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    [GeneratedRegex("[\u200B-\u200D\u2060\uFEFF]")]
    private static partial Regex ZeroWidth();
}
