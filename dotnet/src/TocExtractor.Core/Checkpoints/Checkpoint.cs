using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;

namespace TocExtractor.Core.Checkpoints;

/// <summary>How a stored link set relates to the one just collected.</summary>
public enum TocComparison
{
    Identical,
    GrewAtEnd,
    GrewAtStart,

    /// <summary>Fewer links, but the ones that remain are unchanged and in order.</summary>
    Narrowed,
    Diverged,
}

/// <summary>One chapter already written, as recorded on disk.</summary>
public sealed record CompletedChapter
{
    public required int Index { get; init; }

    public required string Url { get; init; }

    public string Title { get; init; } = "";

    public int Bytes { get; init; }

    public string Sha256 { get; init; } = "";

    public int StrippedUrls { get; init; }

    public string FetchedAt { get; init; } = "";

    /// <summary>What each exporter wrote, keyed by format name.</summary>
    public Dictionary<string, ChapterOutput> Outputs { get; init; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public PriorChapter AsPrior => new(
        this.Index, this.Url, this.Title, this.Bytes, this.Sha256,
        this.StrippedUrls, this.FetchedAt, this.Outputs);
}

/// <summary>On-disk resume state for one output directory.</summary>
/// <remarks>
/// <para>
/// Keyed on URL identity, never on output filename. The same chapter can be
/// written under a different name by a later version of the sanitiser, so a
/// filename-keyed resume would decide nothing had been done and refetch the
/// lot — the exact outcome the politeness machinery exists to avoid.
/// </para>
/// <para>
/// Resume is the default. The failure this exists for is a 200-chapter run
/// dying at 180; making the user opt in would mean the common recovery sends
/// 180 redundant requests to someone else's server.
/// </para>
/// </remarks>
public sealed class Checkpoint
{
    public const string FileName = ".toc_extractor_state.json";

    /// <summary>
    /// Bumped from Python's 1: an entry now records one output per format
    /// rather than a single filename.
    /// </summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required string Path { get; init; }

    public required string TocUrl { get; init; }

    public required string Fingerprint { get; init; }

    public Dictionary<string, string> Selectors { get; init; } = new(StringComparer.Ordinal);

    public List<string> LinkSet { get; set; } = [];

    /// <summary>Completed chapters, keyed by URL identity.</summary>
    public Dictionary<string, CompletedChapter> Completed { get; init; } =
        new(UrlIdentity.Comparer);

    public Dictionary<string, FailureNote> Failed { get; init; } = new(UrlIdentity.Comparer);

    public static string PathFor(string outputDirectory) =>
        System.IO.Path.Combine(outputDirectory, FileName);

    /// <summary>Identify the extraction, not the link set.</summary>
    /// <remarks>
    /// Deliberately excludes the URLs. A serial gaining a chapter overnight is
    /// the normal case on the sites this targets, and folding the link set in
    /// here would invalidate a completed run every time it happened. Changing a
    /// selector is different in kind: same book, different text.
    /// </remarks>
    public static string FingerprintOf(string tocUrl, SelectorSet selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        var payload = JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["toc_url"] = UrlIdentity.Of(tocUrl),
            ["link"] = selectors.Link,
            ["title"] = selectors.Title,
            ["content"] = selectors.Content,
        });

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Whether this URL names a chapter an earlier run already wrote.</summary>
    /// <remarks>
    /// Compares identities. Python compares raw strings here while comparing
    /// normalised ones when deciding whether the table of contents changed, so
    /// a site that starts emitting a trailing slash gets "resuming" in the log
    /// and a full refetch in practice.
    /// </remarks>
    public bool IsDone(string url) => this.Completed.ContainsKey(url);

    public void Record(ChapterRecord record, IReadOnlyDictionary<string, ChapterOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(outputs);

        this.Completed[record.RequestedUrl] = new CompletedChapter
        {
            Index = record.Index,
            Url = record.RequestedUrl,
            Title = record.Title,
            Bytes = record.ByteCount,
            Sha256 = record.Sha256,
            StrippedUrls = record.StrippedUrls,
            FetchedAt = record.FetchedAt.ToString("O"),
            Outputs = outputs.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
        };

        this.Failed.Remove(record.RequestedUrl);
    }

    public void RecordFailure(string url, string reason, int attempts) =>
        this.Failed[url] = new FailureNote(reason, attempts);

    public IReadOnlyDictionary<string, PriorChapter> AsPriorChapters()
    {
        Dictionary<string, PriorChapter> prior = new(UrlIdentity.Comparer);
        foreach (var entry in this.Completed)
        {
            prior[entry.Key] = entry.Value.AsPrior;
        }

        return prior;
    }

    public static Checkpoint? Load(string outputDirectory, Action<string>? warn = null)
    {
        var path = PathFor(outputDirectory);
        Stored? stored;
        try
        {
            stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt state file must not abort a run that can simply start
            // over, but it must not be silent either.
            warn?.Invoke($"ignoring unreadable checkpoint at {path}: {exception.Message}");
            return null;
        }

        if (stored is null)
        {
            return null;
        }

        if (stored.Version != SchemaVersion)
        {
            warn?.Invoke($"ignoring checkpoint at {path} written by schema version {stored.Version}");
            return null;
        }

        var checkpoint = new Checkpoint
        {
            Path = path,
            TocUrl = stored.TocUrl,
            Fingerprint = stored.Fingerprint,
            LinkSet = [.. stored.LinkSet],
        };

        foreach (var entry in stored.Selectors)
        {
            checkpoint.Selectors[entry.Key] = entry.Value;
        }

        foreach (var entry in stored.Completed)
        {
            checkpoint.Completed[entry.Key] = entry.Value;
        }

        foreach (var entry in stored.Failed)
        {
            checkpoint.Failed[entry.Key] = entry.Value;
        }

        return checkpoint;
    }

    /// <summary>Write atomically: temp file, flush, replace.</summary>
    /// <remarks>
    /// A run interrupted mid-write must leave either the previous state or the
    /// new one, never a truncated file. The replace is atomic within a
    /// filesystem, so the temp file is created in the same directory.
    /// </remarks>
    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(this.Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new Stored
        {
            Version = SchemaVersion,
            TocUrl = this.TocUrl,
            Fingerprint = this.Fingerprint,
            Selectors = this.Selectors,
            LinkCount = this.LinkSet.Count,
            LinkSet = this.LinkSet,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Completed = this.Completed,
            Failed = this.Failed,
        };

        var temp = System.IO.Path.Combine(
            directory ?? ".", $".state-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, payload, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, this.Path, overwrite: true);
        }
        catch
        {
            // An abandoned temp file in the output directory is the visible
            // residue of a crash nobody needs to see.
            File.Delete(temp);
            throw;
        }
    }

    public void Discard() => File.Delete(this.Path);

    public sealed record FailureNote(string Reason, int Attempts);

    private sealed class Stored
    {
        public int Version { get; set; }

        public string TocUrl { get; set; } = "";

        public string Fingerprint { get; set; } = "";

        public Dictionary<string, string> Selectors { get; set; } = new(StringComparer.Ordinal);

        public int LinkCount { get; set; }

        public List<string> LinkSet { get; set; } = [];

        public string UpdatedAt { get; set; } = "";

        public Dictionary<string, CompletedChapter> Completed { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, FailureNote> Failed { get; set; } = new(StringComparer.Ordinal);
    }
}
