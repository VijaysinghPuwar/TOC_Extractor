using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>Several extractions at once, the settings screen, and the logs.</summary>
public sealed class ParallelTests
{
    [AvaloniaFact]
    public async Task A_second_book_starts_and_finishes_while_the_first_is_still_saving()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var harness = new Harness();
        harness.Service.PauseBefore = 15;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var first = harness.Job;
        first.From = 12;
        first.To = 20;
        var firstSaving = first.SaveCommand.ExecuteAsync(null);
        Harness.Pump();
        Assert.Equal(Stage.Saving, first.Stage);

        // No waiting: a new extraction can be started and run to the end.
        Assert.True(harness.ViewModel.NewJobCommand.CanExecute(null));
        var second = harness.NewJob();
        harness.Services[1].Book = "The Orchard";
        Assert.NotSame(first, second);
        Assert.Same(second, harness.ViewModel.SelectedJob);
        second.NovelUrl = "https://other.example/the-orchard";
        await second.ScanCommand.ExecuteAsync(null);
        second.From = 1;
        second.To = 5;
        await second.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal("Done. 5 of 5 saved.", second.Status);
        Assert.Equal(Stage.Saving, first.Stage);
        Assert.Equal(1, harness.ViewModel.RunningCount);
        Assert.Equal("Needs you in the browser", first.RailStatus);
        Assert.Equal("Done, 5 of 5", second.RailStatus);
        Assert.Equal("\"The Lighthouse\" needs you in the browser window.", harness.ViewModel.OtherNeedsPerson);
        SaveShot(harness.Window, "two-extractions");

        harness.Service.PauseAt.SetResult();
        await firstSaving;
        Harness.Pump();

        Assert.Equal("Done. 9 of 9 saved.", first.Status);
        Assert.Equal(0, harness.ViewModel.RunningCount);
        Assert.Equal(2, harness.ViewModel.Jobs.Count);
    }

    [AvaloniaFact]
    public async Task Many_extractions_run_at_once_with_no_limit()
    {
        var harness = new Harness();
        await harness.StartAsync();
        List<Task> saving = [];
        List<JobViewModel> jobs = [];
        for (var i = 0; i < 12; i++)
        {
            var job = i == 0 ? harness.Job : harness.NewJob();
            var service = harness.Services[i];
            service.Book = $"Book {i}";
            service.PauseBefore = 3;
            service.PauseAt = new TaskCompletionSource();
            job.NovelUrl = FakeNovelService.NovelUrl;
            await job.ScanCommand.ExecuteAsync(null);
            job.From = 1;
            job.To = 6;
            saving.Add(job.SaveCommand.ExecuteAsync(null));
            jobs.Add(job);
        }

        Harness.Pump();
        Assert.Equal(12, harness.ViewModel.RunningCount);
        Assert.Equal("12 extractions running", harness.ViewModel.RunningText);

        foreach (var service in harness.Services)
        {
            service.PauseAt!.SetResult();
        }

        await Task.WhenAll(saving);
        Harness.Pump();
        Assert.All(jobs, job => Assert.Equal("Done. 6 of 6 saved.", job.Status));
    }

    [AvaloniaFact]
    public async Task Two_ranges_of_the_same_book_save_at_the_same_time()
    {
        var harness = new Harness();
        harness.Service.Chapters = 200;
        harness.Service.PauseBefore = 105;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var first = harness.Job;
        first.From = 101;
        first.To = 150;
        var firstSaving = first.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        var second = harness.NewJob();
        harness.Services[1].Chapters = 200;
        await harness.ScannedAsync();
        second.From = 151;
        second.To = 200;
        await second.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        // The second finished while the first was still saving: no queue.
        Assert.Equal("Done. 50 of 50 saved.", second.Status);
        Assert.Equal(Stage.Saving, first.Stage);

        harness.Service.PauseAt.SetResult();
        await firstSaving;
        Harness.Pump();
        Assert.Equal("Done. 50 of 50 saved.", first.Status);
    }

    [AvaloniaFact]
    public async Task The_same_book_into_another_folder_runs_at_once()
    {
        var harness = new Harness();
        harness.Service.PauseBefore = 2;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var saving = harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        var second = harness.NewJob();
        await harness.ScannedAsync();
        second.OutputDirectory = Harness.Scratch();
        await second.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.StartsWith("Done.", second.Status, StringComparison.Ordinal);

        harness.Service.PauseAt.SetResult();
        await saving;
    }

    [AvaloniaFact]
    public async Task A_running_extraction_cannot_be_closed_and_a_finished_one_can()
    {
        var harness = new Harness();
        harness.Service.PauseBefore = 2;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var first = harness.Job;
        var saving = first.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.False(first.CloseCommand.CanExecute(null));
        var second = harness.NewJob();
        Assert.True(second.CloseCommand.CanExecute(null));
        await second.CloseCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal([first], harness.ViewModel.Jobs);
        Assert.Same(first, harness.ViewModel.SelectedJob);

        harness.Service.PauseAt.SetResult();
        await saving;
        Harness.Pump();
        await first.CloseCommand.ExecuteAsync(null);
        Harness.Pump();

        // Never an empty window: the last one closed makes a fresh one.
        var fresh = Assert.Single(harness.ViewModel.Jobs);
        Assert.NotSame(first, fresh);
        Assert.Equal("", fresh.NovelUrl);
    }

    [AvaloniaFact]
    public async Task One_extraction_failing_unexpectedly_leaves_the_others_running()
    {
        var harness = new Harness();
        harness.Service.PauseBefore = 3;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var healthy = harness.Job;
        var saving = healthy.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        var broken = harness.NewJob();
        harness.Services[1].CrashOnSave = true;
        harness.Services[1].Book = "Broken Book";
        await harness.ScannedAsync();
        await broken.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Contains("Something went wrong during the save", broken.Problem, StringComparison.Ordinal);
        Assert.Equal(Stage.Idle, broken.Stage);
        Assert.Equal(Stage.Saving, healthy.Stage);

        Assert.Equal(harness.Log.Path, broken.LogPath);
        var log = string.Join('\n', harness.LogRows().Where(row => row.Contains(",#2 Broken Book,", StringComparison.Ordinal)));
        Assert.Contains(",crash,", log, StringComparison.Ordinal);
        Assert.Contains("the fake session broke", log, StringComparison.Ordinal);
        Assert.Contains("stack:", log, StringComparison.Ordinal);

        harness.Service.PauseAt.SetResult();
        await saving;
        Harness.Pump();
        Assert.StartsWith("Done.", healthy.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Every_step_is_written_to_the_extractions_csv_log()
    {
        var harness = new Harness();
        harness.Service.Failing.Add(13);
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 12;
        harness.Job.To = 14;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal(harness.Log.Path, harness.Job.LogPath);
        var rows = harness.LogRows();
        Assert.Equal("time,level,job,event,chapter,url,detail", rows[0].TrimStart('\uFEFF'));
        Assert.All(rows.Skip(1), row => Assert.Contains(",#1 ", row, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",started,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",settings,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",scan,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",save,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",loaded,12,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",saved,12,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",error,#1 The Lighthouse,failed,13,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",finished,", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task No_log_is_written_when_logs_are_turned_off()
    {
        var harness = new Harness();
        harness.ViewModel.KeepLog = false;
        await harness.StartAsync();
        await harness.ScannedAsync();
        await harness.Job.SaveCommand.ExecuteAsync(null);

        Assert.Null(harness.Job.LogPath);
        Assert.Single(harness.LogRows());
    }

    [AvaloniaFact]
    public async Task Every_extraction_writes_into_one_common_log_each_under_its_own_name()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        await harness.Job.SaveCommand.ExecuteAsync(null);
        var second = harness.NewJob();
        harness.Services[1].Book = "The Orchard";
        second.NovelUrl = "https://other.example/the-orchard";
        await second.ScanCommand.ExecuteAsync(null);
        second.From = 1;
        second.To = 3;
        await second.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        Assert.Equal([harness.Log.Path], Directory.GetFiles(harness.LogFolder));
        var rows = harness.LogRows();
        Assert.Contains(rows, row => row.Contains(",#1 The Lighthouse,saved,", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(",#2 The Orchard,saved,", StringComparison.Ordinal));

        // Before its scan an extraction is known by its site.
        Assert.Contains(rows, row => row.Contains(",#2 other.example,scan,", StringComparison.Ordinal));
    }

    [Fact]
    public void Separate_logs_from_older_builds_are_folded_into_the_one_log()
    {
        var folder = Harness.Scratch();
        File.WriteAllLines(Path.Combine(folder, "log 2026-09-25 19-25-14 extraction 1 novel.example.csv"),
            [TocExtractor.App.Pipeline.CsvLog.Header, "2026-09-25 19:25:14.000,info,#1,scan,,,old row one"]);
        File.WriteAllLines(Path.Combine(folder, "app.csv"),
            [TocExtractor.App.Pipeline.CsvLog.Header, "2026-09-25 19:25:10.000,info,app,start,,,old app row"]);
        var path = Path.Combine(folder, Services.AppLog.FileName);

        using (var log = new TocExtractor.App.Pipeline.CsvLog(path, "app", append: true))
        {
            Services.AppLog.FoldIn(folder, log);
        }

        Assert.Equal([path], Directory.GetFiles(folder));
        var text = File.ReadAllText(path);
        Assert.Contains("old row one", text, StringComparison.Ordinal);
        Assert.Contains("old app row", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Settings_is_a_whole_screen_with_the_log_folder_and_the_appearance()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var harness = new Harness();
        await harness.StartAsync();

        Assert.True(Visible(harness, "SettingsButton"));
        Assert.False(Visible(harness, "SettingsPanel"));
        harness.ViewModel.OpenSettingsCommand.Execute(null);
        Harness.Pump();

        var panel = harness.Window.FindControl<Control>("SettingsPanel")!;
        Assert.True(Visible(harness, "SettingsPanel"));
        Assert.Equal(harness.Window.Bounds.Width, panel.Bounds.Width, 1);
        Assert.Equal(harness.Window.Bounds.Height, panel.Bounds.Height, 1);
        Assert.True(Visible(harness, "OpenLogFolderButton"));
        Assert.True(Visible(harness, "ThemeBox"));
        Assert.True(Visible(harness, "KeepLogBox"));
        SaveShot(harness.Window, "settings");

        await harness.ViewModel.OpenLogFolderCommand.ExecuteAsync(null);
        Assert.Equal(harness.LogFolder, Assert.Single(harness.Shell.Opened));

        harness.ViewModel.ThemeIndex = 2;
        Assert.Equal(AppTheme.Dark, harness.ViewModel.Theme);
        Assert.Equal(AppTheme.Dark, harness.Themes[^1]);

        harness.ViewModel.CloseSettingsCommand.Execute(null);
        Harness.Pump();
        Assert.False(Visible(harness, "SettingsPanel"));
    }

    [AvaloniaFact]
    public async Task Stopping_keeps_the_chapters_saved_so_far_and_says_so()
    {
        var harness = new Harness();
        harness.Service.PauseBefore = 4;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 10;
        var saving = harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        harness.Job.StopCommand.Execute(null);
        await saving;
        Harness.Pump();

        Assert.Equal(Stage.Idle, harness.Job.Stage);
        Assert.StartsWith("Stopped. 3 of 10 saved", harness.Job.Status, StringComparison.Ordinal);
        Assert.Equal("Stopped", harness.Job.RailStatus);
    }

    [AvaloniaFact]
    public async Task Copy_text_follows_the_audiobook_settings()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 2;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();
        harness.Job.SelectedChapter = harness.Job.Chapters[0];

        await harness.Job.CopyChapterCommand.ExecuteAsync(null);
        Assert.StartsWith("Chapter 1: The Lighthouse Keeper\n\nThe lamp", harness.Shell.Copied, StringComparison.Ordinal);

        harness.ViewModel.LeaveOutHeadings = true;
        await harness.Job.CopyChapterCommand.ExecuteAsync(null);
        Assert.StartsWith("The lamp", harness.Shell.Copied, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_site_needing_the_person_is_hard_to_miss_and_they_are_reminded()
    {
        var harness = new Harness();
        harness.ViewModel.ReminderEvery = TimeSpan.FromMilliseconds(50);
        harness.Service.PauseBefore = 3;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var saving = harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        // Looking at another extraction entirely.
        harness.NewJob();
        Assert.True(Visible(harness, "AttentionBar"));
        Assert.Contains("needs you", harness.ViewModel.Attention, StringComparison.Ordinal);
        Assert.Single(harness.Shell.Notified);

        await harness.ViewModel.ShowCheckCommand.ExecuteAsync(null);
        Assert.Equal(1, harness.Service.ShownChecks);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Harness.Pump();
        Assert.True(harness.Shell.Notified.Count > 1, "reminded while still waiting");

        harness.Service.PauseAt.SetResult();
        await saving;
        Harness.Pump();
        Assert.False(harness.ViewModel.NeedsAttention);
        var after = harness.Shell.Notified.Count;
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Harness.Pump();
        Assert.Equal(after, harness.Shell.Notified.Count);
    }

    [AvaloniaFact]
    public async Task A_chapter_far_shorter_than_the_rest_is_named_so_it_gets_a_look()
    {
        var harness = new Harness();
        await harness.StartAsync();
        await harness.ScannedAsync();
        harness.Job.From = 1;
        harness.Job.To = 5;
        await harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();
        var rows = harness.Job.Chapters;
        foreach (var row in rows)
        {
            row.Words = 2000;
        }

        rows[2].Words = 90;

        Assert.Equal([3], harness.Job.FlagShortChapters());
        Assert.Equal("Saved, only 90 words: check it", rows[2].StatusText);
        Assert.Equal("Saved, 2,000 words", rows[0].StatusText);
    }

    [AvaloniaFact]
    public async Task Going_slower_on_purpose_is_said_so_it_never_looks_stuck()
    {
        var harness = new Harness();
        harness.Service.PauseBefore = 3;
        harness.Service.PauseAt = new TaskCompletionSource();
        await harness.StartAsync();
        await harness.ScannedAsync();
        var saving = harness.Job.SaveCommand.ExecuteAsync(null);
        Harness.Pump();

        harness.Service.Slow("Going slower on purpose: 6s between pages, because this site asked to check you're a person.");
        Harness.Pump();

        Assert.True(Visible(harness, "PaceNoteText"));

        // A person being needed says so first; once not, the list says why it is slow.
        Assert.Equal("Needs you in the browser", harness.Job.RailStatus);
        harness.Job.PersonMessage = null;
        Assert.EndsWith("slower on purpose", harness.Job.RailStatus, StringComparison.Ordinal);

        harness.Service.PauseAt.SetResult();
        await saving;
        Harness.Pump();
        Assert.False(Visible(harness, "PaceNoteText"));
    }

    private static bool Visible(Harness harness, string name) =>
        harness.Window.FindControl<Control>(name) is { } control
        && control.IsVisible
        && control.GetVisualAncestors().OfType<Visual>().All(ancestor => ancestor.IsVisible);

    private static void SaveShot(Window window, string name)
    {
        // A readable folder in the picture, not the test's temporary one.
        if (window.DataContext is MainViewModel model)
        {
            foreach (var job in model.Jobs)
            {
                job.OutputDirectory = "~/Downloads/Novels";
            }
        }

        Harness.Pump();
        var directory = Environment.GetEnvironmentVariable("TOC_SCREENSHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
