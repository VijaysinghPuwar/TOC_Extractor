using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Session;
using TocExtractor.Browser;
using TocExtractor.Desktop.Services;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>
/// One book from every site people have used, scanned and saved through the
/// real window's view models, a real Chromium and each site's real
/// robots.txt. Off unless TOC_LIVE is set: it needs the internet and takes
/// minutes. TOC_LIVE_SITES, as URLs joined by ";;", replaces the list;
/// TOC_LIVE_HEADFUL shows the browser, as the app does.
/// </summary>
public sealed class LiveSitesTests
{
    private static readonly string[] DefaultBooks =
    [
        "https://novelfire.net/book/supreme-daily-login-system",
        "https://www.royalroad.com/fiction/158957/another-world-building-an-empire-from-zero",
        "https://ranobes.top/novels/1075084-talqpfty-grg-v741610.html",
        "https://freewebnovel.com/novel/fortunately-i-met-you",
        "https://lightnovelpub.me/book/cultivation-simulator-starting-from-the-empress-palace",
        "https://www.fanmtl.com/novel/kks31397.html",
        "https://mtl-novel.com/novel/ninja-school-teacher-i-can-become-stronger-by-teaching-2/",

        // Their robots.txt refuses tools like this one: these must say so.
        "https://wtr-lab.com/en/novel/14205/honghuang-i-am-the-emperor-of-mankind-writing-books-like-crazy-to-create-an-emperor",
        "https://novelping.com/book/what-do-you-mean-im-the-captain-of-a-yandere-mercenary-company",
    ];

    [AvaloniaFact]
    public async Task Every_site_scans_and_saves_or_says_why_not()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TOC_LIVE") is { Length: > 0 }, "Set TOC_LIVE=1 to read real sites.");
        var books = Environment.GetEnvironmentVariable("TOC_LIVE_SITES") is { Length: > 0 } list
            ? list.Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DefaultBooks;
        var headful = Environment.GetEnvironmentVariable("TOC_LIVE_HEADFUL") is { Length: > 0 };

        var scratch = Harness.Scratch();
        var output = Path.Combine(scratch, "books");
        var environment = NovelEnvironment.Default(_ => { }) with
        {
            StartSource = async (guard, options, token) =>
                await BrowserPageSource.StartAsync(guard, options with { Headless = !headful }, token).ConfigureAwait(false),
            BrowserProfileDirectory = Path.Combine(scratch, "profile"),
            SitePaces = new SitePaces(null),
            PersonTimeout = TimeSpan.FromSeconds(90),
        };
        await using var host = new BrowserHost(environment);
        using var log = new CsvLog(Path.Combine(scratch, "live log.csv"), "app", append: true);
        var viewModel = new MainViewModel(
            () => new NovelSession(host),
            new FakeShell(),
            BrowserSetup.Present,
            new SettingsStore(Path.Combine(scratch, "settings.json")),
            action => Dispatcher.UIThread.Post(action),
            log: log);
        await viewModel.StartAsync();

        var report = new StringBuilder();
        foreach (var book in books)
        {
            var job = viewModel.Jobs.Count == 1 && viewModel.SelectedJob!.NovelUrl.Length == 0
                ? viewModel.SelectedJob
                : NewJob(viewModel);
            job.OutputDirectory = output;
            job.Text = true;
            job.NovelUrl = book;
            var clock = Stopwatch.StartNew();
            await job.ScanCommand.ExecuteAsync(null);
            var line = $"{new Uri(book).Host}: scan {clock.Elapsed.TotalSeconds:0}s: {(job.ScanReady ? job.ScanSummary : "not ready")} {job.Problem}";
            if (job.ScanReady)
            {
                job.From = job.FirstChapter;
                job.To = Math.Min(job.FirstChapter + 4, job.LastChapter);
                clock.Restart();
                await job.SaveCommand.ExecuteAsync(null);
                if (job.Stage == Stage.Idle && job.Problem?.Contains("Press Save again", StringComparison.Ordinal) == true)
                {
                    await job.SaveCommand.ExecuteAsync(null);
                }

                line += $" | save {job.From}-{job.To} in {clock.Elapsed.TotalSeconds:0}s: {job.Status} {job.Problem}";
            }

            report.AppendLine(line.Replace('\n', ' '));
            Console.WriteLine("live: " + line);
        }

        var path = Environment.GetEnvironmentVariable("TOC_LIVE_REPORT") is { Length: > 0 } named ? named : Path.Combine(scratch, "live report.txt");
        await File.WriteAllTextAsync(path, report.ToString(), TestContext.Current.CancellationToken);
        Console.WriteLine($"live: report {path}, log {log.Path}, books {output}");
    }

    private static JobViewModel NewJob(MainViewModel viewModel)
    {
        viewModel.NewJobCommand.Execute(null);
        return viewModel.SelectedJob!;
    }
}
