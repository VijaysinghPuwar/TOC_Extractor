using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TocExtractor.App;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Scanning;
using TocExtractor.App.Session;
using TocExtractor.Core.Models;
using TocExtractor.Desktop.Services;

namespace TocExtractor.Desktop.ViewModels;

/// <summary>Which tab of the right-hand side is showing.</summary>
public enum Tab
{
    Chapters,
    Reader,
    Activity,
}

/// <summary>Where the flow is.</summary>
public enum Stage
{
    Idle,
    Scanning,
    SigningIn,
    Saving,
    Stopping,
}

/// <summary>
/// Everything the window shows and does: paste a novel's page, scan it,
/// choose a range, save it as TXT or PDF. The window only binds to this.
/// </summary>
/// <remarks>
/// All state changes happen on the UI thread. The session reports from
/// thread-pool threads, so those reports go through <c>post</c>, which is the
/// dispatcher in the app and a direct call in tests.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly INovelService session;
    private readonly IShell shell;
    private readonly BrowserSetup browser;
    private readonly SettingsStore store;
    private readonly Action<Action> post;
    private readonly Dictionary<int, ChapterRow> rowsByNumber = [];
    private CancellationTokenSource? running;
    private RangePreview? preview;
    private bool slowConfirmed;

    public MainViewModel(
        INovelService? session,
        IShell shell,
        BrowserSetup browser,
        SettingsStore store,
        Action<Action> post)
    {
        this.shell = shell;
        this.browser = browser;
        this.store = store;
        this.post = post;
        this.session = session ?? new NovelSession(NovelEnvironment.Default(this.Warn));
        this.session.PersonNeeded += (_, message) => this.post(() => this.PersonMessage = message);
        this.Apply(store.Load());
    }

    // -- the form -----------------------------------------------------------

    [ObservableProperty]
    public partial string NovelUrl { get; set; } = "";

    [ObservableProperty]
    public partial decimal? From { get; set; }

    [ObservableProperty]
    public partial decimal? To { get; set; }

    [ObservableProperty]
    public partial bool Text { get; set; } = true;

    [ObservableProperty]
    public partial bool Pdf { get; set; }

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = AppPaths.DefaultOutputDirectory;

    [ObservableProperty]
    public partial bool KeepLog { get; set; } = true;

    [ObservableProperty]
    public partial decimal? AtOnce { get; set; } = 1;

    [ObservableProperty]
    public partial decimal? MinDelay { get; set; } = 2;

    [ObservableProperty]
    public partial decimal? MaxDelay { get; set; } = 4;

    [ObservableProperty]
    public partial bool IncludeLinks { get; set; }

    [ObservableProperty]
    public partial bool StripAds { get; set; } = true;

    [ObservableProperty]
    public partial bool StartOver { get; set; }

    // -- status ---------------------------------------------------------------

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => this.Problem is not null;

    /// <summary>What the person must do in the browser right now, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PersonNeeded))]
    public partial string? PersonMessage { get; set; }

    public bool PersonNeeded => this.PersonMessage is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(FormLocked), nameof(IsSigningIn), nameof(Step), nameof(IsStep1),
        nameof(IsStep2), nameof(IsStep3), nameof(Step1Done), nameof(Step2Done))]
    public partial Stage Stage { get; set; }

    public bool IsBusy => this.Stage is Stage.Scanning or Stage.Saving or Stage.Stopping;

    public bool FormLocked => this.Stage != Stage.Idle;

    public bool IsSigningIn => this.Stage == Stage.SigningIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChapters), nameof(ShowReader), nameof(ShowActivity))]
    public partial Tab CurrentTab { get; set; } = Tab.Chapters;

    public bool ShowChapters => this.CurrentTab == Tab.Chapters;

    public bool ShowReader => this.CurrentTab == Tab.Reader;

    public bool ShowActivity => this.CurrentTab == Tab.Activity;

    // -- first run ----------------------------------------------------------------

    [ObservableProperty]
    public partial bool BrowserReady { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallPercentText), nameof(InstallIndeterminate), nameof(ShowInstallPercent))]
    public partial double InstallPercent { get; set; }

    public string InstallPercentText => string.Create(CultureInfo.InvariantCulture, $"{this.InstallPercent:0}%");

    public bool InstallIndeterminate => this.Installing && this.InstallPercent <= 0;

    public bool ShowInstallPercent => this.Installing && this.InstallPercent > 0;

    [ObservableProperty]
    public partial string InstallMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallIndeterminate), nameof(ShowInstallPercent))]
    public partial bool Installing { get; set; }

    [ObservableProperty]
    public partial bool InstallFailed { get; set; }

    // -- the scan -----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScan), nameof(ScanReady), nameof(BookTitle), nameof(ScanSummary),
        nameof(FirstChapter), nameof(LastChapter), nameof(Step), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3),
        nameof(Step1Done), nameof(Step2Done), nameof(NeedsSignIn))]
    public partial ScanResult? Scan { get; set; }

    public bool HasScan => this.Scan is not null;

    public bool ScanReady => this.Scan?.Ready == true;

    public bool NeedsSignIn => this.Scan?.Obstacle == Obstacle.SignIn && !this.session.SignedIn;

    public string BookTitle => this.Scan?.BookTitle ?? "";

    public decimal FirstChapter => this.Scan?.FirstNumber ?? 1;

    public decimal LastChapter => Math.Max(this.Scan?.LastNumber ?? 1, 1);

    public string ScanSummary => this.Scan is not { } scan
        ? ""
        : scan.Chapters.Count == 0
            ? "No chapters found."
            : string.Create(CultureInfo.InvariantCulture, $"Chapters {scan.FirstNumber:N0} to {scan.LastNumber:N0}")
                + (scan.Chapters.Count < scan.ChapterSpan
                    ? string.Create(CultureInfo.InvariantCulture, $", {scan.Chapters.Count:N0} listed by the site")
                    : "")
                + (this.session.SignedIn ? ". Signed in." : ".");

    public ObservableCollection<string> ScanNotes { get; } = [];

    public bool HasNotes => this.ScanNotes.Count > 0;

    // -- the range ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial string PlanSummary { get; set; } = "";

    public bool HasPlan => this.PlanSummary.Length > 0;

    [ObservableProperty]
    public partial bool PlanIsSlow { get; set; }

    // -- steps --------------------------------------------------------------------

    public int Step => this.Stage == Stage.Saving || this.SavedCount > 0 ? 3 : this.ScanReady ? 2 : 1;

    public bool IsStep1 => this.Step == 1;

    public bool IsStep2 => this.Step == 2;

    public bool IsStep3 => this.Step == 3;

    public bool Step1Done => this.Step > 1;

    public bool Step2Done => this.Step > 2;

    // -- chapters and reader ------------------------------------------------------

    public ObservableCollection<ChapterRow> Chapters { get; } = [];

    public ObservableCollection<string> Activity { get; } = [];

    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReaderTitle), nameof(ReaderText), nameof(HasReaderChapter))]
    public partial ChapterRow? SelectedChapter { get; set; }

    public bool HasReaderChapter => this.SelectedChapter?.IsReadable == true;

    public string ReaderTitle => this.SelectedChapter?.Heading ?? "";

    public string ReaderText => Paragraphs(this.SelectedChapter?.Text);

    public int SavedCount => this.Chapters.Count(row => row.State is ChapterState.Saved or ChapterState.AlreadySaved);

    public int FailedCount => this.Chapters.Count(row => row.State == ChapterState.Failed);

    public double ProgressPercent => this.Chapters.Count == 0
        ? 0
        : 100.0 * (this.SavedCount + this.FailedCount) / this.Chapters.Count;

    public string ProgressLine => this.Chapters.Count == 0
        ? "Scan a novel, choose chapters, then save."
        : string.Create(CultureInfo.InvariantCulture, $"{this.SavedCount} of {this.Chapters.Count} saved")
            + (this.FailedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $", {this.FailedCount} failed") : "");

    [ObservableProperty]
    public partial bool HasOutput { get; set; }

    // -- lifecycle ----------------------------------------------------------------

    /// <summary>Make sure the browser is downloaded. Runs once when the window opens.</summary>
    public async Task StartAsync()
    {
        try
        {
            if (await this.browser.IsInstalledAsync(CancellationToken.None).ConfigureAwait(true))
            {
                this.BrowserReady = true;
                this.Status = "Paste a novel's page and press Scan.";
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
            this.Status = "Paste a novel's page and press Scan.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            this.InstallFailed = true;
            this.InstallMessage = exception.Message;
            this.Log(exception.Message);
        }
        finally
        {
            this.Installing = false;
            this.RefreshCommands();
        }
    }

    public void Shutdown()
    {
        this.store.Save(this.Remembered());
        this.running?.Cancel();
        _ = this.session.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        this.running?.Cancel();
        await this.session.DisposeAsync().ConfigureAwait(true);
    }

    // -- 1. scan ------------------------------------------------------------------

    private bool CanScan => this.BrowserReady && this.Stage == Stage.Idle && this.NovelUrl.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        this.Problem = null;
        this.Scan = null;
        this.ScanNotes.Clear();
        this.OnPropertyChanged(nameof(this.HasNotes));
        this.PlanSummary = "";
        this.ResetChapters([]);
        this.Stage = Stage.Scanning;
        this.Status = "Scanning. The browser window shows what is being read.";
        this.store.Save(this.Remembered());
        this.session.Pace = this.Pace();
        using var cancel = this.Begin();
        try
        {
            var scan = await this.session.ScanAsync(this.NovelUrl, line => this.post(() => this.Log(line)), cancel.Token)
                .ConfigureAwait(true);
            this.Scan = scan;
            foreach (var note in scan.Notes)
            {
                this.ScanNotes.Add(note);
            }

            this.OnPropertyChanged(nameof(this.HasNotes));

            this.Log((string.IsNullOrWhiteSpace(scan.BookTitle) ? "Scan:" : $"Scan: {scan.BookTitle.TrimEnd('.')}.") + $" {this.ScanSummary} {scan.Problem}".TrimEnd());
            if (scan.Ready)
            {
                this.From ??= scan.FirstNumber;
                this.To ??= scan.LastNumber;
                this.From = Math.Clamp(this.From.Value, scan.FirstNumber, scan.LastNumber);
                this.To = Math.Clamp(this.To.Value, scan.FirstNumber, scan.LastNumber);
                this.UpdatePlan();
                this.Status = "Choose the chapters, then press Save.";
            }
            else
            {
                this.Problem = scan.Problem;
                this.Status = scan.Obstacle == Obstacle.SignIn ? "Sign in to continue." : "The scan could not finish.";
            }
        }
        catch (SessionException exception)
        {
            this.Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            this.Status = "Scan stopped.";
        }
        finally
        {
            this.running = null;
            this.Stage = Stage.Idle;
            this.RefreshCommands();
        }
    }

    // -- signing in -----------------------------------------------------------------

    private bool CanSignIn => this.BrowserReady && this.Stage == Stage.Idle && this.NovelUrl.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        this.Problem = null;
        try
        {
            this.Stage = Stage.SigningIn;
            await this.session.BeginSignInAsync(this.NovelUrl).ConfigureAwait(true);
            this.Status = "Sign in to the site in the browser window, then press Done.";
        }
        catch (SessionException exception)
        {
            this.Stage = Stage.Idle;
            this.Fail(exception.Message);
        }

        this.RefreshCommands();
    }

    private bool CanFinishSignIn => this.Stage == Stage.SigningIn;

    [RelayCommand(CanExecute = nameof(CanFinishSignIn))]
    private async Task FinishSignInAsync()
    {
        var signedIn = await this.session.FinishSignInAsync().ConfigureAwait(true);
        this.Stage = Stage.Idle;
        this.OnPropertyChanged(nameof(this.NeedsSignIn));
        this.OnPropertyChanged(nameof(this.ScanSummary));
        if (signedIn)
        {
            this.Status = "Signed in. Scan again.";
            this.Log("Signed in to the site.");
        }
        else
        {
            this.Fail("No sign-in was found in the browser. If the site's Google sign-in refused this browser, try signing in with an email and password instead.");
        }

        this.RefreshCommands();
    }

    // -- 2. choose ------------------------------------------------------------------

    partial void OnFromChanged(decimal? value) => this.UpdatePlan();

    partial void OnToChanged(decimal? value) => this.UpdatePlan();

    [RelayCommand]
    private void WholeBook()
    {
        if (this.Scan is { Ready: true } scan)
        {
            this.From = scan.FirstNumber;
            this.To = scan.LastNumber;
        }
    }

    private void UpdatePlan()
    {
        if (this.Scan is not { Ready: true } scan || this.From is not { } from || this.To is not { } to)
        {
            this.preview = null;
            this.PlanSummary = "";
            this.PlanIsSlow = false;
            this.SaveCommand.NotifyCanExecuteChanged();
            return;
        }

        this.session.Pace = this.Pace();
        this.slowConfirmed = false;
        this.preview = this.session.Preview(scan, (int)from, (int)to);
        this.PlanSummary = this.preview.Summary;
        this.PlanIsSlow = this.preview.Slow;
        this.SaveCommand.NotifyCanExecuteChanged();
    }

    // -- 3. save --------------------------------------------------------------------

    private bool CanSave => this.Stage == Stage.Idle && this.Scan?.Ready == true && this.preview?.Plan.Problem is null
        && this.preview is not null && (this.Text || this.Pdf);

    partial void OnTextChanged(bool value) => this.SaveCommand.NotifyCanExecuteChanged();

    partial void OnPdfChanged(bool value) => this.SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (this.Scan is not { } scan || this.preview is not { } plan)
        {
            return;
        }

        // A long walk is asked about first: hundreds of extra pages take
        // time, and are what makes a site start asking for checks.
        if (plan.Slow && !this.slowConfirmed)
        {
            this.slowConfirmed = true;
            this.Problem = $"{plan.Summary} The site lists no closer chapter to start from. Press Save again to go ahead, or sign in to the site first so the app can find these chapters directly.";
            return;
        }

        this.Problem = null;
        this.Files.Clear();
        this.ResetChapters([.. Enumerable.Range(plan.From, plan.To - plan.From + 1)]);
        this.CurrentTab = Tab.Chapters;
        this.Stage = Stage.Saving;
        this.Status = "Saving...";
        this.store.Save(this.Remembered());
        this.session.Pace = this.Pace();
        using var cancel = this.Begin();
        try
        {
            var result = await this.session.SaveAsync(
                scan, plan, this.OutputDirectory, this.Text, this.Pdf, this.KeepLog, this.StartOver,
                new Observer(this), cancel.Token).ConfigureAwait(true);
            foreach (var file in result.Files)
            {
                this.Files.Add(Path.GetFileName(file));
            }

            // From the result, not the rows: the last reports can still be
            // on their way to the list when the run returns.
            var total = plan.To - plan.From + 1;
            var saved = total - result.Missing.Count;
            this.HasOutput = result.Files.Count > 0 || saved > 0;
            this.Status = result.Outcome switch
            {
                PipelineOutcome.Ok => string.Create(CultureInfo.InvariantCulture, $"Done. {saved} of {total} saved."),
                PipelineOutcome.Refused => "This folder holds another book's progress. Use Start over in Settings, or choose another folder.",
                _ => string.Create(CultureInfo.InvariantCulture,
                    $"{saved} of {total} saved, {result.Missing.Count} not. Press Save again to retry the rest."),
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
            this.HasOutput = this.SavedCount > 0;
            this.Status = $"Stopped. {this.ProgressLine}. Press Save to carry on.";
        }
        finally
        {
            this.running = null;
            this.PersonMessage = null;
            this.Stage = Stage.Idle;
            this.RefreshProgress();
            this.RefreshCommands();
        }
    }

    private bool CanStop => this.Stage is Stage.Scanning or Stage.Saving;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        this.Stage = Stage.Stopping;
        this.running?.Cancel();
        this.Status = "Stopping...";
    }

    // -- files ----------------------------------------------------------------------

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await this.shell.PickFolderAsync(this.OutputDirectory).ConfigureAwait(true) is { } folder)
        {
            this.OutputDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var folder = this.Scan is { } scan
            ? Path.Combine(this.OutputDirectory, BookFiles.FolderName(scan.BookTitle, scan.NovelUrl))
            : this.OutputDirectory;
        await this.shell.OpenFolderAsync(Directory.Exists(folder) ? folder : this.OutputDirectory).ConfigureAwait(true);
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
    private void ShowTab(Tab tab) => this.CurrentTab = tab;

    [RelayCommand]
    private void DismissProblem() => this.Problem = null;

    // -- helpers --------------------------------------------------------------------

    internal SessionSettings Pace() => new()
    {
        Concurrency = (int)(this.AtOnce ?? 1),
        MinDelaySeconds = (double)(this.MinDelay ?? 2),
        MaxDelaySeconds = (double)Math.Max(this.MaxDelay ?? 4, this.MinDelay ?? 2),
        IncludeLinks = this.IncludeLinks,
        StripAds = this.StripAds,
    };

    internal DesktopSettings Remembered() => new()
    {
        NovelUrl = this.NovelUrl.Trim(),
        OutputDirectory = this.OutputDirectory.Trim(),
        Text = this.Text,
        Pdf = this.Pdf,
        KeepLog = this.KeepLog,
        AtOnce = (int)(this.AtOnce ?? 1),
        MinDelay = (double)(this.MinDelay ?? 2),
        MaxDelay = (double)(this.MaxDelay ?? 4),
        IncludeLinks = this.IncludeLinks,
        StripAds = this.StripAds,
    };

    internal void Apply(DesktopSettings settings)
    {
        this.NovelUrl = settings.NovelUrl;
        this.OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? AppPaths.DefaultOutputDirectory : settings.OutputDirectory;
        this.Text = settings.Text;
        this.Pdf = settings.Pdf;
        this.KeepLog = settings.KeepLog;
        this.AtOnce = settings.AtOnce;
        this.MinDelay = (decimal)settings.MinDelay;
        this.MaxDelay = (decimal)settings.MaxDelay;
        this.IncludeLinks = settings.IncludeLinks;
        this.StripAds = settings.StripAds;
    }

    /// <summary>A blank line between paragraphs, which chapter text keeps to one newline.</summary>
    internal static string Paragraphs(string? text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : string.Join("\n\n", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    partial void OnStageChanged(Stage value) => this.RefreshCommands();

    partial void OnNovelUrlChanged(string value)
    {
        this.ScanCommand.NotifyCanExecuteChanged();
        this.SignInCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChapterChanged(ChapterRow? value)
    {
        if (value?.IsReadable == true)
        {
            this.CurrentTab = Tab.Reader;
        }
    }

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
        if (this.Activity.Count > 3000)
        {
            this.Activity.RemoveAt(0);
        }
    }

    private void RefreshCommands()
    {
        this.ScanCommand.NotifyCanExecuteChanged();
        this.SignInCommand.NotifyCanExecuteChanged();
        this.FinishSignInCommand.NotifyCanExecuteChanged();
        this.SaveCommand.NotifyCanExecuteChanged();
        this.StopCommand.NotifyCanExecuteChanged();
    }

    private void RefreshProgress()
    {
        this.OnPropertyChanged(nameof(this.SavedCount));
        this.OnPropertyChanged(nameof(this.FailedCount));
        this.OnPropertyChanged(nameof(this.ProgressPercent));
        this.OnPropertyChanged(nameof(this.ProgressLine));
        this.OnPropertyChanged(nameof(this.Step));
        this.OnPropertyChanged(nameof(this.IsStep1));
        this.OnPropertyChanged(nameof(this.IsStep2));
        this.OnPropertyChanged(nameof(this.IsStep3));
        this.OnPropertyChanged(nameof(this.Step1Done));
        this.OnPropertyChanged(nameof(this.Step2Done));
    }

    private void ResetChapters(IReadOnlyList<int> numbers)
    {
        this.SelectedChapter = null;
        this.Chapters.Clear();
        this.rowsByNumber.Clear();
        var titles = this.Scan?.Chapters.ToDictionary(c => c.Number, c => c.Title) ?? [];
        foreach (var number in numbers)
        {
            var row = new ChapterRow(number, "") { Title = titles.GetValueOrDefault(number) };
            this.Chapters.Add(row);
            this.rowsByNumber[number] = row;
        }

        this.RefreshProgress();
    }

    /// <summary>Carries pipeline reports onto the UI thread.</summary>
    private sealed class Observer(MainViewModel owner) : IPipelineObserver
    {
        public void Log(string line) => owner.post(() => owner.Log(line));

        public void Record(ChapterRecord record) => owner.post(() =>
        {
            if (owner.rowsByNumber.TryGetValue(record.Index, out var row))
            {
                row.Title = record.Title;
                row.Text = record.Text;
                row.Words = record.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                row.State = ChapterState.Saved;
                owner.RefreshProgress();

                // A report can arrive after the run has ended; the final
                // message must win.
                if (owner.Stage == Stage.Saving)
                {
                    owner.Status = owner.ProgressLine;
                }
            }
        });

        public void Failure(FailedChapter failure) => owner.post(() =>
        {
            if (owner.rowsByNumber.TryGetValue(failure.Index, out var row))
            {
                row.Detail = failure.Detail;
                row.State = ChapterState.Failed;
                owner.RefreshProgress();
            }
        });
    }
}
