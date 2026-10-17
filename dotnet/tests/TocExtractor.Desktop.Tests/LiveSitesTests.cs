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
/// an entry "url@from-to" saves that range; TOC_LIVE_PARALLEL saves them all
/// at once; TOC_LIVE_HEADFUL shows the browser, as the app does; TOC_LIVE_FROM saves
/// from that chapter instead of the first, TOC_LIVE_COUNT that many chapters
/// instead of five, and TOC_LIVE_OUT into that folder.
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
        var output = Environment.GetEnvironmentVariable("TOC_LIVE_OUT") is { Length: > 0 } into ? into : Path.Combine(scratch, "books");
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

        // An entry can name its own range, "url@from-to"; TOC_LIVE_PARALLEL
        // saves every book at once, as a person running several does.
        var parallel = Environment.GetEnvironmentVariable("TOC_LIVE_PARALLEL") is { Length: > 0 };
        var lines = new string[books.Length];
        async Task RunAsync(int index, JobViewModel job)
        {
            var entry = books[index];
            var at = entry.LastIndexOf('@');
            var range = at > 0 && entry[(at + 1)..].Split('-') is [var a, var b]
                && int.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var first)
                && int.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out var last) ? (first, last) : ((int, int)?)null;
            var book = range is null ? entry : entry[..at];
            job.OutputDirectory = output;
            job.Text = true;
            job.NovelUrl = book;
            var clock = Stopwatch.StartNew();
            await job.ScanCommand.ExecuteAsync(null);
            var line = $"{new Uri(book).Host}: scan {clock.Elapsed.TotalSeconds:0}s: {(job.ScanReady ? job.ScanSummary : "not ready")} {job.Problem}";
            if (job.ScanReady)
            {
                // TOC_LIVE_FROM saves from a later chapter where the book has
                // it: chapters no list shows are reached another way.
                var from = range?.Item1 ?? (int.TryParse(Environment.GetEnvironmentVariable("TOC_LIVE_FROM"), System.Globalization.CultureInfo.InvariantCulture, out var later)
                    && later > job.FirstChapter && later + 2 <= job.LastChapter ? later : job.FirstChapter);
                job.From = from;

                // TOC_LIVE_COUNT saves that many instead of five, to check a
                // long run's text chapter by chapter.
                var count = int.TryParse(Environment.GetEnvironmentVariable("TOC_LIVE_COUNT"), System.Globalization.CultureInfo.InvariantCulture, out var asked)
                    && asked > 0 ? asked : 5;
                job.To = Math.Min(range?.Item2 ?? (from + count - 1), job.LastChapter);
                clock.Restart();
                await job.SaveCommand.ExecuteAsync(null);
                if (job.Stage == Stage.Idle && job.Problem?.Contains("Press Save again", StringComparison.Ordinal) == true)
                {
                    await job.SaveCommand.ExecuteAsync(null);
                }

                line += $" | save {job.From}-{job.To} in {clock.Elapsed.TotalSeconds:0}s: {job.Status} {job.Problem}";
            }

            lines[index] = line.Replace('\n', ' ');
            Console.WriteLine("live: " + line);
        }

        List<Task> running = [];
        for (var index = 0; index < books.Length; index++)
        {
            var job = viewModel.Jobs.Count == 1 && viewModel.SelectedJob!.NovelUrl.Length == 0
                ? viewModel.SelectedJob
                : NewJob(viewModel);
            var work = RunAsync(index, job);
            if (parallel)
            {
                running.Add(work);
                await Task.Delay(500);
            }
            else
            {
                await work;
            }
        }

        await Task.WhenAll(running);
        var report = new StringBuilder();
        foreach (var line in lines)
        {
            report.AppendLine(line);
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
