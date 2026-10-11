using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace TocExtractor.Desktop.Tests;

/// <summary>
/// The window with the longest text a site or a failure can put in it, at
/// the smallest and the usual size. Nothing may stick out of the box it sits
/// in: long text wraps or ends in an ellipsis instead.
/// </summary>
public sealed class LayoutTests
{
    private const string LongTitle =
        "The Reincarnated Lighthouse Keeper Who Became the Strongest Archmage of the Northern Harbour Kingdoms and Then Retired";

    public static TheoryData<int, int, string> Sizes() => new()
    {
        { 940, 600, "Light" },
        { 940, 600, "Dark" },
        { 1280, 820, "Light" },
        { 1920, 1080, "Light" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Long_text_stays_inside_its_box(int width, int height, string theme)
    {
        UseTheme(theme);
        var harness = new Harness();
        harness.Window.Width = width;
        harness.Window.Height = height;
        harness.Service.Book = LongTitle;
        harness.Service.Failing.Add(14);
        await harness.StartAsync();
        harness.Job.NovelUrl = "https://www.a-very-long-novel-site-name.example/book/" + new string('x', 120);
        Harness.Pump();
        await harness.Job.ScanCommand.ExecuteAsync(null);
        Harness.Pump();
        harness.Job.From = 12;
        harness.Job.To = 20;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        harness.Job.Problem = "Something went wrong during the save: " + string.Concat(Enumerable.Repeat("a long reason without many spaces/", 12))
            + " The details are in the log (Settings, Open log folder).";
        Harness.Pump();

        // Several more extractions, one of them with a site's host as its name.
        harness.NewJob().NovelUrl = "https://" + new string('n', 60) + ".example/book";
        for (var i = 0; i < 6; i++)
        {
            harness.NewJob();
        }

        harness.ViewModel.SelectedJob = harness.ViewModel.Jobs[0];
        Harness.Pump();

        Shots.Save(harness.Window, $"layout-{width}x{height}-{theme.ToLowerInvariant()}");
        AssertNothingSticksOut(harness.Window);
        AssertRoomToRead(harness.Window);

        harness.Job.CurrentTab = ViewModels.Tab.Activity;
        Harness.Pump();
        Shots.Save(harness.Window, $"layout-activity-{width}x{height}-{theme.ToLowerInvariant()}");
        AssertNothingSticksOut(harness.Window);

        harness.ViewModel.SitePaces.RecordCheck("https://" + new string('s', 70) + ".example/book/1", DateTimeOffset.Now);
        harness.ViewModel.OpenSettingsCommand.Execute(null);
        Harness.Pump();
        Shots.Save(harness.Window, $"layout-settings-{width}x{height}-{theme.ToLowerInvariant()}");
        AssertNothingSticksOut(harness.Window);
    }

    [AvaloniaFact]
    public async Task The_status_does_not_repeat_the_count_under_it()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 12;
        harness.Job.To = 20;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal("Done. 9 of 9 saved.", harness.Job.Status);
        Assert.Equal("Done.", harness.Job.StatusShown);

        harness.Job.Status = "Stopped. 9 of 9 saved. Press Save to carry on.";
        Assert.Equal("Stopped. Press Save to carry on.", harness.Job.StatusShown);

        harness.Job.Status = "9 of 9 saved. Writing the book.";
        Assert.Equal("Writing the book.", harness.Job.StatusShown);

        harness.Job.Status = "9 of 9 saved";
        Assert.Equal("", harness.Job.StatusShown);

        // Away from the chapters the count is not shown anywhere else.
        harness.Job.Status = "Stopped. 9 of 9 saved. Press Save to carry on.";
        harness.Job.CurrentTab = ViewModels.Tab.Activity;
        Assert.Equal(harness.Job.Status, harness.Job.StatusShown);
        harness.Job.CurrentTab = ViewModels.Tab.Chapters;

        // A count that is not the one under it is kept whole.
        harness.Job.Status = "Done. 19 of 9 saved.";
        Assert.Equal("Done. 19 of 9 saved.", harness.Job.StatusShown);
    }

    [AvaloniaFact]
    public async Task A_count_inside_a_bigger_number_is_not_taken_for_the_progress()
    {
        var harness = new Harness();
        harness.Service.PauseBefore = 1;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 10;
        var saving = harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        // The rows still say none saved while the result already says all.
        Assert.Equal("0 of 10 saved", harness.Job.ProgressLine);
        harness.Job.Status = "Done. 10 of 10 saved.";
        Assert.Equal("Done. 10 of 10 saved.", harness.Job.StatusShown);

        harness.Service.PauseAt.SetResult();
        await saving;
    }

    /// <summary>The worst-case text still leaves the chapters readable: a list with rows in view, each title with room.</summary>
    private static void AssertRoomToRead(Window window)
    {
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Classes.Contains("chapters"));
        Assert.True(list.Bounds.Height >= 100, $"The chapter list is {list.Bounds.Height:0} px high");
        var headings = list.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.Text?.StartsWith("Chapter ", StringComparison.Ordinal) == true && Shown(block))
            .ToList();
        Assert.NotEmpty(headings);
        Assert.All(headings, heading => Assert.True(heading.Bounds.Width >= 60, $"\"{heading.Text}\" has {heading.Bounds.Width:0} px"));
    }

    /// <summary>Every visible control ends inside its parent and inside the window.</summary>
    internal static void AssertNothingSticksOut(Window window)
    {
        List<string> problems = [];
        foreach (var control in window.GetVisualDescendants().OfType<Control>())
        {
            if (!Shown(control) || control.Bounds.Width <= 0 || Scrolls(control))
            {
                continue;
            }

            var origin = control.TranslatePoint(default, window);
            if (origin is null)
            {
                continue;
            }

            var right = origin.Value.X + control.Bounds.Width;
            if (right > window.Bounds.Width + 1)
            {
                problems.Add($"{Describe(control)} ends at {right:0} in a {window.Bounds.Width:0} px window ({Path(control)})");
            }

            if (control.GetVisualParent() is Control parent && control.Bounds.Right > parent.Bounds.Width + 1)
            {
                problems.Add($"{Describe(control)} is {control.Bounds.Width:0} wide in a {Describe(parent)} {parent.Bounds.Width:0} wide ({Path(control)})");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    /// <summary>
    /// Inside something that scrolls sideways on purpose, a text box's line
    /// for one, or the theme's own drawing: a Viewbox scales its glyph to
    /// fit, a number box's border overlaps its text box, and a scroll bar
    /// shrinks its thumb by a transform while idle, all by design.
    /// </summary>
    private static bool Scrolls(Control control) =>
        control.GetVisualAncestors().Any(ancestor =>
            ancestor is TextBox or NumericUpDown or Viewbox or ScrollBar or ScrollContentPresenter { CanHorizontallyScroll: true });

    private static string Path(Control control) =>
        string.Join(" < ", control.GetVisualAncestors().OfType<Control>().Take(16).Select(Describe));

    private static bool Shown(Visual control) =>
        control.IsVisible && control.GetVisualAncestors().All(ancestor => ancestor.IsVisible);

    private static string Describe(Control control) =>
        control.GetType().Name + $"[{control.Bounds.X:0},{control.Bounds.Width:0}]" + (string.IsNullOrEmpty(control.Name) ? "" : " " + control.Name)
        + (control is TextBlock { Text: { } text } ? $" \"{text[..Math.Min(text.Length, 40)]}\"" : "");

    private static void UseTheme(string theme) =>
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
}

/// <summary>Pictures of the render, for looking at by eye.</summary>
internal static class Shots
{
    public static void Save(Window window, string name)
    {
        Harness.Pump();
        var directory = Environment.GetEnvironmentVariable("TOC_SCREENSHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        Harness.SettleArrows(window);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
