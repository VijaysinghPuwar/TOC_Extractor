using System.Globalization;
using System.Text;

namespace TocExtractor.Core.Text;

/// <summary>Hands out unique path components for one output directory.</summary>
/// <remarks>
/// Uniqueness is case-insensitive because APFS is case-insensitive by default:
/// "Chapter One.txt" and "chapter one.txt" are one file there, and writing both
/// means the second silently replaces the first.
/// </remarks>
/// <param name="suffix">Extension to append, including the dot.</param>
/// <param name="maxBytes">Byte cap for the whole component, prefix and suffix included.</param>
public sealed class FilenameAllocator(string suffix = ".txt", int maxBytes = FileName.MaxBytes)
{
    private readonly Dictionary<string, string> taken = new(StringComparer.Ordinal);

    /// <summary>Allocate <c>NNN - Title.txt</c>, deduplicating against earlier calls.</summary>
    public AllocatedName Allocate(int index, string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var prefix = string.Create(
            CultureInfo.InvariantCulture, $"{index:D3} - ");
        var reserved = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);
        var stem = FileName.Sanitise(title, maxBytes: Math.Max(1, maxBytes - reserved));

        var candidate = prefix + stem + suffix;
        if (taken.TryAdd(Key(candidate), candidate))
        {
            return new AllocatedName(candidate);
        }

        var original = taken[Key(candidate)];
        for (var counter = 2; ; counter++)
        {
            var marker = string.Create(CultureInfo.InvariantCulture, $" ({counter})");
            // Re-trim the stem so the marker cannot push a component that was
            // already at the byte limit over it.
            var trimmed = FileName.TruncateUtf8(
                stem,
                Math.Max(1, maxBytes - reserved - Encoding.UTF8.GetByteCount(marker)));

            candidate = prefix + trimmed + marker + suffix;
            if (taken.TryAdd(Key(candidate), candidate))
            {
                return new AllocatedName(candidate, original);
            }
        }
    }

    /// <summary>
    /// NFC and lowercase together, which is the closest .NET gets to how a
    /// case-insensitive filesystem compares two names.
    /// </summary>
    private static string Key(string value) =>
        value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
}
