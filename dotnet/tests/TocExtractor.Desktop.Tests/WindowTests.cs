using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TocExtractor.App.Profiles;
using TocExtractor.App.Session;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>The real window, driven the way a person would, with a fake book behind it.</summary>
public sealed class WindowTests
{
    public static TheoryData<int, int, string> Sizes() => new()
    {
        { 1120, 760, "Light" },
        { 1120, 760, "Dark" },
        { 900, 640, "Light" },
        { 760, 560, "Light" },
        { 760, 560, "Dark" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Nothing_is_cut_off_at_any_supported_size(int width, int height, string theme)
    {
        UseTheme(theme);
        var harness = new Harness();
        harness.Window.Width = width;
        harness.Window.Height = height;
        await harness.StartAsync();
        await harness.ThroughToReadyAsync();
        await harness.ViewModel.TestSelectorsCommand.ExecuteAsync(null);
        Harness.Pump();

        Save(harness.Window, $"size-{width}x{height}-{theme.ToLowerInvariant()}");

        foreach (var name in new[] { "TocUrlBox", "LinkBox", "TestButton", "StartButton", "StatusText", "PreviewHeadline" })
        {
            var control = harness.Window.FindControl<Control>(name);
            Assert.NotNull(control);
            var origin = control.TranslatePoint(default, harness.Window);
            Assert.NotNull(origin);
            var right = origin.Value.X + control.Bounds.Width;
            Assert.True(right <= width + 0.5, $"{name} ends at {right:0} in a {width} px window");
            Assert.True(control.Bounds.Width > 0, $"{name} has no width at {width} px");
        }
    }

    [AvaloniaFact]
    public async Task The_step_strip_follows_the_flow()
    {
        var harness = new Harness();
        await harness.StartAsync();
        Assert.Equal(1, harness.ViewModel.Step);
        Assert.True(Visible(harness, "LaunchButton"));
        Assert.False(Visible(harness, "StartButton"));

        harness.FillForm();
        await harness.ViewModel.LaunchCommand.ExecuteAsync(null);
        Harness.Pump();
        Assert.Equal(2, harness.ViewModel.Step);
        Assert.True(Visible(harness, "ConfirmButton"));
        Assert.False(Visible(harness, "LaunchButton"));

        await harness.ViewModel.ConfirmCommand.ExecuteAsync(null);
        Harness.Pump();
        Assert.Equal(3, harness.ViewModel.Step);

        await harness.ViewModel.ExtractCommand.ExecuteAsync(null);
        Harness.Pump();
        Assert.Equal(4, harness.ViewModel.Step);
    }

    [AvaloniaFact]
    public async Task Open_browser_waits_for_the_download_on_first_run()
    {
        var harness = new Harness(browserReady: false);
        harness.Window.Show();
        _ = harness.ViewModel.StartAsync();
        Harness.Pump();
        harness.FillForm();

        Assert.False(harness.ViewModel.BrowserReady);
        Assert.False(harness.ViewModel.LaunchCommand.CanExecute(null));
        Assert.True(harness.ViewModel.Installing);
        Save(harness.Window, "first-run");
    }

    [AvaloniaFact]
    public async Task Testing_selectors_shows_the_count_and_the_first_chapter()
    {
        var harness = new Harness(chapters: 6, robots: "User-agent: *\nCrawl-delay: 2\n");
        await harness.StartAsync();
        await harness.ThroughToReadyAsync();

        await harness.ViewModel.TestSelectorsCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal("6 chapters found", Text(harness, "PreviewHeadline"));
        Assert.Equal("Chapter 1: The Lighthouse Keeper", Text(harness, "SampleTitle"));
        Assert.Contains("2s delay", harness.ViewModel.RobotsLine, StringComparison.Ordinal);
        Assert.False(Directory.EnumerateFileSystemEntries(harness.Output).Any(), "a test must write nothing");
    }

    [AvaloniaFact]
    public async Task A_bad_content_selector_is_explained_in_the_preview()
    {
        var harness = new Harness(broken: [1]);
        await harness.StartAsync();
        await harness.ThroughToReadyAsync();

        await harness.ViewModel.TestSelectorsCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Contains("matched nothing", Text(harness, "SampleProblem"), StringComparison.Ordinal);
        Assert.True(Visible(harness, "SampleProblem"));
    }

    [AvaloniaFact]
    public async Task Saving_fills_the_chapter_list_and_the_reader()
    {
        UseTheme("Light");
        var harness = new Harness(chapters: 8);
        await harness.StartAsync();
        await harness.ThroughToReadyAsync();

        await harness.ViewModel.ExtractCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal(8, harness.ViewModel.Chapters.Count);
        Assert.All(harness.ViewModel.Chapters, row => Assert.Equal(ChapterState.Saved, row.State));
        Assert.Equal("8 of 8 saved", Text(harness, "ProgressLine"));
        Assert.True(harness.ViewModel.HasOutput);
        Assert.True(File.Exists(Path.Combine(harness.Output, "001 - Chapter 1_ The Lighthouse Keeper.txt"))
            || Directory.GetFiles(harness.Output, "001 - *.txt").Length == 1);
        Save(harness.Window, "chapters");

        harness.ViewModel.SelectedChapter = harness.ViewModel.Chapters[2];
        Harness.Pump();

        Assert.Equal(Tab.Reader, harness.ViewModel.CurrentTab);
        Assert.Equal("Chapter 3: Low Tide", Text(harness, "ReaderTitle"));
        Save(harness.Window, "reader");
    }

    [AvaloniaFact]
    public async Task A_failed_chapter_is_marked_and_counted()
    {
        var harness = new Harness(chapters: 4, broken: [2]);
        await harness.StartAsync();
        await harness.ThroughToReadyAsync();

        await harness.ViewModel.ExtractCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal(1, harness.ViewModel.FailedCount);
        Assert.Equal("3 of 4 saved, 1 failed", Text(harness, "ProgressLine"));
        Assert.Equal(ChapterState.Failed, harness.ViewModel.Chapters[1].State);
    }

    [AvaloniaFact]
    public async Task A_problem_is_shown_and_can_be_dismissed()
    {
        var harness = new Harness();
        await harness.StartAsync();
        harness.ViewModel.TocUrl = "not an address";

        await harness.ViewModel.LaunchCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.True(harness.ViewModel.HasProblem);
        Assert.Contains("https://", Text(harness, "ProblemText"), StringComparison.Ordinal);
        harness.ViewModel.DismissProblemCommand.Execute(null);
        Assert.False(harness.ViewModel.HasProblem);
        Assert.Equal(SessionPhase.Idle, harness.ViewModel.Phase);
    }

    [AvaloniaFact]
    public async Task A_profile_saved_from_the_window_loads_back()
    {
        var harness = new Harness();
        await harness.StartAsync();
        harness.FillForm();
        harness.ViewModel.MaxChapters = 70;
        harness.ViewModel.FormatMarkdown = true;
        var path = Path.Combine(Harness.Scratch(), "novel.toml");
        harness.Shell.NextProfileToSave = path;

        await harness.ViewModel.SaveProfileCommand.ExecuteAsync(null);
        harness.ViewModel.LinkSelector = "";
        harness.ViewModel.MaxChapters = 5;
        harness.Shell.NextProfileToOpen = path;
        await harness.ViewModel.LoadProfileCommand.ExecuteAsync(null);

        Assert.Equal("ol.chapters a", harness.ViewModel.LinkSelector);
        Assert.Equal(70, harness.ViewModel.MaxChapters);
        Assert.True(harness.ViewModel.FormatMarkdown);
        Assert.Equal("ol.chapters a", ProfileLoader.Load(path).Link);
    }

    [AvaloniaFact]
    public async Task A_broken_profile_is_reported_not_applied()
    {
        var harness = new Harness();
        await harness.StartAsync();
        harness.FillForm();
        var path = Path.Combine(Harness.Scratch(), "broken.toml");
        await File.WriteAllTextAsync(path, "[selectors]\nlnk = \"a\"\n", TestContext.Current.CancellationToken);
        harness.Shell.NextProfileToOpen = path;

        await harness.ViewModel.LoadProfileCommand.ExecuteAsync(null);

        Assert.Contains("unknown key", harness.ViewModel.Problem, StringComparison.Ordinal);
        Assert.Equal("ol.chapters a", harness.ViewModel.LinkSelector);
    }

    [AvaloniaFact]
    public async Task The_form_is_remembered_between_launches()
    {
        var store = new Services.SettingsStore(Path.Combine(Harness.Scratch(), "settings.json"));
        var settings = new SessionSettings { TocUrl = Harness.Toc, LinkSelector = "a.ch", MaxChapters = 33 };
        store.Save(settings with { Force = true });

        var loaded = store.Load();

        Assert.Equal("a.ch", loaded.LinkSelector);
        Assert.Equal(33, loaded.MaxChapters);
        Assert.False(loaded.Force, "Start over must never be remembered");
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void A_profile_is_named_after_the_site()
    {
        Assert.Equal("novel.example.toml", MainViewModel.SuggestedProfileName("https://www.novel.example/toc"));
        Assert.Equal("my-site.toml", MainViewModel.SuggestedProfileName("not a url"));
    }

    private static void UseTheme(string theme) =>
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    private static string Text(Harness harness, string name)
    {
        var control = harness.Window.FindControl<Control>(name);
        return control switch
        {
            TextBlock block => block.Text ?? "",
            _ => throw new InvalidOperationException($"{name} is not text"),
        };
    }

    private static bool Visible(Harness harness, string name) =>
        harness.Window.FindControl<Control>(name) is { } control
        && control.IsVisible
        && control.GetVisualAncestors().OfType<Visual>().All(ancestor => ancestor.IsVisible);

    /// <summary>Keep a picture of the render, for looking at by eye and for the README.</summary>
    private static void Save(Window window, string name)
    {
        Harness.Pump();
        var directory = Environment.GetEnvironmentVariable("TOC_SCREENSHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
