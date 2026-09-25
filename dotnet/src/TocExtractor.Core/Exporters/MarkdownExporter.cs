using System.Security.Cryptography;
using System.Text;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Sinks;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Exporters;

/// <summary>Markdown chapters plus a merged book.md.</summary>
/// <remarks>
/// No EPUB exporter. It would need author, language, cover and spine order,
/// none of which a selector-driven scraper has, so it could only invent them.
/// Running pandoc over book.md covers the same ground honestly.
/// </remarks>
public sealed class MarkdownExporter(
    string outputDirectory,
    bool includeLinks = false,
    IReadOnlyDictionary<string, PriorChapter>? resumed = null) : ISink
{
    public const string FormatName = "markdown";
    public const string BookName = "book.md";

    private readonly Dictionary<string, PriorChapter> resumed =
        resumed is null
            ? new Dictionary<string, PriorChapter>(UrlIdentity.Comparer)
            : resumed.ToDictionary(e => e.Key, e => e.Value, UrlIdentity.Comparer);

    private readonly FilenameAllocator allocator = new(".md");
    private readonly Dictionary<int, ChapterRecord> records = [];
    private readonly Dictionary<int, string> chunks = [];
    private readonly Dictionary<int, ChapterOutput> written = [];

    /// <summary>Stop a title from re-opening the document structure.</summary>
    /// <remarks>
    /// A chapter genuinely titled "# Prologue" would otherwise produce two
    /// headings and break every downstream table of contents.
    /// </remarks>
    internal static string EscapeHeading(string title)
    {
        var trimmed = title.TrimStart('#').Trim();
        return trimmed.Length == 0 ? "untitled" : trimmed;
    }

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var allocated = this.allocator.Allocate(record.Index, record.Title);
        var body = $"# {EscapeHeading(record.Title)}\n\n";
        if (includeLinks)
        {
            body += $"[Source]({record.FinalUrl})\n\n";
        }

        body += record.Text + "\n";

        var path = Path.Combine(outputDirectory, allocated.Name);
        await File.WriteAllTextAsync(path, body, cancellationToken).ConfigureAwait(false);

        this.records[record.Index] = record;
        this.chunks[record.Index] = body + "\n";
        this.written[record.Index] = new ChapterOutput(
            allocated.Name,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
    }

    public async Task CloseAsync(RunResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var plan = MergePlan.Build(result, this.records, this.resumed);
        List<string> missing = [];
        StringBuilder book = new();

        foreach (var entry in plan)
        {
            if (entry.Fresh is not null)
            {
                book.Append(this.chunks[entry.Fresh.Index]);
                continue;
            }

            var prior = entry.Prior!;
            var output = prior.Output(FormatName);
            var path = output is null ? null : Path.Combine(outputDirectory, output.Name);

            // Refused, not skipped. Python guesses this filename by swapping the
            // text exporter's extension and quietly writes a shorter book when
            // the guess is wrong — which it always is when text was not among
            // the chosen formats, because then nothing was recorded to guess
            // from. A merged file missing chapters is the failure every other
            // guard in this pipeline exists to prevent.
            if (path is null || !File.Exists(path))
            {
                missing.Add(output?.Name ?? $"{prior.Title} (no {FormatName} file recorded)");
                continue;
            }

            book.Append(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
                .Append('\n');
        }

        if (missing.Count > 0)
        {
            throw new MergeIncompleteException(
                $"{BookName} would omit chapters the checkpoint records as complete because their "
                + $"files are gone: {string.Join(", ", missing.Order())}. "
                + "Delete the output directory and rerun, or start over.");
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, BookName), book.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    public IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index) =>
        this.written.TryGetValue(index, out var output)
            ? new Dictionary<string, ChapterOutput>(StringComparer.Ordinal) { [FormatName] = output }
            : new Dictionary<string, ChapterOutput>(StringComparer.Ordinal);
}
