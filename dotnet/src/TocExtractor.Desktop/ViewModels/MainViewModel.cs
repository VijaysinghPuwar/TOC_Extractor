using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TocExtractor.App;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Profiles;
using TocExtractor.App.Session;
using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Models;
using TocExtractor.Desktop.Services;

namespace TocExtractor.Desktop.ViewModels;

/// <summary>Which tab of the right-hand side is showing.</summary>
public enum Tab
{
    Preview,
    Chapters,
    Reader,
    Activity,
}

/// <summary>
/// Everything the window shows and does. The window itself only binds to this.
/// </summary>
/// <remarks>
/// All state changes happen on the UI thread. The extraction pipeline reports
/// from thread-pool threads, so its observer goes through <c>post</c>, which is
/// the dispatcher in the app and a direct call in tests.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ExtractionSession session;
    private readonly IShell shell;
    private readonly BrowserSetup browser;
    private readonly SettingsStore store;
    private readonly Action<Action> post;
    private readonly Dictionary<string, ChapterRow> rowsByUrl = new(StringComparer.Ordinal);
    private CancellationTokenSource? running;

    public MainViewModel(
        SessionEnvironment? environment,
        IShell shell,
        BrowserSetup browser,
        SettingsStore store,
        Action<Action> post)
    {
        this.shell = shell;
        this.browser = browser;
        this.store = store;
        this.post = post;
        this.session = new ExtractionSession(environment ?? SessionEnvironment.Default(this.Warn));
        this.session.PhaseChanged += (_, _) => this.post(this.PhaseMoved);
        this.Apply(store.Load());
    }

    // -- the form -----------------------------------------------------------

    [ObservableProperty]
    public partial string TocUrl { get; set; } = "";

    [ObservableProperty]
    public partial string LinkSelector { get; set; } = "";

    [ObservableProperty]
    public partial string TitleSelector { get; set; } = "";

    [ObservableProperty]
    public partial string ContentSelector { get; set; } = "";

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = AppPaths.DefaultOutputDirectory;

    [ObservableProperty]
    public partial bool FormatText { get; set; } = true;

    [ObservableProperty]
    public partial bool FormatMarkdown { get; set; }

    [ObservableProperty]
    public partial bool FormatJsonl { get; set; }

    [ObservableProperty]
    public partial decimal? MaxChapters { get; set; } = 20;

    [ObservableProperty]
    public partial decimal? Concurrency { get; set; } = 3;

    [ObservableProperty]
    public partial decimal? MinDelay { get; set; } = 1.2m;

    [ObservableProperty]
    public partial decimal? MaxDelay { get; set; } = 2.5m;

    [ObservableProperty]
    public partial bool IncludeLinks { get; set; }

    [ObservableProperty]
    public partial bool StripAds { get; set; } = true;

    [ObservableProperty]
    public partial bool StartOver { get; set; }

    // -- status ---------------------------------------------------------------

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>The last thing that went wrong, in words a person can act on. Null when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => this.Problem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreview), nameof(ShowChapters), nameof(ShowReader), nameof(ShowActivity))]
    public partial Tab CurrentTab { get; set; } = Tab.Preview;

    public bool ShowPreview => this.CurrentTab == Tab.Preview;

    public bool ShowChapters => this.CurrentTab == Tab.Chapters;

    public bool ShowReader => this.CurrentTab == Tab.Reader;

    public bool ShowActivity => this.CurrentTab == Tab.Activity;

    [ObservableProperty]
    public partial bool BrowserReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallPercentText), nameof(InstallIndeterminate), nameof(ShowInstallPercent))]
    public partial double InstallPercent { get; set; }

    public string InstallPercentText => string.Create(CultureInfo.InvariantCulture, $"{this.InstallPercent:0}%");

    /// <summary>Animate until the first real percentage arrives, then show it.</summary>
    public bool InstallIndeterminate => this.Installing && this.InstallPercent <= 0;

    public bool ShowInstallPercent => this.Installing && this.InstallPercent > 0;

    [ObservableProperty]
    public partial string InstallMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallIndeterminate), nameof(ShowInstallPercent))]
    public partial bool Installing { get; set; }

    [ObservableProperty]
    public partial bool InstallFailed { get; set; }

    public SessionPhase Phase => this.session.Phase;

    /// <summary>Which of the four steps is current, for the step strip: 1 to 4.</summary>
    public int Step => this.Phase switch
    {
        SessionPhase.Idle or SessionPhase.Launching => 1,
        SessionPhase.SigningIn => 2,
        _ when this.Chapters.Count > 0 && this.Chapters.Any(row => row.State != ChapterState.Waiting) => 4,
        _ => 3,
    };

    public bool IsStep1 => this.Step == 1;

    public bool IsStep2 => this.Step == 2;

    public bool IsStep3 => this.Step == 3;

    public bool IsStep4 => this.Step == 4;

    public bool Step1Done => this.Step > 1;

    public bool Step2Done => this.Step > 2;

    public bool Step3Done => this.Step > 3;

    public bool IsBusy => this.Phase is SessionPhase.Launching or SessionPhase.Previewing
        or SessionPhase.Extracting or SessionPhase.Stopping;

    public bool FormLocked => this.Phase is not SessionPhase.Idle;

    public bool OptionsLocked => this.IsBusy;

    // -- preview --------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(PreviewHeadline), nameof(RobotsLine), nameof(SkippedLine), nameof(SampleText))]
    public partial SelectorPreview? Preview { get; set; }

    public bool HasPreview => this.Preview is not null;

    public string PreviewHeadline => this.Preview is not { } preview
        ? ""
        : preview.ChapterCount switch
        {
            0 => "No chapters found",
            1 => "1 chapter found",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n:N0} chapters found"),
        };

    public string SkippedLine
    {
        get
        {
            if (this.Preview is not { } preview)
            {
                return "";
            }

            List<string> parts = [.. preview.Skipped
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Value} {Describe(entry.Key)}"))];
            if (preview.BeyondMax > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{preview.BeyondMax} beyond your limit of {this.MaxChapters:0}"));
            }

            return parts.Count == 0 ? "" : "Skipped: " + string.Join(", ", parts);
        }
    }

    public string RobotsLine
    {
        get
        {
            if (this.Preview?.Robots is not { } robots)
            {
                return "";
            }

            if (!robots.Found)
            {
                return "None found. No rules apply.";
            }

            var rules = robots.RuleCount == 1 ? "1 rule" : $"{robots.RuleCount} rules";
            var delay = robots.CrawlDelay is { } wait
                ? string.Create(CultureInfo.InvariantCulture, $", {wait.TotalSeconds:0.#}s delay")
                : "";
            var toc = robots.ContentsPageAllowed ? "" : ", contents page disallowed";
            return $"{rules}{delay}{toc}";
        }
    }

    // -- chapters and reader ----------------------------------------------------

    public ObservableCollection<ChapterRow> Chapters { get; } = [];

    public ObservableCollection<string> Activity { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReaderTitle), nameof(ReaderText), nameof(HasReaderChapter))]
    public partial ChapterRow? SelectedChapter { get; set; }

    public bool HasReaderChapter => this.SelectedChapter?.IsReadable == true;

    public string ReaderTitle => this.SelectedChapter?.Heading ?? "";

    public string ReaderText => Paragraphs(this.SelectedChapter?.Text);

    public string SampleText => Paragraphs(this.Preview?.SampleExcerpt);

    public IEnumerable<ChapterRow> ReadableChapters => this.Chapters.Where(row => row.IsReadable);

    public int SavedCount => this.Chapters.Count(row => row.State is ChapterState.Saved or ChapterState.AlreadySaved);

    public int FailedCount => this.Chapters.Count(row => row.State == ChapterState.Failed);

    public double ProgressPercent => this.Chapters.Count == 0
        ? 0
        : 100.0 * (this.SavedCount + this.FailedCount) / this.Chapters.Count;

    public string ProgressLine => this.Chapters.Count == 0
        ? "No chapters yet."
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{this.SavedCount} of {this.Chapters.Count} saved") + (this.FailedCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $", {this.FailedCount} failed")
                : "");

    [ObservableProperty]
    public partial bool HasOutput { get; set; }

    // -- lifecycle --------------------------------------------------------------

    /// <summary>Make sure the browser is downloaded. Runs once when the window opens.</summary>
    public async Task StartAsync()
    {
        try
        {
            if (await this.browser.IsInstalledAsync(CancellationToken.None).ConfigureAwait(true))
            {
                this.BrowserReady = true;
                this.Status = "Enter a contents page, then open the browser.";
                this.RefreshCommands();
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            this.Log("Could not check for the browser: " + exception.Message);
        }

        await this.InstallBrowserAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallBrowserAsync()
    {
        this.Installing = true;
        this.InstallFailed = false;
        this.InstallPercent = 0;
        this.InstallMessage = "One time, about 150 MB.";
        this.Status = "";
        try
        {
            var progress = new Progress<InstallProgress>(step => this.post(() =>
            {
                this.InstallMessage = step.Message;
                if (step.Percent is { } percent)
                {
                    this.InstallPercent = percent;
                }
            }));
            await this.browser.InstallAsync(progress, CancellationToken.None).ConfigureAwait(true);
            this.BrowserReady = true;
            this.Status = "Enter a contents page, then open the browser.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            this.InstallFailed = true;
            this.InstallMessage = exception.Message;
            this.Status = "Download failed.";
            this.Log(exception.Message);
        }
        finally
        {
            this.Installing = false;
            this.RefreshCommands();
        }
    }

    /// <summary>Save the form and close the browser. Called when the app quits.</summary>
    public void Shutdown()
    {
        this.store.Save(this.Snapshot());
        this.running?.Cancel();
        _ = this.session.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        this.running?.Cancel();
        await this.session.DisposeAsync().ConfigureAwait(true);
    }

    // -- the four steps -----------------------------------------------------------

    private bool CanLaunch => this.BrowserReady && this.Phase == SessionPhase.Idle;

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        this.Problem = null;
        this.Preview = null;
        this.Status = "Opening...";
        var settings = this.Snapshot();
        this.store.Save(settings);
        try
        {
            await this.session.LaunchAsync(settings).ConfigureAwait(true);
            this.Status = "Sign in if the site needs it, then press I'm ready.";
            this.Log("Browser opened on " + settings.TocUrl);
        }
        catch (SessionException exception)
        {
            this.Fail(exception.Message);
        }
    }

    private bool CanConfirm => this.Phase == SessionPhase.SigningIn;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        try
        {
            var signedIn = await this.session.ConfirmAsync().ConfigureAwait(true);
            this.Log(signedIn ? "Signed in." : "Not signed in. robots.txt applies in full.");
            this.Status = "Test your selectors.";
        }
        catch (SessionException exception)
        {
            this.Fail(exception.Message);
        }
    }

    private bool CanUseBrowser => this.Phase == SessionPhase.Ready;

    [RelayCommand(CanExecute = nameof(CanUseBrowser))]
    private async Task TestSelectorsAsync()
    {
        this.Problem = null;
        this.Status = "Testing...";
        this.CurrentTab = Tab.Preview;
        using var cancel = this.Begin();
        try
        {
            this.Preview = await this.session.PreviewAsync(this.Snapshot(), cancel.Token).ConfigureAwait(true);
            this.Status = this.Preview.SampleProblem is null && this.Preview.ChapterCount > 0
                ? "Looks good. Press Start."
                : "Adjust the selectors and test again.";
            this.Log($"Test: {this.PreviewHeadline}. {this.Preview.SampleProblem}".TrimEnd(' ', '.'));
        }
        catch (SessionException exception)
        {
            this.Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            this.Status = "Stopped.";
        }
        finally
        {
            this.running = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseBrowser))]
    private async Task ExtractAsync()
    {
        this.Problem = null;
        this.Status = "Starting...";
        this.ResetChapters([]);
        this.CurrentTab = Tab.Chapters;
        var settings = this.Snapshot();
        this.store.Save(settings);
        using var cancel = this.Begin();
        try
        {
            var result = await this.session.ExtractAsync(settings, new Observer(this), cancel.Token).ConfigureAwait(true);
            this.HasOutput = Directory.Exists(settings.OutputDirectory);
            this.Status = result.Outcome switch
            {
                PipelineOutcome.Ok => $"Done. {this.ProgressLine}.",
                PipelineOutcome.Refused => "This folder has progress from a different book. Use another folder, or Start over in Options.",
                _ when result.Run is null => "Could not read the contents page.",
                _ => $"{this.ProgressLine}. Press Start to retry the failed ones.",
            };
            if (result.Outcome == PipelineOutcome.Refused)
            {
                this.Problem = this.Status;
            }
        }
        catch (SessionException exception)
        {
            this.Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            this.HasOutput = Directory.Exists(settings.OutputDirectory);
            this.Status = $"Stopped. {this.ProgressLine}. Start again to resume.";
        }
        finally
        {
            this.running = null;
            this.RefreshProgress();
        }
    }

    private bool CanStop => this.Phase is SessionPhase.Extracting or SessionPhase.Previewing;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        this.session.BeginStop();
        this.running?.Cancel();
        this.Status = "Stopping...";
    }

    private bool CanCloseBrowser => this.Phase is SessionPhase.SigningIn or SessionPhase.Ready;

    [RelayCommand(CanExecute = nameof(CanCloseBrowser))]
    private async Task CloseBrowserAsync()
    {
        await this.session.CloseAsync().ConfigureAwait(true);
        this.Status = "Browser closed.";
    }

    // -- files and profiles -------------------------------------------------------

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await this.shell.PickFolderAsync(this.OutputDirectory).ConfigureAwait(true) is { } folder)
        {
            this.OutputDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync() =>
        await this.shell.OpenFolderAsync(this.OutputDirectory).ConfigureAwait(true);

    [RelayCommand]
    private async Task LoadProfileAsync()
    {
        if (await this.shell.PickProfileToOpenAsync(AppPaths.Profiles).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        try
        {
            this.Apply(this.Snapshot().With(ProfileLoader.Load(path)));
            this.Problem = null;
            this.Status = $"Loaded {Path.GetFileName(path)}.";
            this.Log("Loaded profile " + path);
        }
        catch (ProfileException exception)
        {
            this.Fail(exception.Message);
        }
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        var suggested = SuggestedProfileName(this.TocUrl);
        if (await this.shell.PickProfileToSaveAsync(AppPaths.Profiles, suggested).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        try
        {
            ProfileWriter.Save(this.Snapshot().ToProfile(path), path);
            this.Status = $"Saved {Path.GetFileName(path)}.";
            this.Log("Saved profile " + path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            this.Fail("Could not save the profile: " + exception.Message);
        }
    }

    [RelayCommand]
    private async Task CopyChapterAsync()
    {
        if (this.SelectedChapter?.Text is { } text)
        {
            await this.shell.CopyTextAsync(text).ConfigureAwait(true);
            this.Status = "Copied.";
        }
    }

    [RelayCommand]
    private void ShowPanel(Tab tab) => this.CurrentTab = tab;

    [RelayCommand]
    private void DismissProblem() => this.Problem = null;

    // -- helpers ------------------------------------------------------------------

    internal SessionSettings Snapshot()
    {
        List<string> formats = [];
        if (this.FormatText)
        {
            formats.Add("text");
        }

        if (this.FormatMarkdown)
        {
            formats.Add("markdown");
        }

        if (this.FormatJsonl)
        {
            formats.Add("jsonl");
        }

        return new SessionSettings
        {
            TocUrl = this.TocUrl.Trim(),
            LinkSelector = this.LinkSelector.Trim(),
            TitleSelector = this.TitleSelector.Trim(),
            ContentSelector = this.ContentSelector.Trim(),
            OutputDirectory = this.OutputDirectory.Trim(),
            Formats = formats,
            MaxChapters = (int)(this.MaxChapters ?? 20),
            Concurrency = (int)(this.Concurrency ?? 3),
            MinDelaySeconds = (double)(this.MinDelay ?? 1.2m),
            MaxDelaySeconds = (double)(this.MaxDelay ?? 2.5m),
            IncludeLinks = this.IncludeLinks,
            StripAds = this.StripAds,
            Force = this.StartOver,
        };
    }

    internal void Apply(SessionSettings settings)
    {
        this.TocUrl = settings.TocUrl;
        this.LinkSelector = settings.LinkSelector;
        this.TitleSelector = settings.TitleSelector;
        this.ContentSelector = settings.ContentSelector;
        this.OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? AppPaths.DefaultOutputDirectory
            : settings.OutputDirectory;
        this.FormatText = settings.Formats.Contains("text", StringComparer.Ordinal);
        this.FormatMarkdown = settings.Formats.Contains("markdown", StringComparer.Ordinal);
        this.FormatJsonl = settings.Formats.Contains("jsonl", StringComparer.Ordinal);
        this.MaxChapters = settings.MaxChapters;
        this.Concurrency = settings.Concurrency;
        this.MinDelay = (decimal)settings.MinDelaySeconds;
        this.MaxDelay = (decimal)settings.MaxDelaySeconds;
        this.IncludeLinks = settings.IncludeLinks;
        this.StripAds = settings.StripAds;
        this.StartOver = settings.Force;
    }

    /// <summary>A blank line between paragraphs, which chapter text keeps to one newline.</summary>
    internal static string Paragraphs(string? text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : string.Join(
                "\n\n",
                text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    internal static string SuggestedProfileName(string tocUrl) =>
        Uri.TryCreate(tocUrl.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase) + ".toml"
            : "my-site.toml";

    private static string Describe(string reason) => reason switch
    {
        "duplicate" => "repeated",
        "robots_disallowed" => "blocked by robots.txt",
        "scheme_not_allowed" => "not web pages",
        "private_address" => "private addresses",
        "empty" => "empty",
        _ => reason.Replace('_', ' '),
    };

    private CancellationTokenSource Begin()
    {
        this.running = new CancellationTokenSource();
        return this.running;
    }

    private void Fail(string message)
    {
        this.Problem = message;
        this.Status = "";
        this.Log(message);
    }

    private void Warn(string message) => this.post(() => this.Log(message));

    private void Log(string line)
    {
        this.Activity.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        if (this.Activity.Count > 2000)
        {
            this.Activity.RemoveAt(0);
        }
    }

    private void PhaseMoved()
    {
        this.OnPropertyChanged(nameof(this.Phase));
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.OnPropertyChanged(nameof(this.FormLocked));
        this.OnPropertyChanged(nameof(this.OptionsLocked));
        this.RefreshSteps();
        this.RefreshCommands();
    }

    private void RefreshSteps()
    {
        this.OnPropertyChanged(nameof(this.Step));
        this.OnPropertyChanged(nameof(this.IsStep1));
        this.OnPropertyChanged(nameof(this.IsStep2));
        this.OnPropertyChanged(nameof(this.IsStep3));
        this.OnPropertyChanged(nameof(this.IsStep4));
        this.OnPropertyChanged(nameof(this.Step1Done));
        this.OnPropertyChanged(nameof(this.Step2Done));
        this.OnPropertyChanged(nameof(this.Step3Done));
    }

    private void RefreshCommands()
    {
        this.LaunchCommand.NotifyCanExecuteChanged();
        this.ConfirmCommand.NotifyCanExecuteChanged();
        this.TestSelectorsCommand.NotifyCanExecuteChanged();
        this.ExtractCommand.NotifyCanExecuteChanged();
        this.StopCommand.NotifyCanExecuteChanged();
        this.CloseBrowserCommand.NotifyCanExecuteChanged();
    }

    private void RefreshProgress()
    {
        this.OnPropertyChanged(nameof(this.SavedCount));
        this.OnPropertyChanged(nameof(this.FailedCount));
        this.OnPropertyChanged(nameof(this.ProgressPercent));
        this.OnPropertyChanged(nameof(this.ProgressLine));
        this.OnPropertyChanged(nameof(this.ReadableChapters));
        this.RefreshSteps();
    }

    private void ResetChapters(IReadOnlyList<string> links)
    {
        this.SelectedChapter = null;
        this.Chapters.Clear();
        this.rowsByUrl.Clear();
        for (var i = 0; i < links.Count; i++)
        {
            var row = new ChapterRow(i + 1, links[i]);
            this.Chapters.Add(row);
            this.rowsByUrl[links[i]] = row;
        }

        this.RefreshProgress();
    }

    partial void OnSelectedChapterChanged(ChapterRow? value)
    {
        if (value?.IsReadable == true)
        {
            this.CurrentTab = Tab.Reader;
        }
    }

    partial void OnMaxChaptersChanged(decimal? value) => this.OnPropertyChanged(nameof(this.SkippedLine));

    /// <summary>Carries pipeline reports onto the UI thread.</summary>
    private sealed class Observer(MainViewModel owner) : IPipelineObserver
    {
        public void Log(string line) => owner.post(() => owner.Log(line));

        public void Collected(CollectedLinks collected)
        {
            var links = collected.Collection.Kept;
            owner.post(() =>
            {
                owner.ResetChapters(links);
                owner.Status = "Saving...";
            });
        }

        public void Resuming(ResumePlan plan, IReadOnlyList<string> alreadyDone) => owner.post(() =>
        {
            foreach (var url in alreadyDone)
            {
                if (owner.rowsByUrl.TryGetValue(url, out var row))
                {
                    row.State = ChapterState.AlreadySaved;
                }
            }

            owner.RefreshProgress();
        });

        public void Record(ChapterRecord record) => owner.post(() =>
        {
            if (owner.rowsByUrl.TryGetValue(record.RequestedUrl, out var row))
            {
                row.Title = record.Title;
                row.Text = record.Text;
                row.Words = record.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                row.State = ChapterState.Saved;
                owner.RefreshProgress();
                owner.Status = owner.ProgressLine;
            }
        });

        public void Failure(FailedChapter failure) => owner.post(() =>
        {
            if (owner.rowsByUrl.TryGetValue(failure.Url, out var row))
            {
                row.Detail = failure.Detail;
                row.State = ChapterState.Failed;
                owner.RefreshProgress();
            }
        });
    }
}
