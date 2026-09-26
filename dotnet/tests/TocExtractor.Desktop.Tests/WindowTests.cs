using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TocExtractor.Desktop.Services;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>The real window, driven the way a person would, with a made-up book behind it.</summary>
public sealed class WindowTests
{
    public static TheoryData<int, int, string> Sizes() => new()
    {
        { 1280, 820, "Light" },
        { 1280, 820, "Dark" },
        { 1120, 760, "Light" },
        { 940, 600, "Light" },
        { 940, 600, "Dark" },
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
        await harness.ScannedAsync();
        harness.Job.From = 12;
        harness.Job.To = 20;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Save(harness.Window, $"size-{width}x{height}-{theme.ToLowerInvariant()}");

        foreach (var name in new[] { "NovelUrlBox", "ScanButton", "FromBox", "ToBox", "SaveButton", "StatusText", "ProgressLine", "BookTitleText", "NewJobButton", "SettingsButton", "JobList" })
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
    public async Task Before_a_scan_only_the_novel_page_is_asked_for()
    {
        UseTheme("Light");
        var harness = new Harness();
        await harness.StartAsync();

        Assert.True(Visible(harness, "NovelUrlBox"));
        Assert.False(Visible(harness, "FromBox"));
        Assert.False(Visible(harness, "SaveButton"));
        Assert.False(harness.Job.ScanCommand.CanExecute(null));
        Save(harness.Window, "start");
    }

    [AvaloniaFact]
    public async Task A_scan_shows_the_book_and_offers_its_whole_range()
    {
        var harness = new Harness();
        await harness.StartAsync();

        await harness.ScannedAsync();

        Assert.Equal("The Lighthouse", Text(harness, "BookTitleText"));
        Assert.Equal("Chapters 1 to 40.", Text(harness, "ScanSummaryText"));
        Assert.Equal(1, harness.Job.From);
        Assert.Equal(40, harness.Job.To);
        Assert.True(Visible(harness, "PlanText"));
        Assert.Equal(2, harness.Job.Step);
    }

    [AvaloniaFact]
    public async Task Saving_a_range_saves_exactly_those_chapters_in_order()
    {
        UseTheme("Light");
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 12;
        harness.Job.To = 20;
        harness.Job.Pdf = true;

        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal([12, 13, 14, 15, 16, 17, 18, 19, 20], harness.Job.Chapters.Select(r => r.Number));
        Assert.All(harness.Job.Chapters, row => Assert.Equal(ChapterState.Saved, row.State));
        Assert.Equal("9 of 9 saved", Text(harness, "ProgressLine"));
        Assert.Equal(["The Lighthouse 12-20.txt", "The Lighthouse 12-20.pdf"], harness.Job.Files);
        Assert.Equal("Done. 9 of 9 saved.", harness.Job.Status);
        Assert.True(harness.Job.HasOutput);
        Assert.Equal(3, harness.Job.Step);
        Save(harness.Window, "saved");

        harness.Job.SelectedChapter = harness.Job.Chapters[2];
        Harness.Pump();
        Assert.Equal(Tab.Reader, harness.Job.CurrentTab);
        Assert.Equal("Chapter 14: The Storm Road", Text(harness, "ReaderTitle"));
        Save(harness.Window, "reader");
    }

    [AvaloniaFact]
    public async Task The_range_cannot_leave_the_book()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();

        var from = harness.Window.FindControl<NumericUpDown>("FromBox")!;
        var to = harness.Window.FindControl<NumericUpDown>("ToBox")!;

        Assert.Equal(1, from.Minimum);
        Assert.Equal(40, to.Maximum);
    }

    [AvaloniaFact]
    public async Task Save_needs_at_least_one_format()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();

        harness.Job.Text = false;
        harness.Job.Pdf = false;

        Assert.False(harness.Job.SaveCommand.CanExecute(null));
        harness.Job.Pdf = true;
        Assert.True(harness.Job.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_site_that_needs_a_sign_in_says_so_and_offers_it()
    {
        UseTheme("Light");
        var harness = new Harness();
        harness.Service.RequireSignIn = true;
        await harness.StartAsync();

        await harness.ScannedAsync();

        Assert.Contains("Sign in required", Text(harness, "ProblemText"), StringComparison.Ordinal);
        Assert.True(Visible(harness, "SignInButton"));
        Assert.False(Visible(harness, "SaveButton"));
        Save(harness.Window, "sign-in-needed");

        await harness.Job.SignInCommand.ExecuteAsync(null);
        Harness.Pump();
        Assert.True(Visible(harness, "DoneButton"));

        await harness.Job.FinishSignInCommand.ExecuteAsync(null);
        await harness.ScannedAsync();

        Assert.True(harness.Job.ScanReady);
        Assert.EndsWith("Signed in.", Text(harness, "ScanSummaryText"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_sign_in_that_did_not_take_is_explained()
    {
        var harness = new Harness();
        harness.Service.RequireSignIn = true;
        harness.Service.SignInSucceeds = false;
        await harness.StartAsync();
        await harness.ScannedAsync();

        await harness.Job.SignInCommand.ExecuteAsync(null);
        await harness.Job.FinishSignInCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Contains("email and password", Text(harness, "ProblemText"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task While_the_site_checks_for_a_person_the_window_says_what_to_do()
    {
        UseTheme("Light");
        var harness = new Harness();
        harness.Service.PauseBefore = 15;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 12;
        harness.Job.To = 20;

        var saving = harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.True(Visible(harness, "PersonNotice"));
        Assert.Contains("check you're a person", Text(harness, "PersonText"), StringComparison.Ordinal);
        Assert.True(harness.Job.StopCommand.CanExecute(null));
        Assert.False(harness.Job.ScanCommand.CanExecute(null));
        Assert.Equal(3, harness.Job.SavedCount);
        Save(harness.Window, "person-needed");

        harness.Service.PauseAt.SetResult();
        await saving;
        Harness.Pump();

        Assert.False(Visible(harness, "PersonNotice"));
        Assert.Equal(9, harness.Job.SavedCount);
    }

    [AvaloniaFact]
    public async Task A_long_walk_is_explained_and_needs_a_second_press()
    {
        var harness = new Harness();
        harness.Service.SlowPlans = true;
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 30;
        harness.Job.To = 35;

        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal(0, harness.Service.Saves);
        Assert.Contains("Press Save again", Text(harness, "ProblemText"), StringComparison.Ordinal);

        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();
        Assert.Equal(1, harness.Service.Saves);
    }

    [AvaloniaFact]
    public async Task Failed_chapters_are_marked_and_counted()
    {
        var harness = new Harness();
        harness.Service.Failing.UnionWith([13, 17]);
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 12;
        harness.Job.To = 20;

        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal("7 of 9 saved, 2 failed", Text(harness, "ProgressLine"));
        Assert.Equal("7 of 9 saved, 2 not. Press Save again to retry the rest.", harness.Job.Status);
        Assert.Equal(ChapterState.Failed, harness.Job.Chapters.Single(r => r.Number == 13).State);
        Assert.Contains("Save again", harness.Job.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Open_folder_opens_the_books_own_folder()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Directory.CreateDirectory(Path.Combine(harness.Job.OutputDirectory, "The Lighthouse"));

        await harness.Job.OpenFolderCommand.ExecuteAsync(null);

        Assert.EndsWith("The Lighthouse", Assert.Single(harness.Shell.Opened), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_first_run_download_blocks_scanning()
    {
        UseTheme("Light");
        var harness = new Harness(browserReady: false);
        harness.Window.Show();
        _ = harness.ViewModel.StartAsync();
        Harness.Pump();
        harness.Job.NovelUrl = FakeNovelService.NovelUrl;

        Assert.False(harness.ViewModel.BrowserReady);
        Assert.False(harness.Job.ScanCommand.CanExecute(null));
        Assert.True(harness.ViewModel.Installing);
        Save(harness.Window, "first-run");
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void The_form_is_remembered_between_launches()
    {
        var store = new SettingsStore(Path.Combine(Harness.Scratch(), "settings.json"));
        store.Save(new DesktopSettings { NovelUrl = FakeNovelService.NovelUrl, Pdf = true, KeepLog = false, AtOnce = 2, Theme = "Dark" });

        var loaded = store.Load();

        Assert.Equal(FakeNovelService.NovelUrl, loaded.NovelUrl);
        Assert.True(loaded.Pdf);
        Assert.False(loaded.KeepLog);
        Assert.Equal(2, loaded.AtOnce);
        Assert.Equal("Dark", loaded.Theme);
    }

    [AvaloniaFact]
    public void A_broken_settings_file_falls_back_to_the_defaults()
    {
        var path = Path.Combine(Harness.Scratch(), "settings.json");
        File.WriteAllText(path, "{ not json");

        var loaded = new SettingsStore(path).Load();

        Assert.True(loaded.Text);
        Assert.True(loaded.KeepLog);
        Assert.Equal(1, loaded.AtOnce);
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
        // A readable folder in the picture, not the test's temporary one.
        if (window.DataContext is MainViewModel { SelectedJob: { } job })
        {
            job.OutputDirectory = "~/Downloads/Novels";
        }

        Harness.Pump();
        var directory = Environment.GetEnvironmentVariable("TOC_SCREENSHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
