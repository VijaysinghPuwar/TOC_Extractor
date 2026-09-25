using System.Collections.Concurrent;

namespace TocExtractor.Core.Politeness;

/// <summary>A <see cref="UrlGuard"/> with its verdicts remembered.</summary>
/// <remarks>
/// Stores the whole verdict, not whether it passed. Python caches a boolean, so
/// the second and later sightings of a blocked URL come back with no reason at
/// all, and callers that fall back to "malformed" report a loopback address as
/// a syntax error. Deduplicating the DNS lookups is a separate concern with a
/// different key, and lives in <see cref="CachingHostResolver"/>.
/// </remarks>
public sealed class UrlScreen(UrlGuard guard)
{
    private readonly ConcurrentDictionary<string, UrlVerdict> verdicts = new(StringComparer.Ordinal);
    private int guardCalls;

    /// <summary>How many times the underlying guard was actually consulted.</summary>
    public int GuardCalls => this.guardCalls;

    /// <summary>The verdict on <paramref name="url"/>, complete with its reason.</summary>
    public UrlVerdict Check(string url) =>
        this.verdicts.GetOrAdd(url, key =>
        {
            Interlocked.Increment(ref this.guardCalls);
            return guard.Check(key);
        });
}
