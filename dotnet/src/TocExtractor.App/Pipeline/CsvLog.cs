using System.Globalization;
using System.Text;
using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Models;

namespace TocExtractor.App.Pipeline;

/// <summary>
/// Everything one extraction did, one row per step, as a CSV file a
/// spreadsheet opens: time, level, job, event, chapter, address, and what
/// happened. Failures carry the reason, and errors their stack trace.
/// </summary>
/// <remarks>
/// <para>
/// Each row is written and flushed as it happens, so an extraction that is
/// stopped, or an app that crashes, still leaves a log up to that moment.
/// That is the point of it: when something goes wrong, the file says what
/// the app was doing and why it stopped.
/// </para>
/// <para>
/// Rows arrive from several threads at once, since chapters are fetched
/// concurrently and several extractions can share one app log, so writes are
/// serialised. A log that cannot be written never stops the work it records.
/// </para>
/// </remarks>
public sealed class CsvLog : IDisposable
{
    public const string Header = "time,level,job,event,chapter,url,detail";

    /// <summary>The file, shared by this log and every <see cref="For"/> view of it.</summary>
    private readonly File_ file;

    /// <summary>Only the log that opened the file closes it.</summary>
    private readonly bool owner;

    /// <param name="path">The file. Created, with its folder, if it does not exist.</param>
    /// <param name="job">What every row is filed under, so rows from several extractions can be told apart.</param>
    /// <param name="append">Add to an existing file rather than start a new one.</param>
    public CsvLog(string path, string job, bool append = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        this.Path = path;
        this.Job = job ?? "";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var fresh = !append || !File.Exists(path) || new FileInfo(path).Length == 0;

        // With a byte order mark, so Excel reads non-English titles correctly.
        // Shared, so the file can be opened in a spreadsheet while it grows.
        var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(fresh)) { AutoFlush = true, NewLine = "\n" };
        if (fresh)
        {
            writer.WriteLine(Header);
        }

        this.file = new File_(writer);
        this.owner = true;
    }

    private CsvLog(CsvLog parent, string job)
    {
        this.Path = parent.Path;
        this.Job = job ?? "";
        this.file = parent.file;
        this.owner = false;
    }

    /// <summary>
    /// The same file, with rows filed under <paramref name="job"/>: one
    /// common log for the whole app, each extraction's rows marked as its own.
    /// Disposing a view leaves the file open for the others.
    /// </summary>
    public CsvLog For(string job) => new(this, job);

    public string Path { get; }

    public string Job { get; set; }

    /// <summary>A file name for a new log in <paramref name="folder"/>.</summary>
    public static string NewPath(string folder, DateTimeOffset now, string? name = null)
    {
        var stem = "log " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        var safe = string.IsNullOrWhiteSpace(name) ? "" : Core.Text.FileName.Sanitise(name, 60);
        var path = System.IO.Path.Combine(folder, stem + (safe.Length > 0 ? " " + safe : "") + ".csv");

        // Two extractions started in the same second each get a file.
        for (var n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(folder, stem + (safe.Length > 0 ? " " + safe : "") + $" ({n}).csv");
        }

        return path;
    }

    public void Info(string kind, string detail, int? chapter = null, string? url = null) =>
        this.Write("info", kind, chapter, url, detail);

    public void Warning(string kind, string detail, int? chapter = null, string? url = null) =>
        this.Write("warning", kind, chapter, url, detail);

    /// <summary>An exception, with its type, message, inner causes and stack trace.</summary>
    public void Error(string kind, Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        this.Write("error", kind, null, null, (context is null ? "" : context + " | ") + Describe(exception));
    }

    /// <summary>Everything about an exception worth reading after the fact, on one line.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var text = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (text.Length > 0)
            {
                text.Append(" | caused by ");
            }

            text.Append(current.GetType().FullName).Append(": ").Append(current.Message);
        }

        if (exception.StackTrace is { } stack)
        {
            text.Append(" | stack: ").Append(string.Join(" <- ", stack.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)));
        }

        return text.ToString();
    }

    /// <summary>
    /// An observer that writes every report to this log and passes it on to
    /// <paramref name="inner"/>, so the window still sees it.
    /// </summary>
    public IPipelineObserver Observe(IPipelineObserver inner) => new Tee(this, inner ?? throw new ArgumentNullException(nameof(inner)));

    public void Write(string level, string kind, int? chapter, string? url, string detail)
    {
        var line = string.Join(
            ',',
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            level,
            Field(this.Job),
            Field(kind),
            chapter?.ToString(CultureInfo.InvariantCulture) ?? "",
            Field(url),
            Field(detail));
        lock (this.file.Gate)
        {
            if (this.file.Closed)
            {
                return;
            }

            try
            {
                this.file.Writer.WriteLine(line);
            }
            catch (IOException)
            {
                // A full disk or a removed drive: the work goes on without its log.
            }
        }
    }

    /// <summary>A row that is already a complete CSV line, such as one copied from an older log.</summary>
    public void WriteRaw(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (this.file.Gate)
        {
            if (this.file.Closed)
            {
                return;
            }

            try
            {
                this.file.Writer.WriteLine(line.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                // As for every row: the work goes on without it.
            }
        }
    }

    public void Dispose()
    {
        if (!this.owner)
        {
            return;
        }

        lock (this.file.Gate)
        {
            if (this.file.Closed)
            {
                return;
            }

            this.file.Closed = true;
            try
            {
                this.file.Writer.Dispose();
            }
            catch (IOException)
            {
                // Nothing more can be done for it.
            }
        }
    }

    /// <summary>The level a free-text report deserves.</summary>
    public static string LevelOf(string line) =>
        line.StartsWith("could not", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("refusing", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not saved", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : "info";

    /// <summary>A CSV field: quoted, with quotes doubled, when it needs to be.</summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var flat = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

        // A leading = + - @ would make a spreadsheet run the cell as a formula.
        if (flat[0] is '=' or '+' or '-' or '@')
        {
            flat = "'" + flat;
        }

        return flat.IndexOfAny([',', '"']) >= 0 || flat.StartsWith(' ') || flat.EndsWith(' ')
            ? "\"" + flat.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : flat;
    }

    private sealed class Tee(CsvLog log, IPipelineObserver inner) : IPipelineObserver
    {
        public void Log(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            log.Write(LevelOf(line), line.StartsWith("scan:", StringComparison.Ordinal) ? "scan" : "message", null, null, line);
            inner.Log(line);
        }

        public void Collected(CollectedLinks collected)
        {
            ArgumentNullException.ThrowIfNull(collected);
            log.Info("collected", $"{collected.Collection.Kept.Count} chapter link(s)", url: collected.TocUrl);
            inner.Collected(collected);
        }

        public void Resuming(ResumePlan plan, IReadOnlyList<string> alreadyDone)
        {
            ArgumentNullException.ThrowIfNull(alreadyDone);
            log.Info("resuming", $"{alreadyDone.Count} chapter(s) saved by an earlier run");
            inner.Resuming(plan, alreadyDone);
        }

        public void Record(ChapterRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            log.Write(
                "info",
                "saved",
                record.Index,
                record.RequestedUrl,
                string.Create(CultureInfo.InvariantCulture, $"{record.Title} | {record.ByteCount} bytes | attempt {record.Attempts}")
                    + (record.Redirected ? " | redirected to " + record.FinalUrl : "")
                    + (record.Robots is { AuthenticatedOverride: true } ? " | robots.txt override (signed in)" : ""));
            inner.Record(record);
        }

        public void Failure(FailedChapter failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            log.Write(
                "error",
                "failed",
                failure.Index,
                failure.Url,
                string.Create(CultureInfo.InvariantCulture, $"{failure.Reason}: {failure.Detail} | after {failure.Attempts} attempt(s)"));
            inner.Failure(failure);
        }

        public void Trace(FetchTrace trace)
        {
            ArgumentNullException.ThrowIfNull(trace);
            log.Write(trace.Event == "retry" ? "warning" : "info", trace.Event, trace.Chapter, trace.Url, trace.Detail);
            inner.Trace(trace);
        }
    }

    private sealed class File_(StreamWriter writer)
    {
        internal StreamWriter Writer { get; } = writer;

        internal Lock Gate { get; } = new();

        internal bool Closed { get; set; }
    }
}
