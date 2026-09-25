using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TocExtractor.App;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Session;
using TocExtractor.Desktop.Services;

namespace TocExtractor.Desktop.ViewModels;

/// <summary>Light, dark, or whatever the computer uses.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// The window: every extraction in a list on the left, the chosen one on the
/// right, and the app's settings.
/// </summary>
/// <remarks>
/// <para>
/// Extractions are independent. Starting one never waits for another: each
/// has its own session, list, progress and log, and they share only the
/// browser and, per site, the pace. There is no limit on how many.
/// </para>
/// <para>
/// All state changes happen on the UI thread; reports from sessions arrive
/// through <c>post</c>.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<INovelService> newSession;
    private readonly BrowserHost? host;
    private readonly BrowserSetup browser;
    private readonly SettingsStore store;
    private readonly Action<Action> post;
    private readonly Action<AppTheme>? applyTheme;
    private readonly Dictionary<string, JobViewModel> claimedFolders = new(StringComparer.OrdinalIgnoreCase);
    private int nextNumber = 1;
    private bool loading;

    /// <remarks>
    /// <c>sessions</c> makes each extraction's session: null for the real one,
    /// sharing one browser. <c>log</c> is the one CSV log every extraction
    /// writes into: null keeps none.
    /// </remarks>
    public MainViewModel(
        Func<INovelService>? sessions,
        IShell shell,
        BrowserSetup browser,
        SettingsStore store,
        Action<Action> post,
        CsvLog? log = null,
        Action<AppTheme>? applyTheme = null)
    {
        this.Shell = shell;
        this.browser = browser;
        this.store = store;
        this.post = post;
        this.applyTheme = applyTheme;
        this.Log = log;
        this.LogFolder = log is null ? null : Path.GetDirectoryName(log.Path);
        if (sessions is null)
        {
            this.host = new BrowserHost(NovelEnvironment.Default(this.Warn));
            var shared = this.host;
            this.newSession = () => new NovelSession(shared);
        }
        else
        {
            this.newSession = sessions;
        }

        var remembered = store.Load();
        this.Apply(remembered);
        var first = this.AddJob();
        first.NovelUrl = remembered.NovelUrl;
        this.SelectedJob = first;
    }

    /// <summary>The app's version, as the build stamped it.</summary>
    public static string Version =>
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public static string VersionText => "Version " + Version;

    internal IShell Shell { get; }

    // -- extractions ----------------------------------------------------------

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJob))]
    public partial JobViewModel? SelectedJob { get; set; }

    public bool HasJob => this.SelectedJob is not null;

    /// <summary>How many are scanning or saving, for the line under the list.</summary>
    public int RunningCount => this.Jobs.Count(job => job.IsBusy);

    public string RunningText => this.RunningCount switch
    {
        0 => "",
        1 => "1 extraction running",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n} extractions running"),
    };

    /// <summary>Another extraction, not the one showing, is waiting for the person in the browser.</summary>
    public string? OtherNeedsPerson => this.Jobs.FirstOrDefault(job => job.PersonNeeded && job != this.SelectedJob) is { } waiting
        ? $"\"{waiting.Caption}\" needs you in the browser window."
        : null;

    public bool HasOtherNeedsPerson => this.OtherNeedsPerson is not null;

    /// <summary>Start another extraction, alongside any already running.</summary>
    [RelayCommand]
    private void NewJob()
    {
        var job = this.AddJob();
        this.SelectedJob = job;
        this.IsSettingsOpen = false;
    }

    /// <summary>Close an extraction that is not running. The last one is replaced by a fresh one.</summary>
    internal async Task CloseJobAsync(JobViewModel job)
    {
        if (job.IsBusy || !this.Jobs.Contains(job))
        {
            return;
        }

        var index = this.Jobs.IndexOf(job);
        this.Jobs.Remove(job);
        AppLog.Info("closed", $"Extraction {job.Number} closed.");
        if (this.Jobs.Count == 0)
        {
            this.AddJob();
        }

        if (this.SelectedJob == job || this.SelectedJob is null)
        {
            this.SelectedJob = this.Jobs[Math.Clamp(index, 0, this.Jobs.Count - 1)];
        }

        this.JobChanged(job);
        await job.DisposeAsync().ConfigureAwait(true);
    }

    internal JobViewModel AddJob()
    {
        var job = new JobViewModel(this.nextNumber++, this.newSession(), this, this.post)
        {
            OutputDirectory = this.lastOutput,
            Text = this.lastText,
            Pdf = this.lastPdf,
        };
        this.Jobs.Add(job);
        AppLog.Info("new", $"Extraction {job.Number} opened.");
        return job;
    }

    /// <summary>Called whenever an extraction's state changes, to keep the summary lines true.</summary>
    internal void JobChanged(JobViewModel job)
    {
        this.OnPropertyChanged(nameof(this.RunningCount));
        this.OnPropertyChanged(nameof(this.RunningText));
        this.OnPropertyChanged(nameof(this.OtherNeedsPerson));
        this.OnPropertyChanged(nameof(this.HasOtherNeedsPerson));
    }

    partial void OnSelectedJobChanged(JobViewModel? value)
    {
        this.OnPropertyChanged(nameof(this.OtherNeedsPerson));
        this.OnPropertyChanged(nameof(this.HasOtherNeedsPerson));
    }

    /// <summary>Claim a book's folder for one extraction's save. False if another is saving there.</summary>
    internal bool TryClaimFolder(string folder, JobViewModel job)
    {
        var key = FolderKey(folder);
        if (this.claimedFolders.TryGetValue(key, out var holder) && holder != job)
        {
            return false;
        }

        this.claimedFolders[key] = job;
        return true;
    }

    internal void ReleaseFolder(string folder, JobViewModel job)
    {
        var key = FolderKey(folder);
        if (this.claimedFolders.TryGetValue(key, out var holder) && holder == job)
        {
            this.claimedFolders.Remove(key);
        }
    }

    private static string FolderKey(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    // -- settings -------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [RelayCommand]
    private void OpenSettings() => this.IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings()
    {
        this.IsSettingsOpen = false;
        this.SaveSettings();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIndex))]
    public partial AppTheme Theme { get; set; }

    /// <summary>The theme as the settings list's position.</summary>
    public int ThemeIndex
    {
        get => (int)this.Theme;
        set => this.Theme = Enum.IsDefined((AppTheme)value) ? (AppTheme)value : AppTheme.System;
    }

    partial void OnThemeChanged(AppTheme value)
    {
        this.applyTheme?.Invoke(value);
        this.SaveSettings();
    }

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

    partial void OnKeepLogChanged(bool value) => this.SaveSettings();

    partial void OnAtOnceChanged(decimal? value) => this.PaceChanged();

    partial void OnMinDelayChanged(decimal? value) => this.PaceChanged();

    partial void OnMaxDelayChanged(decimal? value) => this.PaceChanged();

    partial void OnIncludeLinksChanged(bool value) => this.SaveSettings();

    partial void OnStripAdsChanged(bool value) => this.SaveSettings();

    /// <summary>The one log every extraction writes into, or null when there is none.</summary>
    internal CsvLog? Log { get; }

    /// <summary>The folder the log is in, or null when there is none.</summary>
    public string? LogFolder { get; }

    public string? LogPath => this.Log?.Path;

    public bool HasLogFolder => this.LogFolder is not null;

    [RelayCommand]
    private async Task OpenLogFolderAsync()
    {
        if (this.LogFolder is { } folder)
        {
            await this.Shell.OpenFolderAsync(folder).ConfigureAwait(true);
        }
    }

    // -- first run ----------------------------------------------------------------

    [ObservableProperty]
    public partial bool BrowserReady { get; set; }

    partial void OnBrowserReadyChanged(bool value)
    {
        foreach (var job in this.Jobs)
        {
            job.RefreshCommands();
        }
    }

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

    // -- lifecycle ----------------------------------------------------------------

    /// <summary>Make sure the browser is downloaded. Runs once when the window opens.</summary>
    public async Task StartAsync()
    {
        AppLog.Info("start", $"TOC Extractor {Version} started.");
        try
        {
            if (await this.browser.IsInstalledAsync(CancellationToken.None).ConfigureAwait(true))
            {
                this.Ready();
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Error("browser", exception);
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
        AppLog.Info("browser", "Downloading the browser.");
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
            AppLog.Info("browser", "The browser is downloaded.");
            this.Ready();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or HttpRequestException)
        {
            this.InstallFailed = true;
            this.InstallMessage = exception.Message;
            AppLog.Error("browser", exception);
        }
        finally
        {
            this.Installing = false;
        }
    }

    private void Ready()
    {
        this.BrowserReady = true;
        foreach (var job in this.Jobs.Where(job => job.Status.Length == 0))
        {
            job.Status = "Paste a novel's page and press Scan.";
        }
    }

    /// <summary>The app is closing: remember the form, stop everything, close the browser.</summary>
    public void Shutdown()
    {
        this.SaveSettings();
        AppLog.Info("stop", $"The app is closing with {this.RunningCount} extraction(s) running.");
        foreach (var job in this.Jobs)
        {
            job.Cancel();
        }

        // Close the browser properly before the process ends, or next time
        // it opens with "Chromium didn't shut down correctly". Off the window's
        // thread, which is blocked here, and never for long.
        if (this.host is { } host)
        {
            try
            {
                Task.Run(async () => await host.DisposeAsync().ConfigureAwait(false)).Wait(TimeSpan.FromSeconds(8));
            }
            catch (AggregateException exception)
            {
                AppLog.Error("stop", exception);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var job in this.Jobs.ToList())
        {
            try
            {
                await job.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                AppLog.Error("stop", exception);
            }
        }

        if (this.host is not null)
        {
            await this.host.DisposeAsync().ConfigureAwait(true);
        }
    }

    // -- helpers --------------------------------------------------------------------

    private string lastOutput = AppPaths.DefaultOutputDirectory;
    private bool lastText = true;
    private bool lastPdf;

    internal SessionSettings Pace() => new()
    {
        Concurrency = Math.Clamp((int)(this.AtOnce ?? 1), 1, 8),
        MinDelaySeconds = (double)Math.Max(0, this.MinDelay ?? 2),
        MaxDelaySeconds = (double)Math.Max(this.MaxDelay ?? 4, Math.Max(0, this.MinDelay ?? 2)),
        IncludeLinks = this.IncludeLinks,
        StripAds = this.StripAds,
    };

    /// <summary>An extraction's form, remembered as the starting point for the next one and the next launch.</summary>
    internal void Remember(JobViewModel job)
    {
        this.lastOutput = job.OutputDirectory.Trim();
        this.lastText = job.Text;
        this.lastPdf = job.Pdf;
        this.lastUrl = job.NovelUrl.Trim();
        this.SaveSettings();
    }

    private string lastUrl = "";

    internal DesktopSettings Remembered() => new()
    {
        NovelUrl = this.lastUrl,
        OutputDirectory = this.lastOutput,
        Text = this.lastText,
        Pdf = this.lastPdf,
        KeepLog = this.KeepLog,
        AtOnce = (int)(this.AtOnce ?? 1),
        MinDelay = (double)(this.MinDelay ?? 2),
        MaxDelay = (double)(this.MaxDelay ?? 4),
        IncludeLinks = this.IncludeLinks,
        StripAds = this.StripAds,
        Theme = this.Theme.ToString(),
    };

    internal void Apply(DesktopSettings settings)
    {
        this.loading = true;
        try
        {
            this.lastUrl = settings.NovelUrl;
            this.lastOutput = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? AppPaths.DefaultOutputDirectory : settings.OutputDirectory;
            this.lastText = settings.Text;
            this.lastPdf = settings.Pdf;
            this.KeepLog = settings.KeepLog;
            this.AtOnce = Math.Clamp(settings.AtOnce, 1, 4);
            this.MinDelay = (decimal)Math.Max(0, settings.MinDelay);
            this.MaxDelay = (decimal)Math.Max(settings.MinDelay, settings.MaxDelay);
            this.IncludeLinks = settings.IncludeLinks;
            this.StripAds = settings.StripAds;
            this.Theme = Enum.TryParse<AppTheme>(settings.Theme, ignoreCase: true, out var theme) ? theme : AppTheme.System;
        }
        finally
        {
            this.loading = false;
        }
    }

    private void PaceChanged()
    {
        // The plan's time estimate depends on the pace.
        foreach (var job in this.Jobs.Where(job => job.Stage == Stage.Idle))
        {
            job.UpdatePlan();
        }

        this.SaveSettings();
    }

    private void SaveSettings()
    {
        if (!this.loading)
        {
            this.store.Save(this.Remembered());
        }
    }

    private void Warn(string message) => AppLog.Info("browser", message);
}
