using System.Text.Json;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Tests.Text;

/// <summary>
/// Notices reading sites write into chapters, read from the corpus the
/// Python suite also reads, so both implementations are held to the same answers.
/// </summary>
public sealed class SiteNoticeTests
{
    private static readonly JsonElement Corpus = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "site_notices.json"))).RootElement;

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
    public void A_site_notice_is_removed(string raw, string expected) =>
        Assert.Equal(expected, TextCleaner.Clean(raw).Text);

    [Theory]
    [MemberData(nameof(Kept))]
    public void A_story_line_that_mentions_theft_or_a_site_is_kept(string raw) =>
        Assert.Equal(raw, TextCleaner.Clean(raw).Text);

    [Fact]
    public void Notices_are_kept_when_ad_markers_are()
    {
        var raw = Corpus.GetProperty("strip")[0][0].GetString()!;
        Assert.Equal(raw, TextCleaner.Clean(raw, stripAds: false).Text);
    }

    [Fact]
    public void Windows_line_endings_survive_the_notice_pass()
    {
        var raw = "One.\r\nThis tale has been pilfered from NovelFire. If found on Amazon, kindly file a report.\r\nTwo.";
        Assert.Equal("One.\r\nTwo.", TextCleaner.StripSiteNotices(raw));
    }
}
