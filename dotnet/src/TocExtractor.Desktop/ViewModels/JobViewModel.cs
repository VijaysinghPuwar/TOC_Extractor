using System.Collections.ObjectModel;
using System.ComponentModel;
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

/// <summary>Where an extraction is.</summary>
public enum Stage
{
    Idle,
    Scanning,
    SigningIn,
    Saving,
    Stopping,
}

/// <summary>
/// One extraction: paste a novel's page, scan it, choose a range, save it as
/// TXT or PDF. Any number run at once, each in its own entry on the left.
/// </summary>
/// <remarks>
/// All state changes happen on the UI thread. The session reports from
/// thread-pool threads, so those reports go through <c>post</c>, which is the
/// dispatcher in the app and a direct call in tests. Every step is also
/// written to this extraction's CSV log as it happens, so a crash or a
/// failure can be looked into afterwards.
/// </remarks>
public sealed partial class JobViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly HashSet<string> RailInputs =
    [
        nameof(Stage), nameof(Status), nameof(Problem), nameof(PersonMessage), nameof(Scan), nameof(NovelUrl),
        nameof(ProgressPercent), nameof(ProgressLine), nameof(SavedCount), nameof(HasOutput), nameof(PaceNote),
        nameof(PlanSummary),
    ];

    private readonly INovelService session;
    private readonly MainViewModel owner;
    private readonly Action<Action> post;
    private readonly Dictionary<int, ChapterRow> rowsByNumber = [];
    private CancellationTokenSource? running;
    private RangePreview? preview;
    private bool slowConfirmed;
    private CsvLog? log;
    private bool closed;
    private string? savedFolder;

    internal JobViewModel(int number, INovelService session, MainViewModel owner, Action<Action> post)
    {
        this.Number = number;
        this.session = session;
        this.owner = owner;
        this.post = post;
        this.session.PersonNeeded += this.OnPersonNeeded;
        this.session.PaceNote += this.OnPaceNote;
    }

    /// <summary>
    /// Why this extraction is going slower than usual, while it saves, so a
    /// long gap between chapters is never mistaken for the app hanging.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaceNote))]
    public partial string? PaceNote { get; set; }

    public bool HasPaceNote => this.PaceNote is not null && this.Stage == Stage.Saving;

    private void OnPaceNote(object? sender, string? note) => this.post(() =>
    {
        this.PaceNote = note;
        this.OnPropertyChanged(nameof(this.HasPaceNote));
    });

    /// <summary>This extraction's number in the list, from 1, never reused while the app is open.</summary>
    public int Number { get; }

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

    /// <summary>Scanning, saving or stopping: the extraction cannot be closed.</summary>
    public bool IsBusy => this.Stage is Stage.Scanning or Stage.Saving or Stage.Stopping;

    public bool FormLocked => this.Stage != Stage.Idle;

    public bool IsSigningIn => this.Stage == Stage.SigningIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChapters), nameof(ShowReader), nameof(ShowActivity))]
    public partial Tab CurrentTab { get; set; } = Tab.Chapters;

    public bool ShowChapters => this.CurrentTab == Tab.Chapters;

    public bool ShowReader => this.CurrentTab == Tab.Reader;

    public bool ShowActivity => this.CurrentTab == Tab.Activity;

    // -- the entry on the left ------------------------------------------------

    /// <summary>The book's title once scanned, else the site, else a placeholder.</summary>
    public string Caption =>
        !string.IsNullOrWhiteSpace(this.Scan?.BookTitle)
            ? FirstLine(this.Scan.BookTitle)
            : Uri.TryCreate(this.NovelUrl.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? uri.Host
                : string.Create(CultureInfo.InvariantCulture, $"New extraction {this.Number}");

    /// <summary>One short line on where this extraction is, for the list on the left.</summary>
    public string RailStatus => this.Stage switch
    {
        Stage.Scanning => "Scanning...",
        Stage.SigningIn => "Signing in",
        Stage.Stopping => "Stopping...",
        Stage.Saving when this.PersonNeeded => "Needs you in the browser",
        Stage.Saving when this.PaceNote is not null => string.Create(CultureInfo.InvariantCulture, $"Saving, {this.SavedCount} of {this.Chapters.Count}, slower on purpose"),
        Stage.Saving => string.Create(CultureInfo.InvariantCulture, $"Saving, {this.SavedCount} of {this.Chapters.Count}"),
        _ when this.Problem is not null => "Needs attention",
        _ when this.Status.StartsWith("Done.", StringComparison.Ordinal) => string.Create(
            CultureInfo.InvariantCulture, $"Done, {this.SavedCount} of {this.Chapters.Count}"),
        _ when this.Status.StartsWith("Stopped.", StringComparison.Ordinal) => "Stopped",
        _ when this.Chapters.Count > 0 => this.ProgressLine,

        // Not "Ready to save" when Save cannot be pressed: a range outside
        // the book said so here, and the extraction looked stuck.
        _ when this.ScanReady && this.preview?.Plan.Problem is { } problem => this.OutOfBook
            ? string.Create(CultureInfo.InvariantCulture, $"Only chapters {this.FirstChapter:0} to {this.LastChapter:0}")
            : problem,
        _ when this.ScanReady => "Ready to save",
        _ => "Not started",
    };

    /// <summary>The chosen chapters reach past the book's first or last one.</summary>
    private bool OutOfBook => this.From < this.FirstChapter || this.To > this.LastChapter;

    /// <summary>Whether the list shows a progress bar for this extraction.</summary>
    public bool HasProgress => this.Chapters.Count > 0;

    // -- the scan -----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScan), nameof(ScanReady), nameof(BookTitle), nameof(ScanSummary),
        nameof(FirstChapter), nameof(LastChapter), nameof(Step), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3),
        nameof(Step1Done), nameof(Step2Done), nameof(NeedsSignIn), nameof(Caption))]
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

    public string ReaderText => Paragraphs(this.SelectedChapter?.ReadText());

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

    /// <summary>The log file this extraction writes into, once it has started.</summary>
    public string? LogPath => this.log?.Path;

    /// <summary>What this extraction's rows are marked with in the log: its number and its book or site.</summary>
    internal string LogName => string.Create(CultureInfo.InvariantCulture, $"#{this.Number} {this.Caption}");

    // -- lifecycle ----------------------------------------------------------------

    /// <summary>Stop anything running and let go of the log. The shared browser stays open.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this.closed)
        {
            return;
        }

        this.closed = true;
        this.running?.Cancel();
        this.session.PersonNeeded -= this.OnPersonNeeded;
        this.session.PaceNote -= this.OnPaceNote;
        this.log?.Info("closed", "The extraction was closed.");
        await this.session.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Stop at once, for the app closing.</summary>
    internal void Cancel()
    {
        if (this.running is not null)
        {
            this.log?.Warning("app", "The app is closing; stopping this extraction.");
        }

        this.running?.Cancel();
    }

    // -- 1. scan ------------------------------------------------------------------

    private bool CanScan => this.owner.BrowserReady && this.Stage == Stage.Idle && this.NovelUrl.Trim().Length > 0;

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
        this.owner.Remember(this);
        this.session.Pace = this.owner.Pace();
        this.StartLog();
        this.log?.Info("scan", "Scanning " + this.NovelUrl.Trim(), url: this.NovelUrl.Trim());
        using var cancel = this.Begin();
        try
        {
            var scan = await this.session.ScanAsync(this.NovelUrl, line => this.post(() => this.Log(line)), cancel.Token)
                .ConfigureAwait(true);
            this.Scan = scan;
            foreach (var note in scan.Notes)
            {
                this.ScanNotes.Add(note);
                this.log?.Info("scan-note", note);
            }

            this.OnPropertyChanged(nameof(this.HasNotes));

            // From here on, rows carry the book's name rather than the site's.
            if (this.log is not null)
            {
                this.log.Job = this.LogName;
            }

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
                this.Fail(scan.Problem ?? "The scan could not finish.", keepStatus: true);
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
            this.log?.Warning("stopped", "The scan was stopped.");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            this.Crashed("scan", exception);
        }
        finally
        {
            this.running = null;
            this.Stage = Stage.Idle;
            this.RefreshCommands();
        }
    }

    // -- signing in -----------------------------------------------------------------

    private bool CanSignIn => this.owner.BrowserReady && this.Stage == Stage.Idle && this.NovelUrl.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        this.Problem = null;
        this.StartLog();
        try
        {
            this.Stage = Stage.SigningIn;
            this.log?.Info("sign-in", "Opened the site for the person to sign in.", url: this.NovelUrl.Trim());
            await this.session.BeginSignInAsync(this.NovelUrl).ConfigureAwait(true);
            this.Status = "Sign in to the site in the browser window, then press Done.";
        }
        catch (SessionException exception)
        {
            this.Stage = Stage.Idle;
            this.Fail(exception.Message);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            this.Stage = Stage.Idle;
            this.Crashed("sign-in", exception);
        }

        this.RefreshCommands();
    }

    private bool CanFinishSignIn => this.Stage == Stage.SigningIn;

    [RelayCommand(CanExecute = nameof(CanFinishSignIn))]
    private async Task FinishSignInAsync()
    {
        bool signedIn;
        try
        {
            signedIn = await this.session.FinishSignInAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            this.Stage = Stage.Idle;
            this.Crashed("sign-in", exception);
            this.RefreshCommands();
            return;
        }

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

    internal void UpdatePlan()
    {
        if (this.Scan is not { Ready: true } scan || this.From is not { } from || this.To is not { } to)
        {
            this.preview = null;
            this.PlanSummary = "";
            this.PlanIsSlow = false;
            this.SaveCommand.NotifyCanExecuteChanged();
            return;
        }

        this.session.Pace = this.owner.Pace();
        this.slowConfirmed = false;
        this.preview = this.session.Preview(scan, (int)from, (int)to);
        this.PlanSummary = this.preview.Summary;
        this.PlanIsSlow = this.preview.Slow;
        this.SaveCommand.NotifyCanExecuteChanged();
        this.OnPropertyChanged(nameof(this.RailStatus));
    }

    // -- 3. save --------------------------------------------------------------------

    private bool CanSave => this.Stage == Stage.Idle && this.Scan?.Ready == true && this.preview?.Plan.Problem is null
        && this.preview is not null && (this.Text || this.Pdf);

    partial void OnTextChanged(bool value) => this.SaveCommand.NotifyCanExecuteChanged();

    partial void OnPdfChanged(bool value) => this.SaveCommand.NotifyCanExecuteChanged();

    /// <summary>The folder this extraction's book is saved in.</summary>
    internal string? BookFolder => this.Scan is { } scan
        ? Path.Combine(this.OutputDirectory, BookFiles.FolderName(scan.BookTitle, scan.NovelUrl))
        : null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (this.Scan is not { } scan || this.preview is not { } plan || plan.Plan.Problem is not null)
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
        this.owner.Remember(this);
        this.session.Pace = this.owner.Pace();
        this.StartLog();
        this.PaceNote = this.session.CurrentPaceNote;
        this.log?.Info("save", $"Save pressed: chapters {plan.From}-{plan.To}. {plan.Summary}", url: scan.NovelUrl);
        using var cancel = this.Begin();
        try
        {
            IPipelineObserver observer = new Observer(this);
            if (this.log is { } csv)
            {
                observer = csv.Observe(observer);
            }

            var result = await this.session.SaveAsync(
                scan, plan, this.OutputDirectory, this.Text, this.Pdf, this.StartOver,
                observer, cancel.Token).ConfigureAwait(true);
            foreach (var file in result.Files)
            {
                this.savedFolder = Path.GetDirectoryName(file);
                this.Files.Add(Path.GetFileName(file));
                this.log?.Info("file", "Wrote " + file);
            }

            // From the result, not the rows: the last reports can still be
            // on their way to the list when the run returns.
            var total = plan.To - plan.From + 1;
            var saved = total - result.Missing.Count;
            this.HasOutput = result.Files.Count > 0 || saved > 0;
            this.Status = result.Stopped
                ? $"Stopped. {saved} of {total} saved. Press Save to carry on."
                : result.Outcome switch
                {
                    PipelineOutcome.Ok => string.Create(CultureInfo.InvariantCulture, $"Done. {saved} of {total} saved."),
                    PipelineOutcome.Refused => "This folder holds another book's progress. Tick Start over, or choose another folder.",
                    _ => string.Create(CultureInfo.InvariantCulture,
                        $"{saved} of {total} saved, {result.Missing.Count} not. Press Save again to retry the rest."),
                };
            if (result.Outcome == PipelineOutcome.Refused)
            {
                this.Problem = this.Status;
            }

            // Anything that may have been skipped or doubled is said, by
            // chapter number, where the person reads the result.
            foreach (var (message, chapters) in this.FlagIntegrity(result.SameText))
            {
                var which = BookFiles.Ranges(chapters);
                this.Status += $" {message.Replace("{0}", which, StringComparison.Ordinal)}";
                this.log?.Warning("check", message.Replace("{0}", which, StringComparison.Ordinal));
            }

            // Nobody reads 500 chapters before pasting them into a voice, so
            // a chapter that came out far shorter than the rest is named.
            var shortOnes = this.FlagShortChapters();
            if (shortOnes.Count > 0)
            {
                var which = BookFiles.Ranges(shortOnes);
                this.Status += $" Chapter(s) {which} came out much shorter than the rest; open them in the Reader to check.";
                this.log?.Warning("short", $"Chapter(s) {which} are much shorter than the book's other chapters.");
            }

            var finished = result.Outcome == PipelineOutcome.Ok ? "info" : "warning";
            this.log?.Write(finished, "finished", null, null, this.Status
                + (result.Missing.Count > 0 ? " Missing: " + BookFiles.Ranges(result.Missing) + "." : ""));
        }
        catch (SessionException exception)
        {
            this.Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            this.HasOutput = this.SavedCount > 0;
            this.Status = $"Stopped. {this.ProgressLine}. Press Save to carry on.";
            this.log?.Warning("stopped", this.Status);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            this.HasOutput = this.SavedCount > 0;
            this.Crashed("save", exception);
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
        // Everything about stopping is said before cancelling: the run can
        // finish inside Cancel, and its final message must be the last word.
        this.Stage = Stage.Stopping;
        this.Status = "Stopping...";
        this.log?.Info("stop", "Stop pressed.");
        this.running?.Cancel();
    }

    private bool CanClose => !this.IsBusy && !this.IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private Task CloseAsync() => this.owner.CloseJobAsync(this);

    /// <summary>Bring this extraction's check to the front of the browser.</summary>
    internal Task<bool> ShowCheckAsync() => this.session.ShowCheckAsync();

    // -- files ----------------------------------------------------------------------

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (await this.owner.Shell.PickFolderAsync(this.OutputDirectory).ConfigureAwait(true) is { } folder)
        {
            this.OutputDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        // Where this book was really saved: a same-titled book from another
        // site gets a folder of its own.
        var folder = this.savedFolder ?? this.BookFolder ?? this.OutputDirectory;
        await this.owner.Shell.OpenFolderAsync(Directory.Exists(folder) ? folder : this.OutputDirectory).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CopyChapterAsync()
    {
        if (this.SelectedChapter is { } chapter && chapter.ReadText() is { } text)
        {
            // As the TXT book has it: with or without the heading, and ready
            // for a voice if that is asked for in Settings.
            var copy = BookFiles.Chapter(chapter.Heading, text, !this.owner.LeaveOutHeadings, this.owner.ForSpeech);
            await this.owner.Shell.CopyTextAsync(copy).ConfigureAwait(true);
            this.Status = "Copied.";
        }
    }

    [RelayCommand]
    private void ShowTab(Tab tab) => this.CurrentTab = tab;

    [RelayCommand]
    private void DismissProblem() => this.Problem = null;

    // -- helpers --------------------------------------------------------------------

    /// <summary>A blank line between paragraphs, which chapter text keeps to one newline.</summary>
    internal static string Paragraphs(string? text) =>
        string.IsNullOrEmpty(text)
            ? ""
            : string.Join("\n\n", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && RailInputs.Contains(name))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.RailStatus)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.Caption)));
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.HasProgress)));
            this.owner.JobChanged(this);
        }
    }

    partial void OnStageChanged(Stage value)
    {
        this.OnPropertyChanged(nameof(this.HasPaceNote));
        this.RefreshCommands();
        this.CloseCommand.NotifyCanExecuteChanged();
    }

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

    internal void RefreshCommands()
    {
        this.ScanCommand.NotifyCanExecuteChanged();
        this.SignInCommand.NotifyCanExecuteChanged();
        this.FinishSignInCommand.NotifyCanExecuteChanged();
        this.SaveCommand.NotifyCanExecuteChanged();
        this.StopCommand.NotifyCanExecuteChanged();
        this.CloseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Exceptions a single extraction survives: anything but the runtime itself failing.</summary>
    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? text.Trim();

    private void OnPersonNeeded(object? sender, string? message) => this.post(() =>
    {
        this.PersonMessage = message;
        if (message is null)
        {
            this.log?.Info("person", "The person finished in the browser, or the wait ended.");
        }
        else
        {
            this.log?.Warning("person", message);
        }
    });

    /// <summary>
    /// Start writing this extraction's rows into the app's one log, if logs
    /// are kept. Checked before each scan and save, so turning logs off in
    /// Settings takes effect from the next one.
    /// </summary>
    private void StartLog()
    {
        if (!this.owner.KeepLog || this.owner.Log is not { } common)
        {
            this.log = null;
            return;
        }

        if (this.log is not null)
        {
            this.log.Job = this.LogName;
            return;
        }

        this.log = common.For(this.LogName);
        this.log.Info("started", $"TOC Extractor {MainViewModel.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}).");
        var pace = this.owner.Pace();
        this.log.Info("settings", string.Create(
            CultureInfo.InvariantCulture,
            $"{pace.Concurrency} at once, {pace.MinDelaySeconds:0.#}-{pace.MaxDelaySeconds:0.#}s between pages, keep links {pace.IncludeLinks}, remove ad markers {pace.StripAds}, output {this.OutputDirectory}"));
        this.OnPropertyChanged(nameof(this.LogPath));
    }

    private CancellationTokenSource Begin()
    {
        this.running = new CancellationTokenSource();
        return this.running;
    }

    private void Fail(string message, bool keepStatus = false)
    {
        this.Problem = message;
        if (!keepStatus)
        {
            this.Status = "";
        }

        this.Log(message, toCsv: false);
        this.log?.Write("error", "problem", null, null, message);
    }

    /// <summary>Something no one planned for. The extraction stops; the others carry on; the log keeps the details.</summary>
    private void Crashed(string during, Exception exception)
    {
        this.log?.Error("crash", exception, $"during {during}");
        AppLog.Error("crash", exception, $"extraction {this.Number}, during {during}");
        this.Problem = $"Something went wrong during the {during}: {exception.Message} The details are in the log (Settings, Open log folder). The other extractions are not affected.";
        this.Status = "";
        this.Log($"Something went wrong during the {during}: {exception.GetType().Name}: {exception.Message}", toCsv: false);
    }

    private void Log(string line) => this.Log(line, toCsv: true);

    private void Log(string line, bool toCsv)
    {
        if (toCsv)
        {
            this.log?.Write(CsvLog.LevelOf(line), line.StartsWith("scan", StringComparison.OrdinalIgnoreCase) ? "scan" : "message", null, null, line);
        }

        this.Activity.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        if (this.Activity.Count > 3000)
        {
            this.Activity.RemoveAt(0);
        }
    }

    /// <summary>A removed line seen in this many chapters of one book is the site's own furniture, not story.</summary>
    private const int FurnitureAfter = 3;

    /// <summary>
    /// Mark saved chapters where text on the page may have been left out,
    /// text appears twice, or two chapters have the same text. Returns what
    /// to tell the person, each with its chapters.
    /// </summary>
    /// <remarks>
    /// A long line left out of many chapters of one book is a notice the
    /// site repeats on every page ("If you find any errors, report them"),
    /// which is exactly what should be left out, so it does not count.
    /// </remarks>
    internal List<(string Message, List<int> Chapters)> FlagIntegrity(IReadOnlyList<IReadOnlyList<int>> sameText)
    {
        var saved = this.Chapters.Where(row => row.State == ChapterState.Saved).ToList();
        var seen = saved.SelectMany(row => row.LeftOutLines.Distinct(StringComparer.Ordinal))
            .GroupBy(line => line, StringComparer.Ordinal)
            .Where(group => group.Count() >= FurnitureAfter)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        List<int> leftOut = [];
        List<int> doubled = [];
        foreach (var row in saved)
        {
            row.LeftOut = row.LeftOutLines.Any(line => !seen.Contains(line));
            if (row.LeftOut)
            {
                leftOut.Add(row.Number);
            }

            if (row.Doubled)
            {
                doubled.Add(row.Number);
            }
        }

        List<int> same = [];
        foreach (var group in sameText)
        {
            foreach (var number in group)
            {
                if (this.rowsByNumber.TryGetValue(number, out var row))
                {
                    row.SameTextAs = group.First(other => other != number);
                }

                same.Add(number);
            }
        }

        List<(string, List<int>)> messages = [];
        if (leftOut.Count > 0)
        {
            messages.Add(("Chapter(s) {0}: some text on the site's page was not saved; open them in the Reader to check.", leftOut));
        }

        if (doubled.Count > 0)
        {
            messages.Add(("Chapter(s) {0}: some text was saved twice; open them in the Reader to check.", doubled));
        }

        if (same.Count > 0)
        {
            messages.Add(("Chapter(s) {0}: saved with the same text as another chapter; the site may have shown one page for both.", same));
        }

        return messages;
    }

    /// <summary>
    /// Mark saved chapters far shorter than this book's usual length: a
    /// quarter of the typical chapter, in a book whose chapters are long
    /// enough for that to mean something. Returns their numbers.
    /// </summary>
    internal List<int> FlagShortChapters()
    {
        var saved = this.Chapters.Where(row => row.State == ChapterState.Saved && row.Words > 0).ToList();
        if (saved.Count < 4)
        {
            return [];
        }

        var typical = saved.Select(row => row.Words).Order().ElementAt(saved.Count / 2);
        if (typical < 400)
        {
            return [];
        }

        var threshold = typical / 4;
        List<int> flagged = [];
        foreach (var row in saved)
        {
            row.Short = row.Words < threshold;
            if (row.Short)
            {
                flagged.Add(row.Number);
            }
        }

        return flagged;
    }

    /// <summary>Words in a text, counted without splitting it into a copy.</summary>
    internal static int CountWords(string text)
    {
        var words = 0;
        var inWord = false;
        foreach (var c in text)
        {
            var space = char.IsWhiteSpace(c);
            if (!space && !inWord)
            {
                words++;
            }

            inWord = !space;
        }

        return words;
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

    /// <summary>Carries pipeline reports onto the UI thread. The CSV log has already written them.</summary>
    private sealed class Observer(JobViewModel owner) : IPipelineObserver
    {
        public void Log(string line) => owner.post(() => owner.Log(line, toCsv: false));

        public void Record(ChapterRecord record) => owner.post(() =>
        {
            if (owner.rowsByNumber.TryGetValue(record.Index, out var row))
            {
                row.Title = record.Title;

                // The reader opens the chapter's file when it is shown; only
                // a chapter with no file is kept in memory.
                row.TextLength = record.Text.Length;
                row.File = record.SavedAs;
                row.Text = record.SavedAs is null ? record.Text : null;
                row.Words = CountWords(record.Text);
                if (record.Audit is { } audit)
                {
                    row.LeftOutLines = audit.LeftOutLong;
                    row.LeftOutWords = audit.LeftOutWords;
                    row.Doubled = audit.DoubledText;
                }
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
