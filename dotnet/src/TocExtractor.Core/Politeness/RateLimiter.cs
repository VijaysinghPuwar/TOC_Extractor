using System.Collections.Concurrent;
using System.Diagnostics;

namespace TocExtractor.Core.Politeness;

/// <summary>Waits for <paramref name="duration"/>. Injected so tests spend no real seconds.</summary>
public delegate ValueTask Sleeper(TimeSpan duration, CancellationToken cancellationToken);

/// <summary>A per-host minimum interval that concurrency cannot shorten.</summary>
/// <remarks>
/// <para>
/// The per-host lock is held across the sleep, not merely around the timestamp
/// update. That is the whole point: released early, N workers would each read
/// the same "last request" time and fire together, quietly turning the
/// configured delay into delay ÷ N.
/// </para>
/// <para>
/// Every entry point takes a <see cref="Uri"/> and derives the host key here.
/// Python lets callers pass a key string, and its two callers derive it
/// differently — the crawl-delay override is stored under the authority
/// ("Example.com", or "example.com:8443" when a port is present) while the
/// fetch loop looks it up under the lowercased host. They agree only for a
/// lowercase host on a default port; everywhere else the Crawl-delay is stored
/// and never read, while the log still says it is being honoured. There is no
/// string overload here so that cannot be written.
/// </para>
/// </remarks>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> last = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> overrides = new(StringComparer.Ordinal);
    private readonly TimeSpan minInterval;
    private readonly Func<TimeSpan> clock;
    private readonly Sleeper sleep;

    public RateLimiter(TimeSpan minInterval, Func<TimeSpan>? clock = null, Sleeper? sleep = null)
    {
        this.minInterval = minInterval > TimeSpan.Zero ? minInterval : TimeSpan.Zero;
        this.clock = clock ?? DefaultClock();
        this.sleep = sleep ?? (static (duration, token) =>
            new ValueTask(Task.Delay(duration, token)));
    }

    /// <summary>The key both the override and the wait are filed under.</summary>
    public static string HostKey(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        return url.Host.ToLowerInvariant();
    }

    /// <summary>Raise the interval for one host, e.g. from a Crawl-delay directive.</summary>
    /// <remarks>
    /// Only ever raises. A site asking to be hit faster than the user chose does
    /// not get to speed us up.
    /// </remarks>
    public void SetHostInterval(Uri url, TimeSpan interval)
    {
        var key = HostKey(url);
        this.overrides.AddOrUpdate(
            key,
            interval,
            (_, existing) => interval > existing ? interval : existing);
    }

    public TimeSpan IntervalFor(Uri url)
    {
        var configured = this.overrides.TryGetValue(HostKey(url), out var found)
            ? found
            : TimeSpan.Zero;

        return configured > this.minInterval ? configured : this.minInterval;
    }

    /// <summary>Block until this host may be hit again. Returns how long that took.</summary>
    public async ValueTask<TimeSpan> AcquireAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var key = HostKey(url);
        var gate = this.locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var interval = this.IntervalFor(url);
            var waited = TimeSpan.Zero;

            if (this.last.TryGetValue(key, out var previous))
            {
                var remaining = interval - (this.clock() - previous);
                if (remaining > TimeSpan.Zero)
                {
                    await this.sleep(remaining, cancellationToken).ConfigureAwait(false);
                    waited = remaining;
                }
            }

            this.last[key] = this.clock();
            return waited;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Func<TimeSpan> DefaultClock()
    {
        var since = Stopwatch.StartNew();
        return () => since.Elapsed;
    }
}
