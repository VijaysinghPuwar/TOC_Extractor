using System.Net;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using TocExtractor.App.Session;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Fetching;
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

internal sealed class PublicResolver : IHostResolver
{
    public IReadOnlyList<IPAddress> Resolve(string host) => [IPAddress.Parse("93.184.216.34")];
}

/// <summary>Records what the window asked the operating system for.</summary>
internal sealed class FakeShell : IShell
{
    public string? NextFolder { get; set; }

    public string? NextProfileToOpen { get; set; }

    public string? NextProfileToSave { get; set; }

    public List<string> Opened { get; } = [];

    public string? Copied { get; private set; }

    public Task<string?> PickFolderAsync(string? startIn) => Task.FromResult(this.NextFolder);

    public Task<string?> PickProfileToOpenAsync(string startIn) => Task.FromResult(this.NextProfileToOpen);

    public Task<string?> PickProfileToSaveAsync(string startIn, string suggestedName) =>
        Task.FromResult(this.NextProfileToSave);

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

/// <summary>A window over a small fake book, with nothing real behind it.</summary>
internal sealed class Harness
{
    public const string Toc = "https://novel.example/book/contents";

    // broken: chapter numbers whose content selector matches nothing.
    public Harness(int chapters = 6, bool browserReady = true, string? robots = null, int[]? broken = null)
    {
        this.Output = Scratch();
        this.Pages = Book(chapters);
        foreach (var number in broken ?? [])
        {
            this.Pages[$"https://novel.example/book/chapter-{number}"] = new StubPage { MissingSelector = true };
        }

        this.Source = new StubPageSource(this.Pages, maxConcurrent: 3, authenticated: true);
        var environment = new SessionEnvironment
        {
            StartSource = (_, _, _) => Task.FromResult<Core.Pages.IPageSource>(this.Source),
            FetchRobots = (_, _) => robots,
            BrowserProfileDirectory = Scratch(),
            Resolver = new PublicResolver(),
        };

        this.ViewModel = new MainViewModel(
            environment,
            this.Shell,
            browserReady ? BrowserSetup.Present : new BrowserSetup(_ => Task.FromResult(false), (_, token) => Task.Delay(Timeout.Infinite, token)),
            new SettingsStore(Path.Combine(Scratch(), "settings.json")),
            action => Dispatcher.UIThread.Post(action));
        this.Window = new MainWindow { DataContext = this.ViewModel };
    }

    public Dictionary<string, StubPage> Pages { get; }

    public StubPageSource Source { get; }

    public FakeShell Shell { get; } = new();

    public MainViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public string Output { get; }

    public async Task StartAsync()
    {
        this.Window.Show();
        await this.ViewModel.StartAsync();
        Pump();
    }

    public void FillForm(int concurrency = 1)
    {
        this.ViewModel.TocUrl = Toc;
        this.ViewModel.LinkSelector = "ol.chapters a";
        this.ViewModel.TitleSelector = "h1.chapter-title";
        this.ViewModel.ContentSelector = "div.chapter-body";
        this.ViewModel.OutputDirectory = this.Output;
        this.ViewModel.Concurrency = concurrency;
        this.ViewModel.MinDelay = 0;
        this.ViewModel.MaxDelay = 0;
        Pump();
    }

    public async Task ThroughToReadyAsync()
    {
        this.FillForm();
        await this.ViewModel.LaunchCommand.ExecuteAsync(null);
        Pump();
        await this.ViewModel.ConfirmCommand.ExecuteAsync(null);
        Pump();
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

    private static readonly string[] Titles =
    [
        "The Lighthouse Keeper", "A Letter Arrives", "Low Tide", "The Storm Road", "Harbour Lights",
        "What the Gulls Knew", "The Second Letter", "Fog Signal", "Beacon", "Homecoming",
    ];

    private static Dictionary<string, StubPage> Book(int chapters)
    {
        Dictionary<string, StubPage> pages = new(StringComparer.Ordinal)
        {
            [Toc] = new StubPage
            {
                Links = [.. Enumerable.Range(1, chapters).Select(i => (object?)$"https://novel.example/book/chapter-{i}")],
            },
        };

        foreach (var i in Enumerable.Range(1, chapters))
        {
            pages[$"https://novel.example/book/chapter-{i}"] = new StubPage
            {
                Title = $"Chapter {i}: {Titles[(i - 1) % Titles.Length]}",
                Body = string.Join(
                    "\n\n",
                    "The lamp had burned every night for forty years, and on the forty-first it went out. Mara climbed "
                    + "the spiral stair with the spare wick in her pocket and the wind pushing at the glass like a "
                    + "hand that wanted in.",
                    "Below her the harbour was a dark bowl with a few lit windows floating in it. Somewhere down there "
                    + "a boat was late, and everyone in the village knew whose.",
                    "She trimmed the wick, struck the match, and waited for the flame to take."),
            };
        }

        return pages;
    }
}
