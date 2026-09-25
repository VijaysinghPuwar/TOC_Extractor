using System.Text.Json;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

/// <summary>
/// Every robots decision this implementation makes, against the one the Python
/// implementation makes for the same input.
/// </summary>
/// <remarks>
/// .NET ships no robots parser, so the rule matching, wildcard handling and
/// precedence are written from scratch here and in <c>politeness.py</c>. Both
/// follow RFC 9309, and agreeing across a corpus is the evidence that they do
/// the same thing, so the answers are generated from Python and committed.
/// There is no exception list: every case must agree.
/// </remarks>
public sealed class RobotsConformanceTests
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly Conformance Corpus = Load();

    public static TheoryData<int> CaseIndices() => [.. Enumerable.Range(0, Corpus.Cases.Count)];

    [Theory]
    [MemberData(nameof(CaseIndices))]
    public void Agrees_with_python(int index)
    {
        var entry = Corpus.Cases[index];
        var allowed = Decide(entry);

        Assert.True(
            allowed == entry.Allowed,
            $"{Key(entry)}: python={entry.Allowed}, here={allowed}");
    }

    /// <summary>Agreement would be trivial if nothing were ever refused.</summary>
    [Fact]
    public void Corpus_exercises_both_outcomes()
    {
        Assert.Contains(Corpus.Cases, entry => entry.Allowed);
        Assert.Contains(Corpus.Cases, entry => !entry.Allowed);
    }

    private static bool Decide(ConformanceCase entry) =>
        RobotsPolicy
            .Parse(Corpus.Bodies[entry.Body], "https://e.com", entry.UserAgent)
            .CanFetch("https://e.com" + entry.Path);

    private static string Key(ConformanceCase entry) =>
        $"{entry.Body}|{entry.UserAgent}|{entry.Path}";

    private static Conformance Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "golden", "robots_conformance.json");

        return JsonSerializer.Deserialize<Conformance>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException($"{path} deserialised to null");
    }

    internal sealed record Conformance(
        IReadOnlyDictionary<string, string> Bodies,
        IReadOnlyList<ConformanceCase> Cases);

    internal sealed record ConformanceCase(string Body, string UserAgent, string Path, bool Allowed);
}
