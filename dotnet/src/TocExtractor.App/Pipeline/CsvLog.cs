using System.Globalization;
using System.Text;
using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Models;

namespace TocExtractor.App.Pipeline;

/// <summary>
/// Everything a scan and a download did, one row per step, as a CSV file a
/// spreadsheet opens: time, level, event, chapter, address, and what
/// happened. A failed chapter's row carries the reason.
/// </summary>
/// <remarks>
/// Wraps another observer, so the window still gets every report, and
/// writes each row as it happens: a run that is stopped or crashes still
/// leaves a log up to that point. Rows arrive from several threads during a
/// run; writes are serialised.
/// </remarks>
public sealed class CsvLog : IPipelineObserver, IDisposable
{
    public const string Header = "time,level,event,chapter,url,detail";

    private readonly IPipelineObserver inner;
    private readonly StreamWriter writer;
    private readonly Lock gate = new();

    public CsvLog(string path, IPipelineObserver inner)
    {
        ArgumentNullException.ThrowIfNull(path);
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        // With a byte order mark, so Excel reads non-English titles correctly.
        this.writer = new StreamWriter(path, append: false, new UTF8Encoding(true)) { AutoFlush = true };
        this.writer.WriteLine(Header);
    }

    public string Path { get; }

    /// <summary>A file name for a new log in <paramref name="folder"/>.</summary>
    public static string NewPath(string folder, DateTimeOffset now) =>
        System.IO.Path.Combine(folder, "log " + now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".csv");

    public void Log(string line)
    {
        var level = line.StartsWith("could not", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("refusing", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : "info";
        this.Row(level, line.StartsWith("scan:", StringComparison.Ordinal) ? "scan" : "message", null, null, line);
        this.inner.Log(line);
    }

    public void Collected(CollectedLinks collected)
    {
        ArgumentNullException.ThrowIfNull(collected);
        this.Row("info", "collected", null, collected.TocUrl, $"{collected.Collection.Kept.Count} chapter link(s)");
        this.inner.Collected(collected);
    }

    public void Resuming(ResumePlan plan, IReadOnlyList<string> alreadyDone)
    {
        ArgumentNullException.ThrowIfNull(alreadyDone);
        this.Row("info", "resuming", null, null, $"{alreadyDone.Count} chapter(s) saved by an earlier run");
        this.inner.Resuming(plan, alreadyDone);
    }

    public void Record(ChapterRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.Row(
            "info",
            "saved",
            record.Index,
            record.RequestedUrl,
            string.Create(CultureInfo.InvariantCulture, $"{record.Title} | {record.ByteCount} bytes | attempt {record.Attempts}")
                + (record.Redirected ? " | redirected to " + record.FinalUrl : "")
                + (record.Robots is { AuthenticatedOverride: true } ? " | robots.txt override (signed in)" : ""));
        this.inner.Record(record);
    }

    public void Failure(FailedChapter failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        this.Row(
            "error",
            "failed",
            failure.Index,
            failure.Url,
            string.Create(CultureInfo.InvariantCulture, $"{failure.Reason}: {failure.Detail} | after {failure.Attempts} attempt(s)"));
        this.inner.Failure(failure);
    }

    public void Dispose() => this.writer.Dispose();

    /// <summary>A CSV field: quoted, with quotes doubled, when it needs to be.</summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var flat = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return flat.IndexOfAny([',', '"']) >= 0 || flat.StartsWith(' ') || flat.EndsWith(' ')
            ? "\"" + flat.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : flat;
    }

    private void Row(string level, string kind, int? chapter, string? url, string detail)
    {
        var line = string.Join(
            ',',
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            level,
            kind,
            chapter?.ToString(CultureInfo.InvariantCulture) ?? "",
            Field(url),
            Field(detail));
        lock (this.gate)
        {
            this.writer.WriteLine(line);
        }
    }
}
