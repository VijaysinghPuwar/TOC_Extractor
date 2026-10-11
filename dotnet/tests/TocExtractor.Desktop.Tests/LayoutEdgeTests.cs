using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>Real rendered content stays usable with long titles and diagnostics.</summary>
public sealed class LayoutEdgeTests
{
    [AvaloniaTheory]
    [InlineData(940, 600)]
    [InlineData(1280, 820)]
    public async Task A_long_failed_chapter_keeps_its_heading_and_status_visible(int width, int height)
    {
        var harness = await OpenAsync(width, height);
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 1;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        var row = Assert.Single(harness.Job.Chapters);
        row.Title = "Chapter 1: The lighthouse keeper crosses the harbour in a storm that will not end";
        row.State = ChapterState.Failed;
        row.Detail = "The content selector article matched nothing at https://novel.example/book/the-lighthouse/1. Please check the page and try again.";
        Harness.Pump();

        var list = harness.Window.GetVisualDescendants().OfType<ListBox>()
            .Single(control => ReferenceEquals(control.ItemsSource, harness.Job.Chapters));
        var heading = list.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Text == row.Heading);
        var status = list.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Text == row.StatusText);
        Save(harness.Window, $"long-failure-{width}x{height}");

        Assert.True(heading.Bounds.Width >= 80, $"Heading has only {heading.Bounds.Width:0} px at {width} px");
        Assert.True(status.Bounds.Width >= 80, $"Status has only {status.Bounds.Width:0} px at {width} px");
        AssertWithin(heading, list);
        AssertWithin(status, list);
        harness.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(940, 600)]
    [InlineData(1280, 820)]
    public async Task A_long_error_leaves_room_to_inspect_the_chapters(int width, int height)
    {
        var harness = await OpenAsync(width, height);
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 3;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        harness.Job.Problem = string.Concat(Enumerable.Repeat(
            "The chapter could not be saved because the page returned an unexpected result. Please check the browser and retry. ", 12));
        Harness.Pump();

        var list = harness.Window.GetVisualDescendants().OfType<ListBox>()
            .Single(control => ReferenceEquals(control.ItemsSource, harness.Job.Chapters));
        Save(harness.Window, $"long-error-{width}x{height}");

        Assert.True(list.Bounds.Height >= 100, $"Chapter list has only {list.Bounds.Height:0} px at {width}x{height}");
        AssertWithin(list, harness.Window);
        harness.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(940, 600)]
    [InlineData(1280, 820)]
    public async Task A_long_reader_heading_leaves_the_story_readable(int width, int height)
    {
        var harness = await OpenAsync(width, height);
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 1;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        var row = Assert.Single(harness.Job.Chapters);
        row.Title = "Chapter 1: " + string.Concat(Enumerable.Repeat("The lighthouse keeper and the storm road ", 20));
        harness.Job.SelectedChapter = row;
        Harness.Pump();

        var story = harness.Window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(control => control.Text == harness.Job.ReaderText);
        var viewport = story.GetVisualAncestors().OfType<ScrollViewer>().First();
        Save(harness.Window, $"long-reader-{width}x{height}");

        Assert.True(viewport.Bounds.Height >= 100, $"Reader viewport has only {viewport.Bounds.Height:0} px at {width}x{height}");
        AssertWithin(viewport, harness.Window);
        harness.Window.Close();
    }

    private static async Task<Harness> OpenAsync(int width, int height)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var harness = new Harness();
        harness.Window.Width = width;
        harness.Window.Height = height;
        await harness.StartAsync();
        return harness;
    }

    private static void AssertWithin(Control control, Control container)
    {
        var origin = control.TranslatePoint(default, container);
        Assert.NotNull(origin);
        Assert.True(origin.Value.X >= -0.5 && origin.Value.Y >= -0.5,
            $"{control.GetType().Name} starts outside its container at {origin}");
        Assert.True(origin.Value.X + control.Bounds.Width <= container.Bounds.Width + 0.5,
            $"{control.GetType().Name} extends horizontally beyond its container");
        Assert.True(origin.Value.Y + control.Bounds.Height <= container.Bounds.Height + 0.5,
            $"{control.GetType().Name} extends vertically beyond its container");
    }

    private static void Save(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("TOC_SCREENSHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        Harness.SettleArrows(window);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
