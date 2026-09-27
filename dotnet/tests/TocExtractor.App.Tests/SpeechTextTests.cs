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
    public void A_title_the_site_repeats_at_the_top_of_the_text_is_left_out_too()
    {
        // Some sites start every chapter's text with its title again, so a
        // voice still read "Chapter 151" with headings left out.
        SavedChapter[] chapters =
        [
            new(151, "Chapter 151 Peak of the Mortal World!", "Chapter 151 Peak of the Mortal World!\nTu Ling'er arrived."),
            new(152, "Chapter 152: Dao Heart", "Chapter 152\n\nHan Jue sat."),
        ];

        var text = BookFiles.Combined(chapters, headings: false);

        Assert.DoesNotContain("Chapter", text, StringComparison.Ordinal);
        Assert.StartsWith("Tu Ling'er arrived.\n\nHan Jue sat.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void With_headings_a_repeated_title_shows_once()
    {
        var text = BookFiles.Chapter("Chapter 151 Peak", "Chapter 151 Peak\nStory.");

        Assert.Equal("Chapter 151 Peak\n\nStory.", text);
    }

    [Theory]
    [InlineData("Chapter 3: Dawn", "[Name: Han Jue]\n[Lifespan: 11]")]
    [InlineData("Chapter 3: Dawn", "Chapter 4 was never written.\nHe slept.")]
    [InlineData("Chapter 3: Dawn", "Dawn broke over the hills.\nHe slept.")]
    [InlineData("", "Chapter 3\nHe slept.")]
    public void A_first_line_that_is_story_stays(string heading, string body)
    {
        Assert.Equal(body, BookFiles.Story(heading, body));
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
