using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Scanning;
using TocExtractor.App.Session;
using TocExtractor.Core.Models;
using TocExtractor.Desktop.Services;
using TocExtractor.Desktop.ViewModels;
using TocExtractor.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(TocExtractor.Desktop.Tests.TestApp))]

namespace TocExtractor.Desktop.Tests;

/// <summary>The real App and styles, rendered with Skia but with no screen.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>Records what the window asked the operating system for.</summary>
internal sealed class FakeShell : IShell
{
    public string? NextFolder { get; set; }

    public List<string> Opened { get; } = [];

    public string? Copied { get; private set; }

    public Task<string?> PickFolderAsync(string? startIn) => Task.FromResult(this.NextFolder);

    public Task<string?> PickProfileToOpenAsync(string startIn) => Task.FromResult<string?>(null);

    public Task<string?> PickProfileToSaveAsync(string startIn, string suggestedName) => Task.FromResult<string?>(null);

    public Task OpenFolderAsync(string path)
    {
        this.Opened.Add(path);
        return Task.CompletedTask;
    }

    public Task CopyTextAsync(string text)
    {
        this.Copied = text;
        return Task.CompletedTask;
    }
}

/// <summary>A session that plays out a scan and a download of a made-up book.</summary>
internal sealed class FakeNovelService : INovelService
{
    public const string NovelUrl = "https://novel.example/book/the-lighthouse";

    private static readonly string[] Titles =
    [
        "The Lighthouse Keeper", "A Letter Arrives", "Low Tide", "The Storm Road", "Harbour Lights",
        "What the Gulls Knew", "The Second Letter", "Fog Signal", "Beacon", "Homecoming",
    ];

    public event EventHandler<string?>? PersonNeeded;

    public int Chapters { get; set; } = 40;

    /// <summary>When set, the scan says the chapters need a signed-in reader.</summary>
    public bool RequireSignIn { get; set; }

    public bool SignInSucceeds { get; set; } = true;

    public HashSet<int> Failing { get; } = [];

    /// <summary>Held open mid-save so a test can look at the window while the person is needed.</summary>
    public TaskCompletionSource? PauseAt { get; set; }

    public int PauseBefore { get; set; }

    public bool SignedIn { get; private set; }

    public SessionSettings Pace { get; set; } = new();

    public List<string> Scanned { get; } = [];

    public Task<ScanResult> ScanAsync(string novelUrl, Action<string>? log, CancellationToken cancellationToken = default)
    {
        this.Scanned.Add(novelUrl);
        log?.Invoke("scan: read " + novelUrl);
        var chapters = Enumerable.Range(1, this.Chapters)
            .Select(n => new ScannedChapter(n, Title(n), $"https://novel.example/book/the-lighthouse/{n}"))
            .ToList();
        var scan = new ScanResult
        {
            NovelUrl = novelUrl,
            BookTitle = this.Book,
            Chapters = chapters,
            Layout = new ChapterLayout("h1", "article", "a[rel=next]", "a[rel=prev]"),
            Notes = ["Read the chapter list at https://novel.example/book/the-lighthouse/chapters."],
        };
        return Task.FromResult(this.RequireSignIn && !this.SignedIn
            ? scan with { Problem = NovelSession.SignInNeeded(null), Obstacle = Obstacle.SignIn }
            : scan);
    }

    /// <summary>When set, every range is a long walk that needs confirming.</summary>
    public bool SlowPlans { get; set; }

    public int Saves { get; private set; }

    /// <summary>When set, saving throws something no one planned for.</summary>
    public bool CrashOnSave { get; set; }

    /// <summary>The book's title, so two fakes can be two books.</summary>
    public string Book { get; set; } = "The Lighthouse";

    public RangePreview Preview(ScanResult scan, int first, int last)
    {
        var plan = RangePlanner.Plan(scan, first, last);
        var from = Math.Max(Math.Min(first, last), scan.FirstNumber);
        var to = Math.Min(Math.Max(first, last), scan.LastNumber);
        return this.SlowPlans
            ? new RangePreview(plan, from, to, $"{to - from + 1} chapter(s), 599 extra page(s) visited to reach them. about 30 min.", true)
            : new RangePreview(plan, from, to, $"{to - from + 1} chapter(s), each opened directly. about 1 min.", false);
    }

    public async Task<RangeResult> SaveAsync(
        ScanResult scan,
        RangePreview preview,
        string outputRoot,
        bool text,
        bool pdf,
        bool force,
        IPipelineObserver observer,
        CancellationToken cancellationToken = default)
    {
        this.Saves++;
        if (this.CrashOnSave)
        {
            throw new InvalidOperationException("the fake session broke");
        }

        List<int> missing = [];
        for (var n = preview.From; n <= preview.To; n++)
        {
            if (n == this.PauseBefore && this.PauseAt is { } pause)
            {
                this.PersonNeeded?.Invoke(this, "The site wants to check you're a person. Complete it in the browser window; saving carries on by itself.");
                await pause.Task.WaitAsync(cancellationToken);
                this.PersonNeeded?.Invoke(this, null);
            }

            var url = $"https://novel.example/book/the-lighthouse/{n}";
            if (this.Failing.Contains(n))
            {
                observer.Failure(new FailedChapter(n, url, "selector_not_found", "article matched nothing", 1));
                missing.Add(n);
                continue;
            }

            observer.Trace(new Core.Fetching.FetchTrace("loaded", n, url, "a page, loaded"));
            observer.Record(new ChapterRecord(
                n, url, url, Title(n),
                "The lamp had burned every night for forty years, and on the forty-first it went out.\n"
                + "Below her the harbour was a dark bowl with a few lit windows floating in it.",
                0, DateTimeOffset.Now, 1));
        }

        List<string> files = [];
        if (text)
        {
            files.Add(Path.Combine(outputRoot, "The Lighthouse", $"The Lighthouse {preview.From}-{preview.To}.txt"));
        }

        if (pdf)
        {
            files.Add(Path.Combine(outputRoot, "The Lighthouse", $"The Lighthouse {preview.From}-{preview.To}.pdf"));
        }

        return new RangeResult(missing.Count > 0 ? PipelineOutcome.Failed : PipelineOutcome.Ok, null, files, missing);
    }

    public Task BeginSignInAsync(string novelUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> FinishSignInAsync(CancellationToken cancellationToken = default)
    {
        this.SignedIn = this.SignInSucceeds;
        return Task.FromResult(this.SignedIn);
    }

    public Task CloseAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Title(int n) => $"Chapter {n}: {Titles[(n - 1) % Titles.Length]}";
}

/// <summary>A window over fake sessions, with nothing real behind it.</summary>
internal sealed class Harness
{
    public Harness(bool browserReady = true)
    {
        this.ViewModel = new MainViewModel(
            () =>
            {
                var service = this.Services.Count == 0 ? this.Service : new FakeNovelService();
                this.Services.Add(service);
                return service;
            },
            this.Shell,
            browserReady ? BrowserSetup.Present : new BrowserSetup(_ => Task.FromResult(false), (_, token) => Task.Delay(Timeout.Infinite, token)),
            new SettingsStore(Path.Combine(Scratch(), "settings.json")),
            action => Dispatcher.UIThread.Post(action),
            log: this.Log,
            applyTheme: theme => this.Themes.Add(theme));
        this.Output = Scratch();
        this.Job.OutputDirectory = this.Output;
        this.Window = new MainWindow { DataContext = this.ViewModel };
    }

    /// <summary>The first extraction's session.</summary>
    public FakeNovelService Service { get; } = new();

    /// <summary>Every session made, one per extraction, in order.</summary>
    public List<FakeNovelService> Services { get; } = [];

    public string LogFolder { get; } = Scratch();

    /// <summary>The one log, as the app opens it.</summary>
    public CsvLog Log => this.log ??= new CsvLog(Path.Combine(this.LogFolder, "TOC Extractor log.csv"), "app", append: true);

    private CsvLog? log;

    /// <summary>The log's rows, read the way a spreadsheet would while the app still has it open.</summary>
    public string[] LogRows()
    {
        using var stream = new FileStream(this.Log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public string Output { get; }

    public List<AppTheme> Themes { get; } = [];

    /// <summary>The extraction showing on the right.</summary>
    public JobViewModel Job => this.ViewModel.SelectedJob!;

    public FakeShell Shell { get; } = new();

    public MainViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public async Task StartAsync()
    {
        this.Window.Show();
        await this.ViewModel.StartAsync();
        Pump();
    }

    public async Task ScannedAsync()
    {
        this.Job.NovelUrl = FakeNovelService.NovelUrl;
        Pump();
        await this.Job.ScanCommand.ExecuteAsync(null);
        Pump();
    }

    /// <summary>Start another extraction, as the New extraction button does, and show it.</summary>
    public JobViewModel NewJob()
    {
        this.ViewModel.NewJobCommand.Execute(null);
        this.Job.OutputDirectory = this.Output;
        Pump();
        return this.Job;
    }

    /// <summary>Run everything the dispatcher has queued, as the real loop would between frames.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "toc-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
