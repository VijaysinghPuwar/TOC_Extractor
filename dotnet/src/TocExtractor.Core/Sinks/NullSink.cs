using TocExtractor.Core.Models;

namespace TocExtractor.Core.Sinks;

/// <summary>Discards everything. Used by a dry run, which must not write.</summary>
public sealed class NullSink : ISink
{
    private readonly List<ChapterRecord> records = [];

    public IReadOnlyList<ChapterRecord> Records => this.records;

    public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default)
    {
        this.records.Add(record);
        return Task.CompletedTask;
    }

    public Task CloseAsync(RunResult result, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index) =>
        new Dictionary<string, ChapterOutput>(StringComparer.Ordinal);
}
