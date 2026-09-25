namespace TocExtractor.Core.Models;

/// <summary>One file an exporter wrote, and its hash when it was written.</summary>
public sealed record ChapterOutput(string Name, string Sha256 = "");

/// <summary>A chapter an earlier run completed, as recalled from the checkpoint.</summary>
/// <remarks>
/// <para>
/// Carries what every exporter needs to render a resumed chapter. Without it a
/// resumed run could rebuild the merged text but not the manifest, and a
/// manifest that silently omits half the book is the same partial-output
/// problem the merged file just had.
/// </para>
/// <para>
/// <see cref="Outputs"/> is per format. Python records one filename — the text
/// exporter's — and the markdown exporter guesses its own by swapping the
/// extension, which produces nothing at all when text was not among the
/// requested formats. Each exporter reports what it actually wrote instead.
/// </para>
/// </remarks>
public sealed record PriorChapter(
    int Index,
    string Url,
    string Title,
    int Bytes,
    string Sha256,
    int StrippedUrls,
    string FetchedAt,
    IReadOnlyDictionary<string, ChapterOutput> Outputs)
{
    public ChapterOutput? Output(string format) =>
        this.Outputs.TryGetValue(format, out var found) ? found : null;
}
