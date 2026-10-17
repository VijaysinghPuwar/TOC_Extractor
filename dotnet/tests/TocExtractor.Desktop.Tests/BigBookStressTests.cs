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
/// Ten whole books of 200 to 1,000 chapters, saved at once, each from a
/// list paged a hundred at a time whose titles fall behind their addresses
/// the way novelfire's do. Off unless TOC_STRESS is set: it takes minutes.
/// </summary>
/// <remarks>
/// 2.6.7 scanned a 938-chapter novelfire book as 195: from chapter 196 its
/// titles ran behind ("chapter-196" is "Chapter 195 (Part 2)"), and a list
/// page of such links counted none. Every chapter here says which book and
/// address it is, so a chapter saved under the wrong number, from the wrong
/// book, twice, or not at all is caught.
/// </remarks>
public sealed partial class StressTests
{
    private static readonly int[] BigBookChapters = [200, 289, 378, 467, 556, 644, 733, 822, 911, 1000];

    [AvaloniaFact]
    public async Task Ten_whole_books_of_200_to_1000_chapters_save_at_once_with_every_chapter_in_its_place()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TOC_STRESS") is { Length: > 0 }, "Set TOC_STRESS=1 to run the stress test.");

        using var site = new PagedBooks(BigBookChapters);
        var scratch = Harness.Scratch();
        var output = Path.Combine(scratch, "books");
        var environment = new NovelEnvironment
        {
            StartSource = static async (_, options, token) =>
                await BrowserPageSource.StartAsync(new UrlGuard(allowPrivateHosts: true), options with { Headless = true }, token).ConfigureAwait(false),
            FetchRobots = static (_, _) => "User-agent: *\nAllow: /\n",
            BrowserProfileDirectory = Path.Combine(scratch, "profile"),
            Resolver = new PublicLooking(),
        };
        await using var host = new BrowserHost(environment);
        using var log = new CsvLog(Path.Combine(scratch, "big stress log.csv"), "app", append: true);
        var viewModel = new MainViewModel(
            () => new NovelSession(host),
            new FakeShell(),
            BrowserSetup.Present,
            new SettingsStore(Path.Combine(scratch, "settings.json")),
            action => Dispatcher.UIThread.Post(action),
            log: log);
        await viewModel.StartAsync();

        var clock = Stopwatch.StartNew();
        List<(JobViewModel Job, int Book, Task Work)> started = [];
        for (var book = 0; book < BigBookChapters.Length; book++)
        {
            var job = started.Count == 0 ? viewModel.SelectedJob! : NewJob(viewModel);
            job.OutputDirectory = output;
            job.Text = true;
            job.NovelUrl = site.Book(book);
            started.Add((job, book, RunOneAsync(job, 1, BigBookChapters[book])));
            await Task.Delay(250);
        }

        var all = Task.WhenAll(started.Select(s => s.Work));
        while (!all.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(60))
        {
            await Task.WhenAny(all, Task.Delay(1000));
        }

        Assert.True(all.IsCompleted, $"not finished after {clock.Elapsed}");
        await all;
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }

        var report = new StringBuilder();
        List<string> problems = [];
        report.AppendLine(CultureInfo.InvariantCulture, $"big stress: {started.Count} whole books, {BigBookChapters.Sum():N0} chapters, finished in {clock.Elapsed:hh\\:mm\\:ss}");
        foreach (var (job, book, _) in started)
        {
            var chapters = BigBookChapters[book];
            var loads = site.ChapterLoads.GetValueOrDefault(book);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"big stress: #{job.Number} book {book} ({chapters} chapters) scanned {job.FirstChapter}-{job.LastChapter}, saved {job.From}-{job.To}: {job.Status} | {loads} chapter pages opened | {job.Problem}");
            if (job.LastChapter != chapters || job.To != chapters)
            {
                problems.Add($"book {book}: scanned {job.FirstChapter}-{job.LastChapter} and saved {job.From}-{job.To} of {chapters}");
            }

            if (!job.Status.StartsWith($"Done. {chapters} of {chapters} saved.", StringComparison.Ordinal))
            {
                problems.Add($"book {book} did not finish: {job.Status} {job.Problem}");
            }

            if (loads > chapters * 1.25)
            {
                problems.Add($"book {book}: {loads} chapter pages opened for {chapters} chapters");
            }
        }

        // Every chapter file holds its own book's story at its own address.
        foreach (var (_, book, _) in started)
        {
            var folder = Directory.EnumerateDirectories(output).Single(d => Path.GetFileName(d) == $"Paged Book {book}");
            var files = Directory.GetFiles(Path.Combine(folder, "Chapters"), "*.txt");
            var places = new HashSet<int>();
            foreach (var file in files)
            {
                var place = int.Parse(LeadingNumber().Match(Path.GetFileName(file)).Value, CultureInfo.InvariantCulture);
                var text = File.ReadAllText(file);
                var says = StoryMarker().Matches(text).Select(m => (Book: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), At: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).Distinct().ToList();
                if (says is not [var only] || only.Book != book || only.At != place)
                {
                    problems.Add($"book {book}: {Path.GetFileName(file)} holds {string.Join(", ", says.Select(s => $"book {s.Book} chapter {s.At}"))}");
                }

                if (!places.Add(place))
                {
                    problems.Add($"book {book}: chapter {place} saved twice");
                }
            }

            var missing = Enumerable.Range(1, BigBookChapters[book]).Where(n => !places.Contains(n)).ToList();
            if (missing.Count > 0)
            {
                problems.Add($"book {book}: {missing.Count} chapters missing, first {missing[0]}");
            }

            if (Directory.GetFiles(folder, "*.txt").Length != 1)
            {
                problems.Add($"book {book}: {Directory.GetFiles(folder, "*.txt").Length} book files");
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"big stress: {problems.Count} problem(s)");
        problems.ForEach(p => report.AppendLine("big stress: " + p));
        var outputReport = Environment.GetEnvironmentVariable("TOC_STRESS_REPORT") is { Length: > 0 } named
            ? Path.Combine(Path.GetDirectoryName(named)!, "big " + Path.GetFileName(named))
            : Path.Combine(scratch, "big stress report.txt");
        File.WriteAllText(outputReport, report.ToString());
        Console.WriteLine(report.ToString());
        Console.WriteLine($"big stress: report {outputReport}, log {log.Path}, books {output}");

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(50)));
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"in book (\d+), chapter (\d+), part")]
    private static partial Regex StoryMarker();

    /// <summary>
    /// Books laid out like novelfire's: a novel page, a list a hundred
    /// chapters a page with a pager that shows only nearby pages, and from a
    /// fifth of the way in, titles that fall one further behind their
    /// address every so often, as chapters split into parts.
    /// </summary>
    private sealed partial class PagedBooks : IDisposable
    {
        private const int PerPage = 100;

        private readonly int[] chapters;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public PagedBooks(int[] chapters)
        {
            this.chapters = chapters;
            this.listener.Start();
            this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            _ = Task.Run(this.AcceptAsync);
        }

        public int Port { get; }

        public ConcurrentDictionary<int, int> ChapterLoads { get; } = new();

        public string Book(int book) => $"http://w{book}.localtest.me:{this.Port}/book/paged-{book}";

        /// <summary>The number a chapter's title gives: its address, less the parts split off before it.</summary>
        public int Titled(int book, int n) => n - Math.Max(0, (n - (this.chapters[book] / 5)) / 37);

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
                await Task.Delay(Random.Shared.Next(30, 150));
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

            if (!hostName.StartsWith('w') || path == "/favicon.ico")
            {
                return ("404 Not Found", "");
            }

            var book = int.Parse(hostName[1..hostName.IndexOf('.', StringComparison.Ordinal)], CultureInfo.InvariantCulture);
            var count = this.chapters[book];
            var root = $"/book/paged-{book}";
            var name = $"Paged Book {book}";
            if (path == root)
            {
                return ("200 OK", Document(name,
                    $"<h1>{name}</h1><div class=\"header-stats\"><span><strong>{count}</strong><small>Chapters</small></span></div>"
                    + $"<a href=\"{root}/chapter-1\">Read Now</a><a href=\"{root}/chapters\">Chapters</a>"
                    + $"<p>Latest: <a href=\"{root}/chapter-{count}\">Chapter {this.Titled(book, count)}: The End</a></p>"));
            }

            if (path == $"{root}/chapters" || path.StartsWith($"{root}/chapters?page=", StringComparison.Ordinal))
            {
                var page = path.Contains('=', StringComparison.Ordinal) ? int.Parse(path[(path.IndexOf('=', StringComparison.Ordinal) + 1)..], CultureInfo.InvariantCulture) : 1;
                var pages = (count + PerPage - 1) / PerPage;
                var rows = string.Concat(Enumerable.Range(((page - 1) * PerPage) + 1, Math.Max(0, Math.Min(PerPage, count - ((page - 1) * PerPage))))
                    .Select(n => $"<li><a href=\"{root}/chapter-{n}\" title=\"Chapter {this.Titled(book, n)}\"><span>{n}</span> <strong>Chapter {this.Titled(book, n)} {Title(n)}</strong></a></li>"));
                var near = new[] { 1, 2, 3, page - 1, page, page + 1, pages - 1, pages }.Where(p => p >= 1 && p <= pages).Distinct().Order();
                var pager = string.Concat(near.Select(p => $"<li><a href=\"{root}/chapters?page={p}\">{p}</a></li>"));
                return ("200 OK", Document($"{name} chapters",
                    $"<h1>{name}</h1><p>A total of {count} chapters have been translated.</p><ul class=\"chapter-list\">{rows}</ul><ul class=\"pagination\">{pager}</ul>"));
            }

            if (PagedChapter().Match(path) is { Success: true } match && match.Groups[1].Value == book.ToString(CultureInfo.InvariantCulture))
            {
                var n = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (n < 1 || n > count)
                {
                    return ("404 Not Found", Document("Not found", "<h1>Not found</h1>"));
                }

                this.ChapterLoads.AddOrUpdate(book, 1, (_, loads) => loads + 1);
                var title = $"Chapter {this.Titled(book, n)} {Title(n)}";
                var story = new StringBuilder();
                var random = new Random((book * 10000) + n);
                for (var p = 0; p < 20; p++)
                {
                    story.Append("<p>");
                    var words = random.Next(25, 55);
                    for (var w = 0; w < words; w++)
                    {
                        story.Append(w == 0 ? "" : " ").Append(Words[random.Next(Words.Length)].ToLowerInvariant());
                    }

                    story.Append(CultureInfo.InvariantCulture, $" in book {book}, chapter {n}, part {p + 1}.</p>");
                }

                return ("200 OK", Document($"{title} - {name}",
                    $"<nav><a href=\"/\">Home</a></nav><h1>{name}</h1><h2><span class=\"chapter-title\">{title}</span></h2><div id=\"content\">{story}</div><div class=\"nav\">"
                    + (n > 1 ? $"<a rel=\"prev\" href=\"{root}/chapter-{n - 1}\">Prev Chapter</a> " : "")
                    + (n < count ? $"<a rel=\"next\" href=\"{root}/chapter-{n + 1}\">Next Chapter</a>" : "")
                    + "</div>"));
            }

            return ("404 Not Found", Document("Not found", "<h1>Not found</h1>"));
        }

        [GeneratedRegex(@"^/book/paged-(\d+)/chapter-(\d+)$")]
        private static partial Regex PagedChapter();

        private static string Title(int n) => $"The {Words[n % Words.Length]} of {Words[(n * 7) % Words.Length]}";

        private static readonly string[] Words =
        [
            "Mountain", "River", "Sword", "Lantern", "Dragon", "Harbour", "Orchard", "Winter", "Temple", "Market",
            "Storm", "Garden", "Mirror", "Bell", "Forest", "Tower", "Bridge", "Crane", "Pine", "Moon",
        ];

        private static string Document(string title, string body) =>
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title></head><body>{body}</body></html>";
    }
}
