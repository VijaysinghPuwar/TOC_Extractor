using System.Text.Json;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Tests.Text;

/// <summary>
/// Look-alike site watermarks, read from the corpus the Python suite also
/// reads, so both implementations are held to the same answers.
/// </summary>
public sealed class WatermarkTests
{
    private static readonly JsonElement Corpus = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "watermarks.json"))).RootElement;

    public static TheoryData<string, string> Stripped()
    {
        var data = new TheoryData<string, string>();
        foreach (var pair in Corpus.GetProperty("strip").EnumerateArray())
        {
            data.Add(pair[0].GetString()!, pair[1].GetString()!);
        }

        return data;
    }

    public static TheoryData<string> Kept()
    {
        var data = new TheoryData<string>();
        foreach (var raw in Corpus.GetProperty("keep").EnumerateArray())
        {
            data.Add(raw.GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Stripped))]
    public void A_lookalike_watermark_at_the_end_of_a_paragraph_is_removed(string raw, string expected) =>
        Assert.Equal(expected, TextCleaner.Clean(raw).Text);

    [Theory]
    [MemberData(nameof(Kept))]
    public void An_ordinary_ending_is_kept(string raw) =>
        Assert.Equal(raw, TextCleaner.Clean(raw).Text);

    [Fact]
    public void Watermarks_are_kept_when_ad_markers_are()
    {
        var raw = Corpus.GetProperty("strip")[0][0].GetString()!;
        Assert.Equal(raw, TextCleaner.Clean(raw, stripAds: false).Text);
    }
}
