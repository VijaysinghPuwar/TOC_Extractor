using TocExtractor.App.Pipeline;

namespace TocExtractor.Desktop.Services;

/// <summary>
/// The app's one CSV log, which every extraction writes into too, each row
/// marked with the extraction it belongs to. The app's own rows, marked
/// "app", say when it started and stopped, the browser download, and
/// anything that went wrong outside an extraction, including a crash, with
/// its stack trace.
/// </summary>
/// <remarks>
/// Off until <see cref="Open"/> is called, which the app does and tests do
/// not, so a test run never writes to a person's log folder.
/// </remarks>
internal static class AppLog
{
    /// <summary>The one log file's name.</summary>
    public const string FileName = "TOC Extractor log.csv";

    /// <summary>Above this size the log is set aside and a new one started.</summary>
    private const long MaxBytes = 20 * 1024 * 1024;


    private static CsvLog? log;

    public static string? Path => log?.Path;

    /// <summary>The open log, for extractions to write their own rows into.</summary>
    public static CsvLog? Root => log;

    public static void Open(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, FileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                File.Move(path, System.IO.Path.Combine(folder, "TOC Extractor log (previous).csv"), overwrite: true);
            }

            log = new CsvLog(path, "app", append: true);
            FoldIn(folder, log);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The app runs without its log rather than not at all.
            log = null;
        }
    }

    public static void Info(string kind, string detail) => log?.Info(kind, detail);

    public static void Error(string kind, Exception exception, string? context = null) => log?.Error(kind, exception, context);

    public static void Close()
    {
        log?.Dispose();
        log = null;
    }

    /// <summary>
    /// Move the rows of any separate log files, from builds that kept one per
    /// extraction, into the one log, oldest first, then delete them. There is
    /// only ever one log to look in.
    /// </summary>
    internal static void FoldIn(string folder, CsvLog into)
    {
        var separate = Directory.EnumerateFiles(folder, "*.csv")
            .Where(path => System.IO.Path.GetFileName(path) is var name
                && (name == "app.csv" || name.StartsWith("log ", StringComparison.Ordinal)))
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToList();
        foreach (var file in separate)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                if (lines.Length > 0 && lines[0].TrimStart('\uFEFF') == CsvLog.Header)
                {
                    foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
                    {
                        into.WriteRaw(line);
                    }
                }

                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Open in a spreadsheet, perhaps. Next time.
            }
        }
    }
}
