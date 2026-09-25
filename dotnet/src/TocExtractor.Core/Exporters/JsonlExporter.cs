using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Sinks;

namespace TocExtractor.Core.Exporters;

/// <summary>A machine-readable record of what the run actually did.</summary>
/// <remarks>
/// One JSON object per line: chapters first, then a single summary, so a
/// consumer can stream chapters without buffering and still find the accounting
/// at the end.
/// </remarks>
public sealed class JsonlExporter(
    string outputDirectory,
    IReadOnlyDictionary<string, PriorChapter>? resumed = null) : ISink
{
    public const string FormatName = "jsonl";
    public const string ManifestName = "manifest.jsonl";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, PriorChapter> resumed =
        resumed is null
            ? new Dictionary<string, PriorChapter>(UrlIdentity.Comparer)
            : resumed.ToDictionary(e => e.Key, e => e.Value, UrlIdentity.Comparer);

    private readonly Dictionary<int, ChapterRecord> records = [];
    private readonly List<JsonObject> lines = [];

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        return Task.CompletedTask;
    }

    public Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        this.records[record.Index] = record;

        JsonObject entry = new()
        {
            ["type"] = "chapter",
            ["index"] = record.Index,
            ["url"] = record.RequestedUrl,
            ["final_url"] = record.FinalUrl,
            ["redirected"] = record.Redirected,
            ["title"] = record.Title,
            ["fetched_at"] = record.FetchedAt.ToString("O"),
            ["attempts"] = record.Attempts,
            ["bytes"] = record.ByteCount,
            ["sha256"] = record.Sha256,
            // The count exists because URLs are deleted from prose. The deletion
            // is unchanged; the silence is not.
            ["stripped_urls"] = record.StrippedUrls,
        };

        if (record.Robots is { } robots)
        {
            foreach (var field in robots.AsManifestEntry())
            {
                entry[field.Key] = field.Value switch
                {
                    bool flag => JsonValue.Create(flag),
                    string text => JsonValue.Create(text),
                    null => null,
                    _ => JsonValue.Create(field.Value.ToString()),
                };
            }
        }

        this.lines.Add(entry);
        return Task.CompletedTask;
    }

    public async Task CloseAsync(RunResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Chapters an earlier run wrote. Included so the manifest describes the
        // whole book rather than only the tail a resume happened to fetch; they
        // carry from_checkpoint so the thinner record is visible, not implied.
        // Built from the merge plan for the same reason the merged text is: by
        // index, a prepended chapter takes a resumed chapter's number and the
        // resumed one disappears from the manifest too.
        var plan = MergePlan.Build(result, this.records, this.resumed);
        foreach (var entry in plan.Where(item => item.Prior is not null))
        {
            var prior = entry.Prior!;
            this.lines.Add(new JsonObject
            {
                ["type"] = "chapter",
                ["from_checkpoint"] = true,
                ["index"] = prior.Index,
                ["url"] = prior.Url,
                ["final_url"] = prior.Url,
                ["title"] = prior.Title,
                ["fetched_at"] = prior.FetchedAt,
                ["bytes"] = prior.Bytes,
                ["sha256"] = prior.Sha256,
                ["stripped_urls"] = prior.StrippedUrls,
            });
        }

        JsonArray rejectedDetail = [];
        foreach (var item in result.Collection.Rejected)
        {
            rejectedDetail.Add(new JsonObject
            {
                ["url"] = item.Value,
                ["reason"] = item.Reason.ToWireValue(),
                ["detail"] = item.Detail,
            });
        }

        JsonObject rejected = new();
        foreach (var count in result.Collection.ReasonCounts().OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            rejected[count.Key] = count.Value;
        }

        JsonArray failed = [];
        foreach (var failure in result.Failed)
        {
            failed.Add(new JsonObject
            {
                ["index"] = failure.Index,
                ["url"] = failure.Url,
                ["reason"] = failure.Reason,
                ["attempts"] = failure.Attempts,
            });
        }

        JsonObject summary = new()
        {
            ["type"] = "summary",
            ["toc_url"] = result.TocUrl,
            ["raw_links"] = result.Collection.RawCount,
            ["kept"] = result.Collection.Kept.Count,
            ["truncated"] = result.Collection.Truncated,
            ["completed"] = result.Completed.Count,
            ["chapters_total"] = this.lines.Count,
            ["skipped_resumed"] = result.SkippedResumed.Count,
            ["rejected"] = rejected,
            ["rejected_detail"] = rejectedDetail,
            ["failed"] = failed,
            ["total_stripped_urls"] = result.TotalStrippedUrls,
        };

        this.lines.Sort((left, right) =>
            (left["index"]?.GetValue<int>() ?? 0).CompareTo(right["index"]?.GetValue<int>() ?? 0));

        StringBuilder text = new();
        foreach (var line in this.lines.Append(summary))
        {
            text.Append(line.ToJsonString(Options)).Append('\n');
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, ManifestName), text.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Nothing per chapter: the manifest is one file for the whole run.</summary>
    public IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index) =>
        new Dictionary<string, ChapterOutput>(StringComparer.Ordinal);
}
