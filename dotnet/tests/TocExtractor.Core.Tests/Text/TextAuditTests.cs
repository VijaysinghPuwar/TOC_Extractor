using TocExtractor.Core.Text;

namespace TocExtractor.Core.Tests.Text;

/// <summary>The check that nothing on the page was skipped, and nothing saved twice.</summary>
public sealed class TextAuditTests
{
    /// <summary>A made-up paragraph of a story's length, distinct for each number.</summary>
    private static string Paragraph(int n) =>
        $"Paragraph {n} begins here, and the lantern keeper walks the long stair again while the tide turns below the rocks tonight.";

    private static string Story(params int[] paragraphs) => string.Join("\n", paragraphs.Select(Paragraph));

    [Fact]
    public void Everything_saved_is_clean()
    {
        var audit = TextAudit.Compare(Story(1, 2, 3), Story(1, 2, 3));

        Assert.False(audit.LeftOutStory);
        Assert.False(audit.DoubledText);
        Assert.Equal(0, audit.LeftOutWords);
        Assert.Equal(audit.PageWords, audit.SavedWords);
    }

    [Fact]
    public void Short_furniture_left_out_is_not_story()
    {
        var page = "Share to your friends\n" + Story(1, 2) + "\nMore\n9 h 55 min\nReport chapter";

        var audit = TextAudit.Compare(page, Story(1, 2));

        Assert.False(audit.LeftOutStory);
        Assert.True(audit.LeftOutWords > 0);
    }

    [Fact]
    public void A_story_paragraph_left_out_is_caught()
    {
        var audit = TextAudit.Compare(Story(1, 2, 3), Story(1, 3));

        Assert.True(audit.LeftOutStory);
        Assert.Equal([Paragraph(2)], audit.LeftOutLong);
    }

    [Fact]
    public void A_short_saved_line_inside_a_missing_paragraph_does_not_hide_it()
    {
        var missing = "He nodded. " + Paragraph(2);
        var page = Paragraph(1) + "\n" + missing + "\nHe nodded.";

        var audit = TextAudit.Compare(page, Paragraph(1) + "\nHe nodded.");

        Assert.True(audit.LeftOutStory);
    }

    [Fact]
    public void A_line_split_or_joined_differently_still_counts_as_kept()
    {
        // The saved line is the page line with a trailing tag removed.
        var page = Paragraph(1) + " [ad]";

        Assert.False(TextAudit.Compare(page, Paragraph(1)).LeftOutStory);
    }

    [Fact]
    public void A_paragraph_saved_twice_is_caught()
    {
        var audit = TextAudit.Compare(Story(1, 2), Story(1, 2, 2));

        Assert.True(audit.DoubledText);
        Assert.Equal(1, audit.Doubled);
    }

    [Fact]
    public void A_paragraph_the_page_itself_repeats_is_not_doubled()
    {
        // Authors repeat lines; saving what the page shows is faithful.
        var audit = TextAudit.Compare(Story(1, 2, 1), Story(1, 2, 1));

        Assert.False(audit.DoubledText);
    }

    [Fact]
    public void Spacing_and_zero_width_characters_do_not_count_as_changes()
    {
        var page = "  Paragraph 1 begins here,​ and the lantern keeper walks the long stair again while the tide turns below the rocks tonight.  ";

        var audit = TextAudit.Compare(page, Paragraph(1));

        Assert.False(audit.LeftOutStory);
        Assert.Equal(0, audit.LeftOutWords);
    }
}
