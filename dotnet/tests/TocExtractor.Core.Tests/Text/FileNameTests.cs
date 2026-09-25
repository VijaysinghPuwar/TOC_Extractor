using System.Text;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Tests.Text;

public sealed class FileNameTests
{
    /// <summary>The six cases where the two v1 copies disagreed.</summary>
    private static readonly string[] DriftCases =
    [
        "forbidden_run_two",
        "forbidden_run_mixed",
        "forbidden_run_long",
        "forbidden_only",
        "windows_path",
        "url_as_title",
    ];

    public static TheoryData<string> PinnedCases() =>
        [.. Golden.Cases.SafeFilename.Pinned.Select(entry => entry.Id)];

    public static TheoryData<string> DriftedCases() => [.. DriftCases];

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void Matches_canonical_v1(string caseId)
    {
        var input = Golden.FileNameCase(caseId);

        Assert.Equal(Golden.V1FileName(caseId).Value, FileName.Sanitise(input.Value));
    }

    [Theory]
    [MemberData(nameof(DriftedCases))]
    public void Takes_the_cli_side_of_the_v1_drift(string caseId)
    {
        var gui = Golden.V1.SafeFilename["gui"][caseId].Value;
        var cli = Golden.V1.SafeFilename["cli"][caseId].Value;
        Assert.True(gui != cli, $"{caseId} is no longer a divergence; drop it from this list");

        Assert.Equal(cli, FileName.Sanitise(Golden.FileNameCase(caseId).Value));
    }

    [Fact]
    public void Nfd_and_nfc_titles_collapse_to_one_name()
    {
        var nfd = Golden.FileNameCase("nfd_input");
        var nfc = Golden.FileNameCase("nfc_input");
        Assert.True(
            Golden.V1FileName("nfd_input").Value != Golden.V1FileName("nfc_input").Value,
            "v1 must have kept these distinct, or this test proves nothing");

        var sanitised = FileName.Sanitise(nfd.Value);

        Assert.Equal(FileName.Sanitise(nfc.Value), sanitised);
        Assert.True(sanitised.IsNormalized(NormalizationForm.FormC));
    }

    [Theory]
    [InlineData("leading_dot")]
    [InlineData("leading_dots_multiple")]
    public void Strips_leading_dots_that_v1_kept(string caseId)
    {
        Assert.StartsWith(".", Golden.V1FileName(caseId).Value, StringComparison.Ordinal);

        Assert.DoesNotContain(
            FileName.Sanitise(Golden.FileNameCase(caseId).Value)[..1], ".", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cjk_over_255_bytes")]
    [InlineData("emoji_over_255_bytes")]
    public void Caps_on_bytes_where_v1_capped_on_characters(string caseId)
    {
        Assert.True(
            Golden.V1FileName(caseId).Utf8Bytes > FileName.MaxBytes,
            "v1 must overrun here, or this test proves nothing");

        var result = FileName.Sanitise(Golden.FileNameCase(caseId).Value);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= FileName.MaxBytes);
        // A cut mid-sequence would not survive this round trip intact.
        Assert.Equal(result, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result)));
    }

    [Fact]
    public void Colon_becomes_underscore() =>
        Assert.DoesNotContain(
            ":", FileName.Sanitise(Golden.FileNameCase("colon_macos").Value),
            StringComparison.Ordinal);

    /// <summary>Broader than v1's tab/newline/CR set; a NUL makes the open fail outright.</summary>
    [Fact]
    public void Removes_control_characters() =>
        Assert.Equal("ChapterOneTwo", FileName.Sanitise("Chapter\u0000One\u0007Two"));

    [Theory]
    [InlineData("abc", 10, "abc")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abc", 2, "ab")]
    [InlineData("章章章", 9, "章章章")]
    [InlineData("章章章", 8, "章章")]
    [InlineData("章章章", 3, "章")]
    [InlineData("章章章", 2, "")]
    [InlineData("🙂🙂", 7, "🙂")]
    public void Truncates_on_a_character_boundary(string value, int limit, string expected) =>
        Assert.Equal(expected, FileName.TruncateUtf8(value, limit));

    /// <summary>
    /// Python slices titles by code point. A UTF-16 slice would cut 100 emoji
    /// to 75 while reporting the same character cap.
    /// </summary>
    [Fact]
    public void Character_cap_counts_code_points_not_utf16_units()
    {
        var title = string.Concat(Enumerable.Repeat("🙂", 100));

        var result = FileName.Sanitise(title, maxChars: 150, maxBytes: 1000);

        Assert.Equal(100, result.EnumerateRunes().Count());
    }
}
