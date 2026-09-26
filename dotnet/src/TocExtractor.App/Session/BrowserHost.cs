using System.Collections.Concurrent;
using TocExtractor.Browser;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;

namespace TocExtractor.App.Session;

/// <summary>
/// The one browser every extraction in the app shares, and each site's pace.
/// </summary>
/// <remarks>
/// <para>
/// One browser, not one per extraction: the persistent profile that keeps a
/// sign-in can only be open in one browser at a time. Each extraction gets
/// its own tabs from it, opened as they are needed, so several books on
/// several sites are read at the same time without waiting for each other.
/// </para>
/// <para>
/// One pace per site, not per extraction: two books from the same site share
/// its rate limiter, so running them together never asks more of that site
/// than one would. Books on different sites do not slow each other down.
/// </para>
/// </remarks>
public sealed class BrowserHost(NovelEnvironment environment) : IAsyncDisposable
{
    /// <summary>The most tabs open at once, across every extraction.</summary>
    public const int MostTabs = 48;

    private readonly SemaphoreSlim starting = new(1, 1);
    private readonly ConcurrentDictionary<string, RateLimiter> limiters = new(StringComparer.OrdinalIgnoreCase);
    private IPageSource? source;
    private TimeSpan budget = TimeSpan.FromSeconds(25);
    private bool disposed;

    public NovelEnvironment Environment { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>Screens every address the browser is asked to open.</summary>
    public UrlGuard Guard { get; } = new(false, new CachingHostResolver(environment.Resolver ?? SystemHostResolver.Instance));

    /// <summary>Whether the browser is open now.</summary>
    public bool IsOpen => this.source is not null and not BrowserPageSource { IsClosed: true };

    /// <summary>
    /// The shared browser as every extraction sees it: each call goes to the
    /// browser open at that moment, so if the browser is closed mid-run the
    /// next page opens a new one and the run carries on.
    /// </summary>
    public async Task<IPageSource> SourceAsync(TimeSpan pageBudget, CancellationToken cancellationToken)
    {
        this.budget = pageBudget;
        await this.CurrentAsync(cancellationToken).ConfigureAwait(false);
        return new HostedSource(this);
    }

    /// <summary>The person has started using the browser window, to sign in.</summary>
    public void PersonArrived() => (this.source as BrowserPageSource)?.PersonArrived();

    /// <summary>The person has finished with the browser window.</summary>
    public void PersonLeft() => (this.source as BrowserPageSource)?.PersonLeft();

    /// <summary>The browser open now, started the first time it is asked for, and again if it was closed.</summary>
    internal async Task<IPageSource> CurrentAsync(CancellationToken cancellationToken)
    {
        var pageBudget = this.budget;
        await this.starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.source is BrowserPageSource { IsClosed: true } gone)
            {
                // The person closed the browser window. Start a fresh one.
                this.source = null;
                await gone.DisposeAsync().ConfigureAwait(false);
            }

            if (this.source is { } open)
            {
                return open;
            }

            Directory.CreateDirectory(this.Environment.BrowserProfileDirectory);
            var options = new BrowserPageSourceOptions
            {
                Headless = false,
                UserDataDirectory = this.Environment.BrowserProfileDirectory,
                OperationBudget = pageBudget,
                MaxPages = 1,
                GrowTo = MostTabs,
                TitleFallback = true,
            };

            try
            {
                this.source = await this.Environment.StartSource(this.Guard, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Microsoft.Playwright.PlaywrightException exception)
            {
                throw new SessionException(
                    exception.Message.Contains("ProcessSingleton", StringComparison.Ordinal)
                        ? "The browser from an earlier run is still open. Close it and try again."
                        : $"Could not start the browser: {exception.Message.Split('\n')[0]}",
                    exception);
            }

            return this.source;
        }
        finally
        {
            this.starting.Release();
        }
    }

    /// <summary>Raised when a site's pace is slowed after a check, with the site and the new wait between pages.</summary>
    public event EventHandler<(string Site, TimeSpan Interval)>? Slowed;

    private readonly ConcurrentDictionary<string, TimeSpan> slowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The wait between pages a site has been slowed to after a check, or null if it has not been.</summary>
    public TimeSpan? SlowedFor(string url) =>
        this.slowed.TryGetValue(Robots.OriginOf(url), out var interval)
            ? interval
            : this.Environment.SitePaces.IsCareful(url) ? SitePaces.CarefulEvery : null;

    /// <summary>Record that a site's pace was slowed, and tell every extraction reading it.</summary>
    public void MarkSlowed(string url, TimeSpan interval)
    {
        var site = Robots.OriginOf(url);
        this.slowed[site] = interval;
        this.Slowed?.Invoke(this, (site, interval));
    }

    /// <summary>
    /// The pace for one site, shared by every extraction reading it. A slower
    /// minimum asked for later raises it; a faster one never lowers it.
    /// </summary>
    public RateLimiter LimiterFor(string url, TimeSpan minDelay)
    {
        var site = new Uri(url);
        var limiter = this.limiters.GetOrAdd(Robots.OriginOf(url), _ => new RateLimiter(minDelay));

        // A site known to ask for checks starts careful; one the person set
        // back to fast does not.
        this.Environment.SitePaces.Apply(limiter, url);
        if (minDelay > limiter.IntervalFor(site))
        {
            limiter.SetHostInterval(site, minDelay);
        }

        return limiter;
    }

    public async Task CloseAsync()
    {
        await this.starting.WaitAsync().ConfigureAwait(false);
        try
        {
            var closing = this.source;
            this.source = null;
            if (closing is not null)
            {
                await closing.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            this.starting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        await this.CloseAsync().ConfigureAwait(false);
        this.disposed = true;
        this.starting.Dispose();
    }

    /// <summary>Forwards every call to whichever browser is open when it is made.</summary>
    private sealed class HostedSource(BrowserHost host) : IPageSource, IPageProbe, IHumanGate
    {
        public async Task<string> OpenPageAsync(string url, CancellationToken cancellationToken = default) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false)).OpenPageAsync(url, cancellationToken).ConfigureAwait(false);

        public async Task<bool> HasSessionCookiesAsync(CancellationToken cancellationToken = default) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false)).HasSessionCookiesAsync(cancellationToken).ConfigureAwait(false);

        public async Task<bool> HasSessionCookiesAsync(string? siteUrl, CancellationToken cancellationToken = default) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false)).HasSessionCookiesAsync(siteUrl, cancellationToken).ConfigureAwait(false);

        public async Task<TocPage> LoadTocAsync(
            string url,
            string linkSelector,
            bool captureHtml = false,
            string? screenshotPath = null,
            CancellationToken cancellationToken = default) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false))
                .LoadTocAsync(url, linkSelector, captureHtml, screenshotPath, cancellationToken).ConfigureAwait(false);

        public async Task<ChapterPage> LoadChapterAsync(
            string url,
            string titleSelector,
            string contentSelector,
            CancellationToken cancellationToken = default) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false))
                .LoadChapterAsync(url, titleSelector, contentSelector, cancellationToken).ConfigureAwait(false);

        public async Task<ChapterPage> LoadChapterAsync(
            string url,
            string titleSelector,
            string contentSelector,
            string nextSelector,
            CancellationToken cancellationToken) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false))
                .LoadChapterAsync(url, titleSelector, contentSelector, nextSelector, cancellationToken).ConfigureAwait(false);

        public async Task<(string FinalUrl, string Json)> ProbeAsync(
            string url,
            string script,
            TimeSpan settle,
            CancellationToken cancellationToken = default) =>
            await (await this.CurrentAsync(cancellationToken).ConfigureAwait(false) is IPageProbe probe
                    ? probe
                    : throw new InvalidOperationException("this browser cannot inspect pages"))
                .ProbeAsync(url, script, settle, cancellationToken).ConfigureAwait(false);

        public async Task<bool> WaitForPersonAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            await this.CurrentAsync(cancellationToken).ConfigureAwait(false) is IHumanGate gate
            && await gate.WaitForPersonAsync(timeout, cancellationToken).ConfigureAwait(false);

        public async Task<bool> WaitForSignInAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            await this.CurrentAsync(cancellationToken).ConfigureAwait(false) is IHumanGate gate
            && await gate.WaitForSignInAsync(timeout, cancellationToken).ConfigureAwait(false);

        public async Task<bool> WaitForSignInAsync(string? siteUrl, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            await this.CurrentAsync(cancellationToken).ConfigureAwait(false) is IHumanGate gate
            && await gate.WaitForSignInAsync(siteUrl, timeout, cancellationToken).ConfigureAwait(false);

        public async Task<bool> ShowCheckAsync() =>
            await this.CurrentAsync(CancellationToken.None).ConfigureAwait(false) is IHumanGate gate
            && await gate.ShowCheckAsync().ConfigureAwait(false);

        /// <summary>Nothing to release: the host owns the browser.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async Task<IPageSource> CurrentAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await host.CurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SessionException exception)
            {
                // Inside a run, a browser that cannot be reopened is a page
                // failure, which the fetch loop records and retries.
                throw new PageException(exception.Message, exception);
            }
        }
    }
}
