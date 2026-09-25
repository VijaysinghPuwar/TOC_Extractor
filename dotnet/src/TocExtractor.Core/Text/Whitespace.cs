namespace TocExtractor.Core.Text;

/// <summary>Python's definition of whitespace, which .NET's does not match.</summary>
/// <remarks>
/// <para>
/// Python's <c>\s</c> and <c>str.strip()</c> both treat U+001C to U+001F - the
/// file, group, record and unit separators - as whitespace. .NET's <c>\s</c> is
/// <c>[\f\n\r\t\v\x85\p{Z}]</c>, and <c>char.IsWhiteSpace</c> agrees with it,
/// so neither covers those four: they are category Cc, not Z. Every other
/// character the two could disagree about does not exist - U+0085, NBSP,
/// U+2028 and U+3000 all match on both sides, measured rather than assumed.
/// </para>
/// <para>
/// The gap stays invisible until a page carries one of those bytes, at which
/// point the two implementations produce different text for the same input and
/// the golden corpus, which contains none of them, cannot say why. Closing it
/// costs one character class.
/// </para>
/// </remarks>
internal static class Whitespace
{
    private const char FirstSeparator = '\u001C';
    private const char LastSeparator = '\u001F';

    /// <summary>A character class matching exactly what Python's <c>\s</c> does.</summary>
    internal const string Class = @"[\s\u001C-\u001F]";

    /// <summary>The negation of <see cref="Class"/>, for Python's <c>\S</c>.</summary>
    internal const string Negated = @"[^\s\u001C-\u001F]";

    private static bool IsWhitespace(char value) =>
        char.IsWhiteSpace(value) || value is >= FirstSeparator and <= LastSeparator;

    /// <summary>Trim as Python's <c>str.strip()</c> does.</summary>
    internal static string Trim(string value)
    {
        var start = 0;
        var end = value.Length;

        while (start < end && IsWhitespace(value[start]))
        {
            start++;
        }

        while (end > start && IsWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }
}
