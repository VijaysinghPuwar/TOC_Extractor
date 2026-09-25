using System.Security.Cryptography;
using System.Text;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Sinks;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Exporters;

/// <summary>Per-chapter .txt files and a merged combined.txt.</summary>
/// <remarks>
/// Chapters land as they complete, which with concurrency is out of order.
/// combined.txt is assembled at close in table-of-contents order, because the
/// byte-identity claim is about its contents.
/// </remarks>
public sealed class TextExporter(
    string outputDirectory,
    bool includeLinks = false,
    IReadOnlyDictionary<string, PriorChapter>? resumed = null) : ISink
{
    public const string FormatName = "text";
    public const string CombinedName = "combined.txt";

    private static readonly string Separator = new('-', 80);

    private readonly Dictionary<string, PriorChapter> resumed =
        resumed is null
            ? new Dictionary<string, PriorChapter>(UrlIdentity.Comparer)
            : resumed.ToDictionary(e => e.Key, e => e.Value, UrlIdentity.Comparer);

    private readonly FilenameAllocator allocator = new();
    private readonly Dictionary<int, ChapterRecord> records = [];
    private readonly Dictionary<int, string> chunks = [];
    private readonly Dictionary<int, ChapterOutput> written = [];

    public IReadOnlyDictionary<int, ChapterOutput> Written => this.written;

    public List<(string Existing, string Replacement)> Deduplicated { get; } = [];

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        return Task.CompletedTask;
    }

    public async Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var allocated = this.allocator.Allocate(record.Index, record.Title);
        if (allocated.CollidedWith is not null)
        {
            this.Deduplicated.Add((allocated.CollidedWith, allocated.Name));
        }

        var header = $"{record.Title}\n\n";
        if (includeLinks)
        {
            header += $"Source: {record.FinalUrl}\n\n";
        }

        var body = header + record.Text + "\n";
        var path = Path.Combine(outputDirectory, allocated.Name);
        await File.WriteAllTextAsync(path, body, cancellationToken).ConfigureAwait(false);

        this.records[record.Index] = record;
        this.chunks[record.Index] = body + "\n" + Separator + "\n\n";
        this.written[record.Index] = new ChapterOutput(
            allocated.Name,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
    }

    public async Task CloseAsync(RunResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var plan = MergePlan.Build(result, this.records, this.resumed);
        List<string> missing = [];
        List<string> altered = [];
        StringBuilder combined = new();

        foreach (var entry in plan)
        {
            if (entry.Fresh is not null)
            {
                combined.Append(this.chunks[entry.Fresh.Index]);
                continue;
            }

            var prior = entry.Prior!;
            var output = prior.Output(FormatName);
            if (output is null)
            {
                missing.Add($"{prior.Title} (no {FormatName} file recorded)");
                continue;
            }

            var path = Path.Combine(outputDirectory, output.Name);
            if (!File.Exists(path))
            {
                missing.Add(output.Name);
                continue;
            }

            var raw = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            // Refusing a missing file but accepting an edited one leaves the one
            // gap the previous fix did not cover: a truncated or modified
            // chapter enters the merged output silently. The hash is already
            // recorded, so checking it costs a read we are doing anyway.
            if (output.Sha256.Length > 0
                && Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw))) != output.Sha256)
            {
                altered.Add(output.Name);
                continue;
            }

            // Exact reconstruction: a chapter file is its merged entry minus the
            // separator, so no header has to be re-derived.
            combined.Append(raw).Append('\n').Append(Separator).Append("\n\n");
        }

        if (missing.Count > 0)
        {
            throw new MergeIncompleteException(
                $"{CombinedName} would omit chapters the checkpoint records as complete because "
                + $"their files are gone: {string.Join(", ", missing.Order())}. "
                + "Delete the output directory and rerun, or start over.");
        }

        if (altered.Count > 0)
        {
            throw new MergeIncompleteException(
                "these chapter files changed since they were written, so the merged output would "
                + $"not match what was recorded: {string.Join(", ", altered.Order())}. "
                + "Keep your edits and delete the checkpoint, or start over to refetch.");
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, CombinedName), combined.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    public IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index) =>
        this.written.TryGetValue(index, out var output)
            ? new Dictionary<string, ChapterOutput>(StringComparer.Ordinal) { [FormatName] = output }
            : new Dictionary<string, ChapterOutput>(StringComparer.Ordinal);
}
