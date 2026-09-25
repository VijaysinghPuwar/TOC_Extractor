using System.Text;
using System.Text.RegularExpressions;

namespace TocExtractor.Core.Text;

/// <summary>Turns a chapter title into one safe path component.</summary>
/// <remarks>
/// Not collision-free on its own. Runs of forbidden characters collapse to a
/// single underscore, so "A//B" and "A/B" both become "A_B";
/// <see cref="FilenameAllocator"/> is what stops the second from overwriting
/// the first, and removing it turns that collapse into silent data loss.
/// </remarks>
public static partial class FileName
{
    /// <summary>Character cap on a name, before the byte cap applies.</summary>
    public const int MaxChars = 150;

    /// <summary>
    /// Byte cap on one path component. APFS and HFS+ limit a component to 255
    /// <em>bytes</em>, so a 150-character CJK title is 450 bytes and rejected
    /// by a filesystem that a character count says is well within the limit.
    /// </summary>
    public const int MaxBytes = 255;

    private const string Fallback = "untitled";

    [GeneratedRegex(@"[\t\n\r]")]
    private static partial Regex LineBreaks { get; }

    /// <summary>C0 controls and DEL, less the three handled as line breaks.</summary>
    /// <remarks>A title carrying a NUL produces a name the filesystem rejects outright.</remarks>
    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex Controls { get; }

    [GeneratedRegex(@"[\\/:*?""<>|]+")]
    private static partial Regex Forbidden { get; }

    [GeneratedRegex(Whitespace.Class + "+")]
    private static partial Regex WhitespaceRun { get; }

    [GeneratedRegex(@"^\.+")]
    private static partial Regex LeadingDots { get; }

    /// <summary>Trim to at most <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    /// <remarks>
    /// Never splits a character: a name cut mid-sequence is not valid UTF-8 and
    /// round-trips differently through the filesystem than it went in.
    /// </remarks>
    public static string TruncateUtf8(string value, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        // Walk back to the start of the sequence straddling the cut, then keep
        // it only if all of it fits.
        var end = maxBytes;
        var start = end - 1;
        while (start > 0 && (bytes[start] & 0b1100_0000) == 0b1000_0000)
        {
            start--;
        }

        if (start + SequenceLength(bytes[start]) > end)
        {
            end = start;
        }

        return Encoding.UTF8.GetString(bytes, 0, end);
    }

    /// <param name="name">The chapter title, as read from the page.</param>
    /// <param name="maxChars">Cap in Unicode code points.</param>
    /// <param name="maxBytes">Cap in UTF-8 bytes, applied after the character cap.</param>
    public static string Sanitise(string name, int maxChars = MaxChars, int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(name);

        // NFC first. macOS returns NFD from the filesystem while the DOM supplies
        // NFC, so without this the same title compares unequal depending on
        // whether it came from a page or from a directory listing — which breaks
        // deduplication and resume together.
        name = name.Normalize(NormalizationForm.FormC);
        name = Whitespace.Trim(LineBreaks.Replace(name, " "));
        name = Controls.Replace(name, string.Empty);
        name = Forbidden.Replace(name, "_");
        name = WhitespaceRun.Replace(name, " ");
        // A leading dot hides the file in Finder and in ls, so a chapter titled
        // ".Prologue" silently vanishes from the output directory.
        name = Whitespace.Trim(LeadingDots.Replace(name, string.Empty));

        if (name.Length == 0)
        {
            name = Fallback;
        }

        var truncated = TruncateUtf8(TakeRunes(name, maxChars), maxBytes);
        return truncated.Length == 0 ? Fallback : truncated;
    }

    /// <summary>Take the first <paramref name="count"/> code points.</summary>
    /// <remarks>
    /// Not <c>value[..count]</c>: that counts UTF-16 units, so a title of 100
    /// emoji would be cut to 75 where Python's character slice keeps all 100.
    /// </remarks>
    private static string TakeRunes(string value, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var index = 0;
        var taken = 0;
        while (index < value.Length && taken < count)
        {
            index += char.IsSurrogatePair(value, index) ? 2 : 1;
            taken++;
        }

        return index >= value.Length ? value : value[..index];
    }

    private static int SequenceLength(byte lead) => lead switch
    {
        < 0xC0 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };
}
