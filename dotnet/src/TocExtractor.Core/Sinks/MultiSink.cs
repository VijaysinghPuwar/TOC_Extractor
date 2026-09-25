using TocExtractor.Core.Models;

namespace TocExtractor.Core.Sinks;

/// <summary>Fans one run out to several exporters.</summary>
/// <remarks>
/// Sequential rather than concurrent: the exporters all write into one
/// directory, and a failure in the third should not race the first two into an
/// inconsistent state.
/// </remarks>
public sealed class MultiSink(IReadOnlyList<ISink> sinks) : ISink
{
    public IReadOnlyList<ISink> Sinks { get; } = sinks;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        foreach (var sink in this.Sinks)
        {
            await sink.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WriteAsync(ChapterRecord record, CancellationToken cancellationToken = default)
    {
        foreach (var sink in this.Sinks)
        {
            await sink.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CloseAsync(RunResult result, CancellationToken cancellationToken = default)
    {
        foreach (var sink in this.Sinks)
        {
            await sink.CloseAsync(result, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyDictionary<string, ChapterOutput> OutputsFor(int index)
    {
        Dictionary<string, ChapterOutput> merged = new(StringComparer.Ordinal);
        foreach (var sink in this.Sinks)
        {
            foreach (var entry in sink.OutputsFor(index))
            {
                merged[entry.Key] = entry.Value;
            }
        }

        return merged;
    }
}
