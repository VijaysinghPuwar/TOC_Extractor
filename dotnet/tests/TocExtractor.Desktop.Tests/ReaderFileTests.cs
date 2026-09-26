using Avalonia.Headless.XUnit;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>The reader opens a saved chapter from its file, rather than keeping every chapter in memory.</summary>
public sealed class ReaderFileTests
{
    [Fact]
    public void A_saved_chapter_is_read_back_from_its_file_without_its_heading()
    {
        var file = Path.Combine(Path.GetTempPath(), $"toc-reader-{Guid.NewGuid():N}.txt");
        const string text = "First line.\nSecond line, with a blank line above the heading's.";
        File.WriteAllText(file, "Chapter 7: The Storm\n\n" + text + "\n");
        try
        {
            var row = new ChapterRow(7, "") { File = file, TextLength = text.Length, State = ChapterState.Saved };

            Assert.True(row.IsReadable);
            Assert.Null(row.Text);
            Assert.Equal(text, row.ReadText());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_file_that_was_edited_is_shown_whole_and_a_missing_one_shows_nothing()
    {
        var file = Path.Combine(Path.GetTempPath(), $"toc-reader-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "short");
        try
        {
            var row = new ChapterRow(1, "") { File = file, TextLength = 5000, State = ChapterState.Saved };
            Assert.Equal("short", row.ReadText());
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Null(new ChapterRow(1, "") { File = file, TextLength = 5 }.ReadText());
    }

    [Fact]
    public void A_chapter_without_a_file_keeps_its_text()
    {
        var row = new ChapterRow(1, "") { Text = "Kept.", State = ChapterState.Saved };
        Assert.True(row.IsReadable);
        Assert.Equal("Kept.", row.ReadText());
    }

    [AvaloniaFact]
    public async Task Text_left_out_is_flagged_by_chapter_unless_the_site_repeats_it_everywhere()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 5;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        const string notice = "If you find any errors in this chapter, please let us know so we can fix them as soon as we possibly can.";
        const string story = "The keeper climbed the long stair again while the tide turned below the rocks and the lamp burned on into the night.";
        foreach (var row in harness.Job.Chapters.Take(4))
        {
            row.LeftOutLines = [notice];
        }

        harness.Job.Chapters[3].LeftOutLines = [notice, story];
        harness.Job.Chapters[4].Doubled = true;

        var messages = harness.Job.FlagIntegrity([]);

        // The notice on every page is the site's furniture; the story line is not.
        Assert.Equal([4], harness.Job.Chapters.Where(row => row.LeftOut).Select(row => row.Number));
        Assert.Equal("!", harness.Job.Chapters[3].Symbol);
        Assert.StartsWith("Saved, but", harness.Job.Chapters[3].StatusText, StringComparison.Ordinal);
        Assert.Equal(2, messages.Count);
        Assert.Equal([4], messages[0].Chapters);
        Assert.Contains("not saved", messages[0].Message, StringComparison.Ordinal);
        Assert.Equal([5], messages[1].Chapters);
        Assert.Contains("saved twice", messages[1].Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task When_every_chapter_checks_out_the_status_says_so()
    {
        var harness = new Harness();
        harness.Service.Audited = true;
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 3;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.StartsWith("Done. 3 of 3 saved.", harness.Job.Status, StringComparison.Ordinal);
        Assert.Contains("All 3 chapters saved this time were checked against the site's pages: nothing left out, nothing saved twice.", harness.Job.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("one")]
    [InlineData("  two  words ")]
    [InlineData("tabs\tand\nnew\r\nlines and wide spaces")]
    public void Words_are_counted_as_a_split_would_count_them(string text) =>
        Assert.Equal(
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            JobViewModel.CountWords(text));
}
