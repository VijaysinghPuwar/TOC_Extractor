using System.Text.Json;

namespace TocExtractor.Core.Tests.Text;

/// <summary>The fixtures the Python suite asserts against, loaded for this one.</summary>
/// <remarks>
/// Both files are produced on the Python side and copied in at build time.
/// corpus.json carries the inputs (exported from corpus.py); v1_golden.json
/// carries what v1 produced for them. Reading both here is what makes "the two
/// implementations agree" an assertion rather than a claim.
/// </remarks>
internal static class Golden
{
    /// <summary>
    /// The v1 CLI script is canonical where the two v1 copies drifted: it is the
    /// newer of the pair, and it collapses runs of forbidden characters, which
    /// is safe only because FilenameAllocator resolves the collisions that
    /// collapse can create.
    /// </summary>
    internal const string CanonicalImplementation = "cli";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    internal static Corpus Cases { get; } = Load<Corpus>("corpus.json");

    internal static V1Golden V1 { get; } = Load<V1Golden>("v1_golden.json");

    internal static CorpusCase CleanTextCase(string id) =>
        Cases.CleanText.Find(id) ?? throw new InvalidOperationException($"no clean_text case {id}");

    internal static CorpusCase FileNameCase(string id) =>
        Cases.SafeFilename.Find(id)
        ?? throw new InvalidOperationException($"no safe_filename case {id}");

    internal static GoldenEntry V1FileName(string id) =>
        V1.SafeFilename[CanonicalImplementation][id];

    internal static GoldenEntry V1CleanText(string combo, string id) =>
        V1.CleanText[CanonicalImplementation][combo][id];

    private static T Load<T>(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", name);
        var loaded = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);

        return loaded ?? throw new InvalidOperationException($"{path} deserialised to null");
    }
}

internal sealed record Corpus(
    IReadOnlyList<Combo> CleanTextCombos,
    CaseGroup CleanText,
    CaseGroup SafeFilename);

internal sealed record CaseGroup(
    IReadOnlyList<CorpusCase> Pinned,
    IReadOnlyList<CorpusCase> Changing)
{
    internal CorpusCase? Find(string id) =>
        Pinned.Concat(Changing).FirstOrDefault(entry => entry.Id == id);
}

internal sealed record CorpusCase(string Id, string Value, string Reason, IReadOnlyList<string> Tags);

internal sealed record Combo(string Name, bool RemoveLinks, bool StripAds);

internal sealed record GoldenEntry(string Value, int Utf8Bytes, string Sha256);

internal sealed record V1Golden(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, GoldenEntry>>> CleanText,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, GoldenEntry>> SafeFilename);
