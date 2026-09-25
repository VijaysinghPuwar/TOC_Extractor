using System.Collections.Concurrent;
using System.Net;

namespace TocExtractor.Core.Politeness;

/// <summary>Resolves each host once per run.</summary>
/// <remarks>
/// A scraped page of forty images on one CDN is forty screened requests and
/// would otherwise be forty lookups. Keyed on the host, which is what a
/// resolution is actually a property of — Python keys its equivalent cache on
/// the full URL, so it never reuses anything across the forty images its own
/// comment says it exists to avoid.
/// <para>
/// Failures are remembered too. A host that does not resolve will not resolve
/// on the next image either, and retrying it forty times is the same waste
/// wearing a different hat.
/// </para>
/// </remarks>
public sealed class CachingHostResolver(IHostResolver inner) : IHostResolver
{
    private readonly ConcurrentDictionary<string, Answer> answers =
        new(StringComparer.OrdinalIgnoreCase);

    private int lookups;

    /// <summary>How many times the underlying resolver was actually consulted.</summary>
    public int Lookups => this.lookups;

    public IReadOnlyList<IPAddress> Resolve(string host)
    {
        var answer = this.answers.GetOrAdd(host, key =>
        {
            Interlocked.Increment(ref this.lookups);
            try
            {
                return new Answer(inner.Resolve(key), null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new Answer(null, exception);
            }
        });

        return answer.Failure is not null
            ? throw answer.Failure
            : answer.Addresses!;
    }

    private sealed record Answer(IReadOnlyList<IPAddress>? Addresses, Exception? Failure);
}
