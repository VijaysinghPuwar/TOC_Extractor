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

        var fingerprint = Checkpoint.FingerprintOf(request.Scan.NovelUrl, selectors);
        var checkpoint = Checkpoint.Load(folder, observer.Log);
        if (checkpoint is not null && (request.Force || checkpoint.Fingerprint != fingerprint))
        {
            if (!request.Force)
            {
                observer.Log($"refusing to resume: {folder} holds progress for a different book or layout. Use Start over.");
                return new RangeResult(PipelineOutcome.Refused, null, [], []);
            }

            checkpoint.Discard();
            checkpoint = null;
        }

        checkpoint ??= new Checkpoint
        {
            Path = Checkpoint.PathFor(folder),
            TocUrl = request.Scan.NovelUrl,
            Fingerprint = fingerprint,
            Selectors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = layout.TitleSelector,
                ["content"] = layout.ContentSelector,
            },
        };

        var resumed = checkpoint.AsPriorChapters();
        var already = request.Plan.Direct.Count(link => checkpoint.IsDone(link.Url));
        if (already > 0)
        {
            observer.Log($"{already} chapter(s) in this range were saved before and are not fetched again.");
        }

        ISink sink = ExporterRegistry.Build([TextExporter.FormatName], folder, request.Fetch.IncludeLinks, resumed);
        void Persist(ChapterRecord record)
        {
            checkpoint.Record(record, sink.OutputsFor(record.Index));
            checkpoint.Save();
            observer.Record(record);
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
                checkpoint.IsDone,
                page => ChapterNumbers.FromTitle(page.Title),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped. Every chapter saved so far still goes into the book
            // below, so stopping never leaves a person with no file at all.
            stopped = true;
            observer.Log("Stopped. Writing the chapters saved so far.");
        }

        checkpoint.Save();

        // The exporter's merged file follows fetch order, which is backwards
        // after a walk back; the range files below are the book, in order.
        File.Delete(Path.Combine(folder, TextExporter.CombinedName));

        foreach (var failure in result?.Failed ?? [])
        {
            observer.Log($"chapter {failure.Index} failed after {failure.Attempts} attempt(s): {failure.Detail}");
        }

        // The combined files, from the chapter files, for exactly this range.
        // Not cancellable: after a Stop, these are exactly what should still happen.
        var saved = BookFiles.ReadRange(folder, checkpoint, request.From, request.To);
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
            await File.WriteAllTextAsync(path, BookFiles.Combined(chapters.Values), new UTF8Encoding(false), CancellationToken.None)
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
                    [.. chapters.Values.Select(c => (c.Heading, c.Body))],
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
public static class BookFiles
{
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

    public static string Combined(IEnumerable<SavedChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var text = new StringBuilder();
        foreach (var chapter in chapters)
        {
            text.Append(chapter.Heading).Append("\n\n").Append(chapter.Body).Append("\n\n")
                .Append(new string('-', 40)).Append("\n\n");
        }

        return text.ToString();
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
