using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Session;
using TocExtractor.Browser;
using TocExtractor.Core.Politeness;
using TocExtractor.Desktop.Services;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop.Tests;

/// <summary>
/// Forty extractions of fifty chapters each, at once, through the real
/// window's view models, real sessions and a real Chromium, against books
/// served on this machine. Off unless TOC_STRESS is set: it takes minutes.
/// </summary>
/// <remarks>
/// Half the books are laid out like a site that hides most of its list: the site lists chapter 1 (by a
/// first-chapter link) and its newest chapters only, so 1-50 and 51-100 are
/// both reached by following next links from chapter 1, started together.
/// The other half are laid out like a site that lists everything: every chapter listed, and
/// each chapter's text starting with its own title again. Every book is
/// saved as TXT and PDF with chapter numbers and titles left out.
/// </remarks>
public sealed partial class StressTests
{
    /// <summary>Books of each layout, four extractions for each pair: 10 makes forty. TOC_STRESS_BOOKS sets fewer for a quick look.</summary>
    private static int Books => int.TryParse(Environment.GetEnvironmentVariable("TOC_STRESS_BOOKS"), CultureInfo.InvariantCulture, out var books) && books > 0 ? books : 10;

    [AvaloniaFact]
    public async Task Forty_extractions_of_fifty_chapters_each_all_finish_and_keep_the_window_moving()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TOC_STRESS") is { Length: > 0 }, "Set TOC_STRESS=1 to run the stress test.");

        using var site = new LocalBooks();
        var scratch = Harness.Scratch();
        var output = Path.Combine(scratch, "books");
        var environment = new NovelEnvironment
        {
            // The browser reaches this machine's books; the app's own guard
            // still screens every address, told these hosts are public.
            StartSource = static async (_, options, token) =>
                await BrowserPageSource.StartAsync(new UrlGuard(allowPrivateHosts: true), options with { Headless = true }, token).ConfigureAwait(false),
            FetchRobots = static (_, _) => "User-agent: *\nAllow: /\n",
            BrowserProfileDirectory = Path.Combine(scratch, "profile"),
            Resolver = new PublicLooking(),
            Pdf = new ChromiumPdfWriter(),
        };
        await using var host = new BrowserHost(environment);
        using var log = new CsvLog(Path.Combine(scratch, "stress log.csv"), "app", append: true);
        var viewModel = new MainViewModel(
            () => new NovelSession(host),
            new FakeShell(),
            BrowserSetup.Present,
            new SettingsStore(Path.Combine(scratch, "settings.json")),
            action => Dispatcher.UIThread.Post(action),
            log: log);
        viewModel.LeaveOutHeadings = true;
        viewModel.ForSpeech = true;
        await viewModel.StartAsync();

        List<(JobViewModel Job, string Book, Task Work)> started = [];
        for (var book = 0; book < Books; book++)
        {
            foreach (var (url, from, to) in new[]
            {
                (site.FirstOnly(book), 1, 50), (site.FirstOnly(book), 51, 100),
                (site.Listed(book), 101, 150), (site.Listed(book), 151, 200),
            })
            {
                var job = started.Count == 0 ? viewModel.SelectedJob! : NewJob(viewModel);
                job.OutputDirectory = output;
                job.Text = true;
                job.Pdf = true;
                job.NovelUrl = url;
                started.Add((job, url, RunOneAsync(job, from, to)));
                await Task.Delay(250);
            }
        }

        // Watch the window as a person would: every extraction's status line,
        // once a second. A saving extraction whose line has not changed in a
        // long while looks stuck, whatever it is doing underneath.
        var clock = Stopwatch.StartNew();
        Dictionary<JobViewModel, (string Status, TimeSpan Since)> seen = [];
        Dictionary<JobViewModel, TimeSpan> longestStill = [];
        var all = Task.WhenAll(started.Select(s => s.Work));
        while (!all.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(40))
        {
            await Task.WhenAny(all, Task.Delay(1000));
            foreach (var (job, _, _) in started)
            {
                // Waiting its turn for a PDF says so, so it does not look stuck.
                if (job.Stage != Stage.Saving || job.Status.Contains("Writing the book", StringComparison.Ordinal))
                {
                    seen.Remove(job);
                    continue;
                }

                if (!seen.TryGetValue(job, out var last) || last.Status != job.Status)
                {
                    seen[job] = (job.Status, clock.Elapsed);
                    continue;
                }

                var still = clock.Elapsed - last.Since;
                if (still > longestStill.GetValueOrDefault(job))
                {
                    longestStill[job] = still;
                }
            }
        }

        Assert.True(all.IsCompleted, $"not finished after {clock.Elapsed}");
        await all;
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"stress: {started.Count} extractions finished in {clock.Elapsed:mm\\:ss}");
        foreach (var (job, book, _) in started)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"stress: #{job.Number} {book} {job.From}-{job.To}: {job.Status} | longest unchanged status while saving {longestStill.GetValueOrDefault(job).TotalSeconds:0}s | {job.Problem}");
        }

        foreach (var (hostName, loads) in site.ChapterLoads.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"stress: {hostName}: {loads} chapter page loads");
        }

        var outputReport = Environment.GetEnvironmentVariable("TOC_STRESS_REPORT") is { Length: > 0 } named ? named : Path.Combine(scratch, "stress report.txt");
        File.WriteAllText(outputReport, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"stress: report {outputReport}, log {log.Path}, books {output}");

        List<string> problems = [];
        foreach (var (job, book, _) in started)
        {
            if (!job.Status.StartsWith("Done. 50 of 50 saved.", StringComparison.Ordinal))
            {
                problems.Add($"#{job.Number} {book} {job.From}-{job.To} did not finish: {job.Status} {job.Problem}");
            }

            if (longestStill.GetValueOrDefault(job) > TimeSpan.FromSeconds(30))
            {
                problems.Add($"#{job.Number} {book} {job.From}-{job.To} showed the same status for {longestStill[job].TotalSeconds:0}s while saving");
            }
        }

        // Each chapter page opened about once: 100 per book, whatever order
        // the extractions reached them in. A few repeats are the race between
        // two extractions reaching one page together.
        foreach (var (hostName, loads) in site.ChapterLoads)
        {
            if (loads > 100 * 1.25)
            {
                problems.Add($"{hostName}: {loads} chapter pages opened for 100 chapters");
            }
        }

        foreach (var folder in Directory.EnumerateDirectories(output))
        {
            var books = Directory.GetFiles(folder, "*.txt");
            var pdfs = Directory.GetFiles(folder, "*.pdf");
            if (books.Length != 2 || pdfs.Length != 2)
            {
                problems.Add($"{Path.GetFileName(folder)}: {books.Length} TXT and {pdfs.Length} PDF books, expected 2 of each");
            }

            foreach (var file in books)
            {
                var text = File.ReadAllText(file);
                if (TitleLine().IsMatch(text))
                {
                    problems.Add($"{Path.GetFileName(file)} still has a chapter title line: {TitleLine().Match(text).Value.Trim()}");
                }
            }
        }

        // Then Save again further on, as a person does the next day: 101-150
        // of each first-only book, reached from chapter 1 past the hundred saved
        // chapters. It used to open all hundred again before saving one.
        Dictionary<string, int> before = new(site.ChapterLoads, StringComparer.Ordinal);
        var again = started.Where(s => s.Book.Contains("//r", StringComparison.Ordinal) && s.Job.From == 1).ToList();
        var secondClock = Stopwatch.StartNew();
        await Task.WhenAll(again.Select(s =>
        {
            s.Job.From = 101;
            s.Job.To = 150;
            return SaveAsync(s.Job);
        }));
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"stress: then 101-150 of {again.Count} books, past 100 saved chapters, in {secondClock.Elapsed:mm\\:ss}");
        foreach (var (job, book, _) in again)
        {
            var bookHost = new Uri(book).Host;
            var opened = site.ChapterLoads.GetValueOrDefault(bookHost) - before.GetValueOrDefault(bookHost);
            report.AppendLine(CultureInfo.InvariantCulture, $"stress: #{job.Number} {book} 101-150: {job.Status} | {opened} chapter pages opened");
            if (!job.Status.StartsWith("Done. 50 of 50 saved.", StringComparison.Ordinal))
            {
                problems.Add($"#{job.Number} {book} 101-150 did not finish: {job.Status} {job.Problem}");
            }

            if (opened > 52)
            {
                problems.Add($"#{job.Number} {book} 101-150 opened {opened} chapter pages to save 50");
            }
        }

        File.WriteAllText(outputReport, report.ToString());

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [GeneratedRegex(@"(?m)^\s*Chapter \d+.*$")]
    private static partial Regex TitleLine();

    private static JobViewModel NewJob(MainViewModel viewModel)
    {
        viewModel.NewJobCommand.Execute(null);
        return viewModel.SelectedJob!;
    }

    private static async Task RunOneAsync(JobViewModel job, int from, int to)
    {
        await job.ScanCommand.ExecuteAsync(null);
        if (!job.ScanReady)
        {
            return;
        }

        job.From = from;
        job.To = to;
        await SaveAsync(job);
    }

    private static async Task SaveAsync(JobViewModel job)
    {
        await job.SaveCommand.ExecuteAsync(null);

        // A long walk is asked about first; a person would press Save again.
        if (job.Stage == Stage.Idle && job.Problem?.Contains("Press Save again", StringComparison.Ordinal) == true)
        {
            await job.SaveCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Every host looks public to the app's guard; the browser still finds them on this machine.</summary>
    private sealed class PublicLooking : IHostResolver
    {
        public IReadOnlyList<IPAddress> Resolve(string host) => [IPAddress.Parse("93.184.215.14")];
    }

    /// <summary>
    /// Books served from this machine under names that resolve to it
    /// (*.localtest.me), one site per book, so each has its own pace.
    /// </summary>
    private sealed partial class LocalBooks : IDisposable
    {
        private const int Chapters = 250;

        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public LocalBooks()
        {
            this.listener.Start();
            this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            _ = Task.Run(this.AcceptAsync);
        }

        public int Port { get; }

        public ConcurrentDictionary<string, int> ChapterLoads { get; } = new(StringComparer.Ordinal);

        public string FirstOnly(int book) => $"http://r{book}.localtest.me:{this.Port}/novels/book-{book}.html";

        public string Listed(int book) => $"http://n{book}.localtest.me:{this.Port}/book/b{book}";

        public void Dispose()
        {
            this.stop.Cancel();
            this.listener.Stop();
            this.stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!this.stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await this.listener.AcceptTcpClientAsync(this.stop.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => this.ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var request = await reader.ReadLineAsync();
                var hostName = "";
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        hostName = line[5..].Trim().Split(':')[0];
                    }
                }

                var path = request?.Split(' ') is [_, var target, ..] ? target : "/";
                var (status, html) = this.Page(hostName, path);

                // A real site takes a moment to answer.
                await Task.Delay(Random.Shared.Next(50, 250));
                var body = Encoding.UTF8.GetBytes(html);
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: {(path.EndsWith(".txt", StringComparison.Ordinal) ? "text/plain" : "text/html; charset=utf-8")}\r\n"
                    + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head);
                await stream.WriteAsync(body);
            }
            catch (IOException)
            {
                // The browser went away mid-answer.
            }
        }

        private (string Status, string Html) Page(string hostName, string path)
        {
            if (path == "/robots.txt")
            {
                return ("200 OK", "User-agent: *\nAllow: /\n");
            }

            if (path == "/favicon.ico")
            {
                return ("404 Not Found", "");
            }

            var firstOnly = hostName.StartsWith('r');
            var book = int.Parse(hostName[1..hostName.IndexOf('.', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
            if (firstOnly)
            {
                if (path == $"/novels/book-{book}.html")
                {
                    var latest = string.Concat(Enumerable.Range(Chapters - 24, 25).Reverse()
                        .Select(n => $"<li><a href=\"{FirstOnlyUrl(n)}\">Chapter {n}: {Title(n)}</a></li>"));
                    return ("200 OK", Document($"First-only Book {book}",
                        $"<h1>First-only Book {book}</h1><p>A long story.</p><p><a href=\"{FirstOnlyUrl(1)}\">First chapter</a></p><h3>Latest</h3><ul>{latest}</ul>"));
                }

                if (FirstOnlyChapter().Match(path) is { Success: true } match)
                {
                    var id = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var n = id >= 900000 ? (int)((id - 900000) / 3) : (int)(id - 100000);
                    this.ChapterLoads.AddOrUpdate(hostName, 1, (_, count) => count + 1);
                    return ("200 OK", ChapterPage($"First-only Book {book}", $"Chapter {n}: {Title(n)}", Story(book, n, repeatTitle: null),
                        n > 1 ? FirstOnlyUrl(n - 1) : null, n < Chapters ? FirstOnlyUrl(n + 1) : null));
                }
            }
            else
            {
                if (path == $"/book/b{book}")
                {
                    return ("200 OK", Document($"Listed Book {book}",
                        $"<h1>Listed Book {book}</h1><p>A long story.</p><p><a href=\"/book/b{book}/chapters\">Chapters</a></p>"));
                }

                if (path == $"/book/b{book}/chapters")
                {
                    var all = string.Concat(Enumerable.Range(1, Chapters)
                        .Select(n => $"<li><a href=\"/book/b{book}/chapter-{n}\">Chapter {n} {Title(n)}</a></li>"));
                    return ("200 OK", Document($"Listed Book {book} chapters", $"<h1>Listed Book {book}</h1><ul>{all}</ul>"));
                }

                if (ListedChapter().Match(path) is { Success: true } match)
                {
                    var n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    this.ChapterLoads.AddOrUpdate(hostName, 1, (_, count) => count + 1);
                    var title = $"Chapter {n} {Title(n)}";
                    return ("200 OK", ChapterPage($"Listed Book {book}", title, Story(book, n, repeatTitle: title),
                        n > 1 ? $"/book/b{book}/chapter-{n - 1}" : null, n < Chapters ? $"/book/b{book}/chapter-{n + 1}" : null));
                }
            }

            return ("404 Not Found", Document("Not found", "<h1>Not found</h1>"));
        }

        /// <summary>Chapter 1 to 225 have one run of ids, the newest another, so no address pattern holds.</summary>
        private static string FirstOnlyUrl(int n) =>
            n > Chapters - 25 ? $"/read/{900000 + (n * 3)}.html" : $"/read/{100000 + n}.html";

        private static string Title(int n) => $"The {Words[n % Words.Length]} of {Words[(n * 7) % Words.Length]}";

        private static readonly string[] Words =
        [
            "Mountain", "River", "Sword", "Lantern", "Dragon", "Harbour", "Orchard", "Winter", "Temple", "Market",
            "Storm", "Garden", "Mirror", "Bell", "Forest", "Tower", "Bridge", "Crane", "Pine", "Moon",
        ];

        private static string Story(int book, int n, string? repeatTitle)
        {
            var text = new StringBuilder();
            if (repeatTitle is not null)
            {
                text.Append("<p>").Append(repeatTitle).Append("</p>");
            }

            var random = new Random((book * 1000) + n);
            for (var p = 0; p < 30; p++)
            {
                text.Append("<p>");
                var count = random.Next(25, 55);
                for (var w = 0; w < count; w++)
                {
                    text.Append(w == 0 ? "" : " ").Append(Words[random.Next(Words.Length)].ToLowerInvariant());
                }

                text.Append(CultureInfo.InvariantCulture, $" in book {book}, chapter {n}, part {p + 1}.</p>");
            }

            return text.ToString();
        }

        private static string ChapterPage(string book, string title, string story, string? previous, string? next) => Document(
            $"{title} - {book}",
            $"<nav><a href=\"/\">Home</a></nav><h1>{title}</h1><div id=\"text\">{story}</div><div class=\"nav\">"
            + (previous is null ? "" : $"<a rel=\"prev\" href=\"{previous}\">Previous chapter</a> ")
            + (next is null ? "" : $"<a rel=\"next\" href=\"{next}\">Next chapter</a>")
            + "</div><footer><p>Comments are closed.</p></footer>");

        private static string Document(string title, string body) =>
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title></head><body>{body}</body></html>";

        [GeneratedRegex(@"^/read/(\d+)\.html$")]
        private static partial Regex FirstOnlyChapter();

        [GeneratedRegex(@"^/book/b\d+/chapter-(\d+)$")]
        private static partial Regex ListedChapter();
    }
}
