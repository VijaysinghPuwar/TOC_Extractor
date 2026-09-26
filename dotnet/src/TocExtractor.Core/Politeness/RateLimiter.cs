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
    private readonly ConcurrentDictionary<string, Allowance> allowances = new(StringComparer.Ordinal);
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

    /// <summary>
    /// Give one host an allowance on top of its interval: up to
    /// <paramref name="burst"/> pages at the usual pace, then one page per
    /// <paramref name="refillEvery"/>, the allowance filling back up while
    /// the host is left alone. For a site that tolerates a burst but checks
    /// visitors who keep up a fast pace.
    /// </summary>
    /// <param name="url">The host.</param>
    /// <param name="burst">Pages at the usual pace before the refill rate applies.</param>
    /// <param name="refillEvery">One page per this, once the burst is spent.</param>
    /// <param name="empty">Start with the burst already spent: the site has just shown its allowance is used up.</param>
    public void SetHostAllowance(Uri url, int burst, TimeSpan refillEvery, bool empty = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        this.allowances[HostKey(url)] = new Allowance(burst, refillEvery, empty ? 0 : burst, empty ? this.clock() : null);
    }

    /// <summary>Take a host's allowance away: back to its interval alone.</summary>
    public void ClearHostAllowance(Uri url) => this.allowances.TryRemove(HostKey(url), out _);

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

            var remaining = TimeSpan.Zero;
            if (this.last.TryGetValue(key, out var previous))
            {
                remaining = interval - (this.clock() - previous);
            }

            // The allowance: wait for a page's worth to refill when it is spent.
            if (this.allowances.TryGetValue(key, out var allowance))
            {
                var now = this.clock();
                var tokens = allowance.Tokens;
                if (allowance.At is { } at && allowance.RefillEvery > TimeSpan.Zero)
                {
                    tokens = Math.Min(allowance.Burst, tokens + ((now - at) / allowance.RefillEvery));
                }

                if (tokens < 1 && allowance.RefillEvery > TimeSpan.Zero)
                {
                    var refill = allowance.RefillEvery * (1 - tokens);
                    if (refill > remaining)
                    {
                        remaining = refill;
                    }
                }

                allowance = allowance with { Tokens = tokens, At = now };
                this.allowances[key] = allowance;
            }

            if (remaining > TimeSpan.Zero)
            {
                await this.sleep(remaining, cancellationToken).ConfigureAwait(false);
                waited = remaining;
            }

            if (this.allowances.TryGetValue(key, out var spend))
            {
                // What refilled during the wait, less this page.
                var now = this.clock();
                var tokens = spend.At is { } at && spend.RefillEvery > TimeSpan.Zero
                    ? Math.Min(spend.Burst, spend.Tokens + ((now - at) / spend.RefillEvery))
                    : spend.Tokens;
                this.allowances[key] = spend with { Tokens = Math.Max(0, tokens - 1), At = now };
            }

            this.last[key] = this.clock();
            return waited;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record Allowance(int Burst, TimeSpan RefillEvery, double Tokens, TimeSpan? At);

    private static Func<TimeSpan> DefaultClock()
    {
        var since = Stopwatch.StartNew();
        return () => since.Elapsed;
    }
}
