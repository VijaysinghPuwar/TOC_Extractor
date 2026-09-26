using System.Security.Cryptography;
using System.Text;
using TocExtractor.Core.Links;

namespace TocExtractor.Core.Models;

/// <summary>One successfully extracted chapter.</summary>
/// <remarks>
/// Everything an exporter could want is carried here, populated once by the
/// fetch loop. The alternative — sinks reaching back for detail — would mean
/// reopening the fetch loop every time an exporter needed one more field.
/// </remarks>
public sealed record ChapterRecord(
    int Index,
    string RequestedUrl,
    string FinalUrl,
    string Title,
    string Text,
    int StrippedUrls,
    DateTimeOffset FetchedAt,
    int Attempts,
    RobotsDecision? Robots = null,
    string? NextUrl = null)
{
    /// <summary>What the saved text left out of the page, or had twice; null where the page could not be compared.</summary>
    public Text.TextAudit? Audit { get; init; }

    /// <summary>The file the chapter was saved in, once it has been; null before, or where there is none.</summary>
    public string? SavedAs { get; init; }

    public int ByteCount => Encoding.UTF8.GetByteCount(this.Text);

    public string Sha256 =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(this.Text)));

    public bool Redirected =>
        !string.Equals(this.RequestedUrl, this.FinalUrl, StringComparison.Ordinal);
}
