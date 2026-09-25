using System.Text.Json;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

/// <summary>
/// Every robots decision this implementation makes, against the one Python's
/// <c>urllib.robotparser</c> makes for the same input.
/// </summary>
/// <remarks>
/// This layer is a reimplementation, not a port: .NET ships no robots parser,
/// so the rule matching, wildcard handling and precedence are written from
/// scratch. Agreeing with the reference across a corpus is the only honest
/// evidence that it behaves the same, so the corpus and the answers it produced
/// are generated from Python and committed.
/// </remarks>
public sealed class RobotsConformanceTests
{
    private static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly Conformance Corpus = Load();

    /// <summary>
    /// The cases where this implementation deliberately differs, and only
    /// those.
    /// </summary>
    /// <remarks>
    /// All three are one decision: robotparser puts an agent in a group when
    /// the group's name appears anywhere inside it, so "MyTOCExtractorBot"
    /// joins the "TOCExtractor" group. RFC 9309 matches the product token, so
    /// it does not. Python's own two code paths disagree on exactly these
    /// inputs — <c>can_fetch</c> matches by substring and <c>matched_rule</c>
    /// by prefix — which is why one algorithm answers both questions here.
    /// </remarks>
    private static readonly HashSet<string> Divergences =
    [
        "case_agent|MyTOCExtractorBot|/caps/q",
        "two_groups|MyTOCExtractorBot|/members/secret",
        "two_groups|MyTOCExtractorBot|/private/x",
    ];

    public static TheoryData<int> CaseIndices() => [.. Enumerable.Range(0, Corpus.Cases.Count)];

    [Theory]
    [MemberData(nameof(CaseIndices))]
    public void Agrees_with_python_except_where_declared(int index)
    {
        var entry = Corpus.Cases[index];
        var allowed = Decide(entry);
        var declared = Divergences.Contains(Key(entry));

        if (declared)
        {
            Assert.True(
                allowed != entry.Allowed,
                $"{Key(entry)} is listed as a divergence but now agrees with Python "
                + "(python=" + entry.Allowed + "); remove it from the list");
            return;
        }

        Assert.True(
            allowed == entry.Allowed,
            $"{Key(entry)}: python={entry.Allowed}, here={allowed}");
    }

    /// <summary>Every declared divergence must be a case the corpus actually contains.</summary>
    [Fact]
    public void Declared_divergences_all_exist_in_the_corpus()
    {
        var present = Corpus.Cases.Select(Key).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(Divergences.Except(present, StringComparer.Ordinal));
    }

    /// <summary>Agreement would be trivial if nothing were ever refused.</summary>
    [Fact]
    public void Corpus_exercises_both_outcomes()
    {
        Assert.Contains(Corpus.Cases, entry => entry.Allowed);
        Assert.Contains(Corpus.Cases, entry => !entry.Allowed);
    }

    /// <summary>
    /// The divergences are confined to one agent. If a future change spread
    /// them to another, that is a bug rather than the documented decision.
    /// </summary>
    [Fact]
    public void Divergences_are_confined_to_one_user_agent()
    {
        var agents = Divergences.Select(key => key.Split('|')[1]).Distinct(StringComparer.Ordinal);

        Assert.Equal(["MyTOCExtractorBot"], agents);
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
