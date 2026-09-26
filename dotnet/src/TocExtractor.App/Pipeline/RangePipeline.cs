using System.Text;
using TocExtractor.App.Scanning;
using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Exporters;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;

namespace TocExtractor.App.Pipeline;

/// <summary>One range of one book, as the desktop app asks for it.</summary>
public sealed record RangeRequest
{
    public required ScanResult Scan { get; init; }

    public required RangePlan Plan { get; init; }

    public required int From { get; init; }

    public required int To { get; init; }

    /// <summary>The folder chosen for downloads; the book gets its own folder inside.</summary>
    public required string OutputRoot { get; init; }

    public bool WriteText { get; init; } = true;

    public bool WritePdf { get; init; }

    /// <summary>Each chapter in the TXT book starts with its number and title. The PDF always has them.</summary>
    public bool TextHeadings { get; init; } = true;

    /// <summary>Take the symbols a text-to-speech voice reads aloud out of the books.</summary>
    public bool ForSpeech { get; init; }

    public required FetchOptions Fetch { get; init; }

    public bool Force { get; init; }

    /// <summary>The book's own folder: its title, made safe for a file name.</summary>
    public string BookDirectory => Path.Combine(this.OutputRoot, BookFiles.FolderName(this.Scan.BookTitle, this.Scan.NovelUrl));
}

/// <summary>What a range run produced. <c>Stopped</c>: the person pressed Stop, and the files hold what was saved until then.</summary>
public sealed record RangeResult(
    PipelineOutcome Outcome,
    RunResult? Run,
    IReadOnlyList<string> Files,
    IReadOnlyList<int> Missing,
    bool Stopped = false);

/// <summary>Writes a finished range as one book, in PDF.</summary>
public interface IPdfWriter
{
    Task WriteAsync(string title, IReadOnlyList<(string Heading, string Body)> chapters, string path, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches one range of a scanned book and writes it: a text file per
/// chapter, then the range as one text file and/or one PDF.
/// </summary>
/// <remarks>
/// Chapters are written under their own numbers into the book's folder, and
/// a checkpoint there records each one, so choosing 350-400 today and
/// 380-420 tomorrow fetches only 401-420 the second time. The combined files
/// are rebuilt from the chapter files for exactly the chosen range, so they
/// are complete whether a chapter came from this run or an earlier one.
/// </remarks>
public static class RangePipeline
{
    public static async Task<RangeResult> RunAsync(
        RangeRequest request,
        IPageSource source,
        UrlGuard guard,
        RobotsPolicy robots,
        RateLimiter limiter,
        IPipelineObserver observer,
        IPdfWriter? pdf = null,
        Func<HumanCheckException, CancellationToken, Task<bool>>? onHumanCheck = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var layout = request.Scan.Layout ?? throw new InvalidOperationException("the scan found no chapter layout");
        var selectors = SelectorSet.Create("a", layout.TitleSelector, layout.ContentSelector);
        var folder = request.BookDirectory;
        Directory.CreateDirectory(folder);

        // The book files sit in the book's folder; the one-file-per-chapter
        // working set, and the progress record, sit in its Chapters folder,
        // so 500 chapters never bury the book a person is looking for.
        using var book = SharedBook.Enter(folder, observer.Log);
        var chaptersFolder = book.ChaptersFolder;

        // Several extractions can save one book at once (say 101-150 and
        // 151-200). They share one progress record, in memory and behind one
        // lock, so neither overwrites what the other has saved.
        var fingerprint = Checkpoint.FingerprintOf(request.Scan.NovelUrl, selectors);
        Checkpoint checkpoint;
        lock (book.Gate)
        {
            if (book.Checkpoint is null)
            {
                var loaded = Checkpoint.Load(chaptersFolder, observer.Log);
                if (loaded is not null && (request.Force || loaded.Fingerprint != fingerprint))
                {
                    if (!request.Force)
                    {
                        observer.Log($"refusing to resume: {folder} holds progress for a different book or layout. Use Start over.");
                        return new RangeResult(PipelineOutcome.Refused, null, [], []);
                    }

                    loaded.Discard();
                    loaded = null;
                }

                book.Checkpoint = loaded ?? new Checkpoint
                {
                    Path = Checkpoint.PathFor(chaptersFolder),
                    TocUrl = request.Scan.NovelUrl,
                    Fingerprint = fingerprint,
                    Selectors = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["title"] = layout.TitleSelector,
                        ["content"] = layout.ContentSelector,
                    },
                };
            }
            else if (book.Checkpoint.Fingerprint != fingerprint)
            {
                observer.Log($"refusing: another extraction is saving a different layout of this book into {folder} right now.");
                return new RangeResult(PipelineOutcome.Refused, null, [], []);
            }
            else if (request.Force)
            {
                // Throwing away progress another extraction is adding to
                // would lose its chapters mid-save.
                observer.Log("Start over was not applied: another extraction is saving this book right now.");
            }

            checkpoint = book.Checkpoint;
        }

        IReadOnlyDictionary<string, PriorChapter> resumed;
        int already;
        lock (book.Gate)
        {
            resumed = checkpoint.AsPriorChapters();
            already = request.Plan.Direct.Count(link => checkpoint.IsDone(link.Url));
        }

        if (already > 0)
        {
            observer.Log($"{already} chapter(s) in this range were saved before and are not fetched again.");
        }

        // Only the chapter files: the book itself is built below, for exactly
        // the chosen range, so the exporter's own merged file is not wanted.
        var sink = new ChapterFilesOnly(ExporterRegistry.Build([TextExporter.FormatName], chaptersFolder, request.Fetch.IncludeLinks, resumed));
        void Persist(ChapterRecord record)
        {
            lock (book.Gate)
            {
                checkpoint.Record(record, sink.OutputsFor(record.Index));
                checkpoint.Save();
            }

            observer.Record(record);
        }

        bool IsDone(string url)
        {
            lock (book.Gate)
            {
                return checkpoint.IsDone(url);
            }
        }

        using var fetcher = new Fetcher(
            new TidyTitles(source), guard, sink, request.Fetch, limiter, robots,
            onRecord: Persist, onFailure: observer.Failure, onHumanCheck: onHumanCheck, onTrace: observer.Trace);

        observer.Log($"Fetching chapters {request.From}-{request.To}: {request.Plan.Direct.Count} by address"
            + (request.Plan.PredictedLinks.Count > 0 ? $" ({request.Plan.PredictedLinks.Count} from the address pattern)" : "")
            + (request.Plan.Walks.Count > 0 ? $", {request.Plan.Walks.Count} stretch(es) by following links" : "")
            + (request.Plan.ExtraVisits > 0 ? $", {request.Plan.ExtraVisits} page(s) visited only to get there" : "")
            + ".");

        RunResult? result = null;
        var stopped = false;
        try
        {
            result = await fetcher.FetchRangeAsync(
                request.Scan.NovelUrl,
                request.Plan.Direct,
                request.Plan.Walks,
                selectors,
                IsDone,
                // A book numbered by place: a walked page is the next place,
                // whatever its title says.
                request.Scan.PositionalNumbers ? null : page => ChapterNumbers.FromTitle(page.Title),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped. Every chapter saved so far still goes into the book
            // below, so stopping never leaves a person with no file at all.
            stopped = true;
            observer.Log("Stopped. Writing the chapters saved so far.");
        }

        SortedDictionary<int, SavedChapter> saved;
        lock (book.Gate)
        {
            checkpoint.Save();
            saved = BookFiles.ReadRange(chaptersFolder, checkpoint, request.From, request.To);
        }

        foreach (var failure in result?.Failed ?? [])
        {
            observer.Log($"chapter {failure.Index} failed after {failure.Attempts} attempt(s): {failure.Detail}");
        }

        // The combined files, from the chapter files, for exactly this range.
        // Not cancellable: after a Stop, these are exactly what should still happen.
        var missing = Enumerable.Range(request.From, request.To - request.From + 1)
            .Where(n => !saved.ContainsKey(n)).ToList();
        List<string> files = [];

        // The book file only ever holds an unbroken run of chapters, and is
        // named for exactly that run. Asked for 1-50 with chapter 27 failed,
        // it is "1-26", never "1-50": a name must not promise chapters the
        // file does not have. Chapters after a gap stay saved on their own and
        // join the book once Save again fills the gap.
        var chapters = BookFiles.FirstRun(saved);
        var stem = chapters.Count == 0
            ? ""
            : BookFiles.RangeName(request.Scan.BookTitle, chapters.Keys.First(), chapters.Keys.Last());
        if (missing.Count > 0 && chapters.Count > 0)
        {
            var later = saved.Count - chapters.Count;
            observer.Log($"The book file holds chapters {chapters.Keys.First()}-{chapters.Keys.Last()} only, because chapter(s) "
                + $"{BookFiles.Ranges(missing)} are not saved yet"
                + (later > 0 ? $"; {later} chapter(s) after the gap are saved and join it once the gap is filled." : "."));
        }

        if (request.WriteText && chapters.Count > 0)
        {
            var path = Path.Combine(folder, stem + ".txt");
            await File.WriteAllTextAsync(path, BookFiles.Combined(chapters.Values, request.TextHeadings, request.ForSpeech), new UTF8Encoding(false), CancellationToken.None)
                .ConfigureAwait(false);
            files.Add(path);
            observer.Log($"Wrote {Path.GetFileName(path)} ({chapters.Count} chapters).");
        }

        if (request.WritePdf && chapters.Count > 0 && pdf is not null)
        {
            var path = Path.Combine(folder, stem + ".pdf");
            try
            {
                await pdf.WriteAsync(
                    request.Scan.BookTitle,
                    [.. chapters.Values.Select(c => (c.Heading, request.ForSpeech ? SpeechText.Clean(c.Body) : c.Body))],
                    path,
                    CancellationToken.None).ConfigureAwait(false);
                files.Add(path);
                observer.Log($"Wrote {Path.GetFileName(path)}.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // The chapters are safe on disk; only the one PDF is missing,
                // and Save again rebuilds it without fetching anything.
                observer.Log($"Could not write the PDF: {exception.GetType().Name}: {exception.Message}. The chapters are saved; press Save again to retry the PDF.");
            }
        }

        // Once the whole range is there, the shorter books inside it are stale.
        if (missing.Count == 0 && chapters.Count > 0)
        {
            foreach (var stale in BookFiles.ShorterBooks(folder, request.Scan.BookTitle, request.From, request.To))
            {
                try
                {
                    File.Delete(stale);
                    observer.Log($"Removed {Path.GetFileName(stale)}; {stem} has all of it.");
                }
                catch (IOException)
                {
                    // Open elsewhere; harmless to leave.
                }
            }
        }

        if (missing.Count > 0)
        {
            observer.Log($"Not saved: chapter(s) {BookFiles.Ranges(missing)}. Press Save again to fetch them.");
        }

        var outcome = stopped || result is null || result.Failed.Count > 0 || missing.Count > 0 ? PipelineOutcome.Failed : PipelineOutcome.Ok;
        return new RangeResult(outcome, result, files, missing, stopped);
    }
}

/// <summary>A saved chapter read back from its file.</summary>
public sealed record SavedChapter(int Number, string Heading, string Body);

/// <summary>Names and assembles the files for one book.</summary>
public static partial class BookFiles
{
    /// <summary>The subfolder of a book's folder that holds one file per chapter.</summary>
    public const string ChaptersFolderName = "Chapters";

    /// <summary>
    /// The book's Chapters folder, created if need be. A book saved before
    /// there was one has its chapter files and progress record moved in, so
    /// nothing is fetched again and the book's folder is left tidy.
    /// </summary>
    public static string MoveChaptersIn(string folder, Action<string>? log = null)
    {
        var chapters = Path.Combine(folder, ChaptersFolderName);
        Directory.CreateDirectory(chapters);
        var oldState = Checkpoint.PathFor(folder);
        if (!File.Exists(oldState) || File.Exists(Checkpoint.PathFor(chapters)))
        {
            return chapters;
        }

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.txt"))
        {
            if (ChapterFile().IsMatch(Path.GetFileName(file)))
            {
                File.Move(file, Path.Combine(chapters, Path.GetFileName(file)), overwrite: false);
                moved++;
            }
        }

        File.Move(oldState, Checkpoint.PathFor(chapters));
        var combined = Path.Combine(folder, TextExporter.CombinedName);
        if (File.Exists(combined))
        {
            File.Delete(combined);
        }

        log?.Invoke($"Moved {moved} chapter file(s) into the {ChaptersFolderName} folder, so the book files stand out.");
        return chapters;
    }

    /// <summary>A chapter's own file: "012 - Chapter 12.txt".</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{3,} - .*\.txt$")]
    private static partial System.Text.RegularExpressions.Regex ChapterFile();

    public static string FolderName(string title, string novelUrl)
    {
        var name = Core.Text.FileName.Sanitise(string.IsNullOrWhiteSpace(title) ? HostOf(novelUrl) : FirstLine(title), 80);
        return string.IsNullOrWhiteSpace(name) ? "Book" : name;
    }

    public static string RangeName(string title, int from, int to) =>
        FolderName(title, "") + $" {from}-{to}";

    /// <summary>The chapters from <paramref name="from"/> to <paramref name="to"/> that have a file, in order.</summary>
    public static SortedDictionary<int, SavedChapter> ReadRange(string folder, Checkpoint checkpoint, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        SortedDictionary<int, SavedChapter> chapters = [];
        foreach (var done in checkpoint.Completed.Values)
        {
            if (done.Index < from || done.Index > to
                || !done.Outputs.TryGetValue(TextExporter.FormatName, out var output))
            {
                continue;
            }

            var path = Path.Combine(folder, output.Name);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            var heading = split > 0 ? text[..split].Trim() : done.Title;
            var body = split > 0 ? text[(split + 2)..].Trim() : text.Trim();
            chapters[done.Index] = new SavedChapter(done.Index, heading, body);
        }

        return chapters;
    }

    /// <summary>The first unbroken run of chapter numbers, so a book file never has a hole in it.</summary>
    public static SortedDictionary<int, SavedChapter> FirstRun(SortedDictionary<int, SavedChapter> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        SortedDictionary<int, SavedChapter> run = [];
        foreach (var (number, chapter) in saved)
        {
            if (run.Count > 0 && number != run.Keys.Last() + 1)
            {
                break;
            }

            run[number] = chapter;
        }

        return run;
    }

    /// <summary>
    /// Book files for ranges inside <paramref name="from"/>-<paramref name="to"/>
    /// other than that range itself: what an earlier, shorter save wrote.
    /// </summary>
    public static IReadOnlyList<string> ShorterBooks(string folder, string title, int from, int to)
    {
        var prefix = FolderName(title, "") + " ";
        List<string> stale = [];
        foreach (var path in Directory.EnumerateFiles(folder, prefix + "*"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            if (extension is not (".txt" or ".pdf") || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var span = name[prefix.Length..];
            span = span.Replace(" (partial", "|", StringComparison.Ordinal).Split('|')[0];
            var parts = span.Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var a)
                && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var b)
                && a >= from && b <= to && (a, b) != (from, to))
            {
                stale.Add(path);
            }
        }

        return stale;
    }

    /// <summary>The chapters as one text, in order.</summary>
    /// <param name="chapters">The chapters.</param>
    /// <param name="headings">Start each with its heading. Without, chapters are parted by a blank line only, so a voice reads straight on.</param>
    /// <param name="forSpeech">Take out symbols a voice would read aloud, the dividers included.</param>
    public static string Combined(IEnumerable<SavedChapter> chapters, bool headings = true, bool forSpeech = false)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var text = new StringBuilder();
        foreach (var chapter in chapters)
        {
            text.Append(Chapter(chapter.Heading, chapter.Body, headings, forSpeech)).Append("\n\n");
            if (!forSpeech && headings)
            {
                text.Append(new string('-', 40)).Append("\n\n");
            }
        }

        return text.ToString();
    }

    /// <summary>One chapter as text, with or without its heading, cleaned for a voice or not.</summary>
    public static string Chapter(string? heading, string body, bool headings = true, bool forSpeech = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        var story = forSpeech ? SpeechText.Clean(body) : body.Trim();
        return headings && !string.IsNullOrWhiteSpace(heading) ? heading.Trim() + "\n\n" + story : story;
    }

    /// <summary>"3, 5-9, 12" from a sorted list of numbers.</summary>
    public static string Ranges(IReadOnlyList<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        List<string> parts = [];
        for (var i = 0; i < numbers.Count; i++)
        {
            var start = numbers[i];
            while (i + 1 < numbers.Count && numbers[i + 1] == numbers[i] + 1)
            {
                i++;
            }

            parts.Add(start == numbers[i] ? $"{start}" : $"{start}-{numbers[i]}");
        }

        return string.Join(", ", parts);
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? text;

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "Book";
}

/// <summary>
/// One book's folder while extractions are saving into it: its Chapters
/// folder and the progress record they all share.
/// </summary>
internal sealed class SharedBook : IDisposable
{
    private static readonly Dictionary<string, SharedBook> Open = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Registry = new();

    private readonly string key;
    private int users;

    private SharedBook(string key, string chaptersFolder)
    {
        this.key = key;
        this.ChaptersFolder = chaptersFolder;
    }

    public string ChaptersFolder { get; }

    /// <summary>Guards <see cref="Checkpoint"/>, which several runs read and write.</summary>
    public Lock Gate { get; } = new();

    /// <summary>The progress record, loaded by the first run in and shared until the last one leaves.</summary>
    public Checkpoint? Checkpoint { get; set; }

    public static SharedBook Enter(string folder, Action<string>? log)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        lock (Registry)
        {
            if (!Open.TryGetValue(key, out var book))
            {
                book = new SharedBook(key, BookFiles.MoveChaptersIn(folder, log));
                Open[key] = book;
            }

            book.users++;
            return book;
        }
    }

    public void Dispose()
    {
        lock (Registry)
        {
            if (--this.users == 0)
            {
                Open.Remove(this.key);
            }
        }
    }
}

/// <summary>Writes each chapter's file and nothing else: no merged file when the run closes.</summary>
internal sealed class ChapterFilesOnly(ISink inner) : ISink
{
    public Task OpenAsync(CancellationToken cancellationToken = default) => inner.OpenAsync(cancellationToken);

    public Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(record, cancellationToken);

    public Task CloseAsync(RunResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index) => inner.OutputsFor(index);
}
