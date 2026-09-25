using TocExtractor.Core.Models;

namespace TocExtractor.Core.Sinks;

/// <summary>Receives chapters as they complete, then a final accounting.</summary>
/// <remarks>
/// The seam between the fetch loop and where chapters end up. The loop never
/// touches the filesystem itself.
/// </remarks>
public interface ISink
{
    Task OpenAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default);

    Task CloseAsync(RunResult result, CancellationToken cancellationToken = default);

    /// <summary>The files this sink wrote for one chapter, keyed by format.</summary>
    /// <remarks>
    /// The checkpoint has to record the name an exporter actually wrote. Building
    /// it by formatting the title produces "007 - A/B.txt" for a file really
    /// called "007 - A_B (2).txt" — unsanitised and unaware of collision dedup —
    /// so the next resume hard-fails looking for a file that never existed.
    /// Python reaches into the sink to find the text exporter and read its map,
    /// which records nothing at all when text is not among the chosen formats.
    /// Asking every sink what it wrote covers all of them.
    /// </remarks>
    IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index);
}
