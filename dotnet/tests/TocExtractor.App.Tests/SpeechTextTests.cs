using TocExtractor.App.Pipeline;

namespace TocExtractor.App.Tests;

public sealed class SpeechTextTests
{
    [Theory]
    [InlineData("He smiled.\n==========\nShe left.", "He smiled.\nShe left.")]
    [InlineData("He smiled.\n* * *\nShe left.", "He smiled.\nShe left.")]
    [InlineData("-----", "")]
    [InlineData("#### Status Panel ####", "Status Panel")]
    [InlineData("Strength: 10 == Agility: 12", "Strength: 10 Agility: 12")]
    [InlineData("He paused -- then ran.", "He paused then ran.")]
    [InlineData("He paused - then ran.", "He paused then ran.")]
    [InlineData("- first item", "first item")]
    [InlineData("<System> Quest ~complete~", "System Quest complete")]
    [InlineData("snake_case and #tag", "snake case and tag")]
    public void Symbols_a_voice_reads_aloud_are_taken_out(string text, string expected)
    {
        Assert.Equal(expected, SpeechText.Clean(text));
    }

    [Theory]
    [InlineData("A well-known, twenty-one-year-old hero.")]
    [InlineData("\"Wait!\" she said. \"Is it +5 strength?\"")]
    [InlineData("It cost 3.5 gold (not 4); fine: yes.")]
    [InlineData("[Ding! You gained a level.]")]
    [InlineData("First paragraph.\n\nSecond paragraph.")]
    public void Words_and_punctuation_are_left_exactly_as_they_were(string text)
    {
        Assert.Equal(text, SpeechText.Clean(text));
    }

    [Fact]
    public void Blank_lines_left_by_removed_dividers_collapse()
    {
        Assert.Equal("One.\n\nTwo.", SpeechText.Clean("One.\n\n=====\n\n\nTwo."));
    }

    [Fact]
    public void A_book_without_headings_goes_straight_from_story_to_story()
    {
        SavedChapter[] chapters = [new(1, "Chapter 1. Dawn", "It began."), new(2, "Chapter 2. Noon", "It went on.")];

        var text = BookFiles.Combined(chapters, headings: false);

        Assert.DoesNotContain("Chapter", text, StringComparison.Ordinal);
        Assert.StartsWith("It began.\n\nIt went on.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_book_for_speech_has_no_dividers_between_chapters()
    {
        SavedChapter[] chapters = [new(1, "Chapter 1", "A.\n=====\nB."), new(2, "Chapter 2", "C.")];

        var text = BookFiles.Combined(chapters, headings: true, forSpeech: true);

        Assert.Equal("Chapter 1\n\nA.\nB.\n\nChapter 2\n\nC.\n\n", text);
    }

    [Fact]
    public void The_usual_book_is_unchanged()
    {
        SavedChapter[] chapters = [new(1, "Chapter 1", "A.")];

        Assert.Equal("Chapter 1\n\nA.\n\n" + new string('-', 40) + "\n\n", BookFiles.Combined(chapters));
    }
}
