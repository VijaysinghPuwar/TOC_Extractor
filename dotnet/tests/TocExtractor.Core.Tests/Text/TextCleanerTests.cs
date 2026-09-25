using TocExtractor.Core.Text;

namespace TocExtractor.Core.Tests.Text;

public sealed class TextCleanerTests
{
    public static TheoryData<string, string> PinnedCasesAndCombos()
    {
        var data = new TheoryData<string, string>();
        foreach (var caseEntry in Golden.Cases.CleanText.Pinned)
        {
            foreach (var combo in Golden.Cases.CleanTextCombos)
            {
                data.Add(caseEntry.Id, combo.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PinnedCasesAndCombos))]
    public void Matches_v1_byte_for_byte(string caseId, string comboName)
    {
        var input = Golden.CleanTextCase(caseId);
        var combo = Golden.Cases.CleanTextCombos.Single(entry => entry.Name == comboName);

        var result = TextCleaner.Clean(input.Value, combo.RemoveLinks, combo.StripAds);

        Assert.Equal(Golden.V1CleanText(comboName, caseId).Value, result.Text);
    }

    /// <summary>
    /// The two v1 copies never disagreed on clean_text. If that stops being
    /// true the fixture has been recaptured wrongly, and every assertion above
    /// is measuring the wrong side of a drift.
    /// </summary>
    [Theory]
    [MemberData(nameof(PinnedCasesAndCombos))]
    public void Both_v1_copies_agreed_on_this_case(string caseId, string comboName)
    {
        var gui = Golden.V1.CleanText["gui"][comboName][caseId].Sha256;
        var cli = Golden.V1.CleanText["cli"][comboName][caseId].Sha256;

        Assert.Equal(gui, cli);
    }

    [Fact]
    public void Counts_the_urls_it_deletes()
    {
        var result = TextCleaner.Clean("a https://a.com b http://b.com c https://c.com/x d");

        Assert.Equal(3, result.StrippedUrls);
        Assert.DoesNotContain("https://", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_nothing_when_links_are_kept()
    {
        var result = TextCleaner.Clean("see https://example.com/x", removeLinks: false);

        Assert.Equal(0, result.StrippedUrls);
        Assert.Contains("https://example.com/x", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_zero_when_there_are_no_urls() =>
        Assert.Equal(0, TextCleaner.Clean("no links here").StrippedUrls);

    /// <summary>
    /// The four separators Python calls whitespace and .NET does not. No corpus
    /// case carries one, so without this the divergence would sit unnoticed
    /// until a real page produced one.
    /// </summary>
    [Theory]
    [InlineData('\u001C')]
    [InlineData('\u001D')]
    [InlineData('\u001E')]
    [InlineData('\u001F')]
    public void Treats_the_c0_separators_as_whitespace_the_way_python_does(char separator)
    {
        var result = TextCleaner.Clean($"{separator}text{separator}");

        Assert.Equal("text", result.Text);
    }

    [Fact]
    public void Collapses_a_separator_run_before_a_newline()
    {
        var result = TextCleaner.Clean("one\u001C\u001D\ntwo");

        Assert.Equal("one\ntwo", result.Text);
    }

    /// <summary>
    /// Cleaning destroys paragraph breaks, and both implementations do it. The
    /// rule that looks like it preserves them - collapse three or more newlines
    /// to two - never fires, because the preceding substitution has already
    /// reduced every newline run to one.
    /// </summary>
    /// <remarks>
    /// Pinned rather than fixed: changing it would change the text of every
    /// chapter either implementation has ever produced.
    /// </remarks>
    [Theory]
    [InlineData("a\n\nb")]
    [InlineData("a\n\n\nb")]
    [InlineData("a\n\n\n\n\n\nb")]
    [InlineData("a  \n\n\n  b")]
    public void Blank_lines_do_not_survive_cleaning(string input) =>
        Assert.Equal("a\nb", TextCleaner.Clean(input).Text);

    [Fact]
    public void No_v1_output_anywhere_in_the_golden_contains_a_blank_line()
    {
        var outputs =
            from implementation in Golden.V1.CleanText.Values
            from combo in implementation.Values
            from entry in combo.Values
            select entry.Value;

        Assert.All(
            outputs,
            value => Assert.DoesNotContain("\n\n", value, StringComparison.Ordinal));
    }
}
