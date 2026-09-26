using System.Collections.Concurrent;
using System.Text.Json;
using TocExtractor.Core.Politeness;

namespace TocExtractor.App.Session;

/// <summary>What the app has learned about one site's pace.</summary>
/// <param name="Site">The site, as scheme and host.</param>
/// <param name="Checks">How many times it has asked to check the person is human.</param>
/// <param name="Careful">Read at the careful pace. On once the site has asked; the person can turn it off.</param>
/// <param name="LastCheck">When it last asked.</param>
public sealed record SitePace(string Site, int Checks, bool Careful, DateTimeOffset? LastCheck);

/// <summary>
/// The sites that have asked visitors to prove they are human, remembered on
/// this computer, so the next visit starts at a pace that keeps the checks away.
/// </summary>
/// <remarks>
/// <para>
/// Every site starts fast. Measured on a site that checks: a steady page
/// every 12 seconds from the start passed 75 pages with no check, while
/// every run that crowded about 50 pages into five minutes or less was
/// asked, whether those pages came as one fast stretch or as a burst of 25
/// followed by the slow pace. So the careful pace is steady: a page every
/// 12 seconds, with only the scan's few pages at the usual speed.
/// </para>
/// <para>
/// Learned, not listed: nothing about any particular site is built in. A
/// file that cannot be read or written is never a reason not to run.
/// </para>
/// </remarks>
public sealed class SitePaces(string? path)
{
    /// <summary>Pages at the usual speed before the careful pace begins: the scan's few, no more.</summary>
    public const int CarefulBurst = 5;

    /// <summary>One page per this, once the burst is spent.</summary>
    public static readonly TimeSpan CarefulEvery = TimeSpan.FromSeconds(12);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, SitePace> sites = Load(path);
    private readonly Lock saving = new();

    /// <summary>Raised when anything here changes, for a settings screen to refresh.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SitePace> All => [.. this.sites.Values.OrderBy(site => site.Site, StringComparer.Ordinal)];

    public bool IsCareful(string url) =>
        this.sites.TryGetValue(Robots.OriginOf(url), out var site) && site.Careful;

    /// <summary>A site asked for a check: remember it, and read it carefully from now on unless the person said otherwise.</summary>
    /// <returns>Whether the careful pace was newly switched on.</returns>
    public bool RecordCheck(string url, DateTimeOffset when)
    {
        var key = Robots.OriginOf(url);
        var switched = false;
        this.sites.AddOrUpdate(
            key,
            _ =>
            {
                switched = true;
                return new SitePace(key, 1, true, when);
            },
            (_, known) => known with { Checks = known.Checks + 1, LastCheck = when });
        this.Save();
        return switched;
    }

    /// <summary>The person's choice for one site: careful, or fast with the odd check to click.</summary>
    public void SetCareful(string site, bool careful)
    {
        if (this.sites.TryGetValue(site, out var known) && known.Careful != careful)
        {
            this.sites[site] = known with { Careful = careful };
            this.Save();
        }
    }

    /// <summary>Forget one site: it starts fast again.</summary>
    public void Forget(string site)
    {
        if (this.sites.TryRemove(site, out _))
        {
            this.Save();
        }
    }

    /// <summary>Put the careful pace on a site's limiter, or take it off.</summary>
    /// <param name="limiter">The site's limiter.</param>
    /// <param name="url">The site.</param>
    /// <param name="justChecked">The site has just asked, so its allowance is spent: no burst until it refills.</param>
    public void Apply(RateLimiter limiter, string url, bool justChecked = false)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        var site = new Uri(url);
        if (this.IsCareful(url))
        {
            limiter.SetHostAllowance(site, CarefulBurst, CarefulEvery, empty: justChecked);
        }
        else
        {
            limiter.ClearHostAllowance(site);
        }
    }

    private static ConcurrentDictionary<string, SitePace> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)
                && JsonSerializer.Deserialize<List<SitePace>>(File.ReadAllText(path), Options) is { } list)
            {
                return new(list.Where(site => !string.IsNullOrWhiteSpace(site.Site)).ToDictionary(site => site.Site, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start from nothing learned.
        }

        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        if (path is not null)
        {
            lock (this.saving)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(this.All, Options));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Remembered for this session only.
                }
            }
        }

        this.Changed?.Invoke(this, EventArgs.Empty);
    }
}
