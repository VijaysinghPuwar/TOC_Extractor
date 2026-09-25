using System.Diagnostics;
using Microsoft.Playwright;
using TocExtractor.Core.Links;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Browser;

/// <summary>The Playwright-backed page source.</summary>
/// <remarks>
/// <para>
/// The only type that touches a browser driver. Everything it can throw is
/// translated into the page-exception vocabulary before it leaves.
/// </para>
/// <para>
/// The route handling is more involved than a reader would expect, for a
/// measured reason: a route handler fires once per navigation, not once per
/// redirect hop. Chromium follows redirects internally, so a handler that only
/// inspects the first request never sees where the chain actually ended.
/// Neither fetching then fulfilling, nor observing requests, fixes it — the
/// first does not re-enter the handler, the second sees every hop but cannot
/// block. So the redirect loop lives in the handler: fetch with no redirects,
/// validate the Location target, repeat, and abort the moment a hop is
/// disallowed.
/// </para>
/// <para>
/// Two consequences fall out. The page's own idea of its URL becomes wrong,
/// because the final body is fulfilled at the originally requested URL and the
/// browser never learns a redirect happened — so the final URL is tracked in
/// the handler and reported from there. And only navigations are fulfilled:
/// proxying every subresource would make cookie, encoding and cache fidelity
/// this type's problem for no security gain, whereas screening and aborting
/// needs no proxying at all.
/// </para>
/// </remarks>
public sealed class BrowserPageSource : IPageSource
{
    private const int MaxRedirectHops = 20;

    private readonly UrlScreen screen;
    private readonly BrowserPageSourceOptions options;
    private readonly List<PageSlot> slots = [];
    private readonly SemaphoreSlim available;
    private readonly ConcurrentQueueOfSlots pool = new();

    private IPlaywright? playwright;
    private IBrowser? browser;
    private IBrowserContext? context;

    public BrowserPageSource(UrlGuard guard, BrowserPageSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(guard);

        this.options = options ?? new BrowserPageSourceOptions();
        this.screen = new UrlScreen(guard);
        this.available = new SemaphoreSlim(0);
    }

    public static async Task<BrowserPageSource> StartAsync(
        UrlGuard guard,
        BrowserPageSourceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var source = new BrowserPageSource(guard, options);
        try
        {
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
            return source;
        }
        catch
        {
            // Starting is not atomic: the driver process can be up before the
            // browser launch fails. Tearing down here is what stops that
            // process outliving the run. Python starts the source outside the
            // block that owns its disposal and leaks the driver on this path.
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        this.playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        var chromium = this.playwright.Chromium;

        if (this.options.UserDataDirectory is { } profile)
        {
            // A persistent profile, so a manual sign-in survives between runs.
            // Playwright returns a context directly, with no browser to close.
            this.context = await chromium.LaunchPersistentContextAsync(
                profile,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = this.options.Headless,
                    UserAgent = this.options.UserAgent,
                }).ConfigureAwait(false);
        }
        else
        {
            this.browser = await chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = this.options.Headless }).ConfigureAwait(false);
            this.context = await this.browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    UserAgent = this.options.UserAgent,
                    StorageStatePath = this.options.StorageStatePath,
                }).ConfigureAwait(false);
        }

        var budget = (float)this.options.OperationBudget.TotalMilliseconds;
        this.context.SetDefaultNavigationTimeout(budget);
        this.context.SetDefaultTimeout(budget);

        for (var i = 0; i < Math.Max(1, this.options.MaxPages); i++)
        {
            var page = await this.context.NewPageAsync().ConfigureAwait(false);
            var slot = new PageSlot(page);

            // Bound to its slot, so redirect state cannot be attributed to a
            // navigation happening on another page.
            await page.RouteAsync("**/*", route => this.HandleRouteAsync(slot, route))
                .ConfigureAwait(false);

            this.slots.Add(slot);
            this.pool.Add(slot);
            this.available.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var closable in new IAsyncDisposable?[] { this.context, this.browser })
        {
            if (closable is null)
            {
                continue;
            }

            try
            {
                await closable.DisposeAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // Teardown must not mask whatever caused it.
            }
        }

        if (this.playwright is not null)
        {
            this.playwright.Dispose();
        }

        this.playwright = null;
        this.browser = null;
        this.context = null;
        this.slots.Clear();
        this.available.Dispose();
    }

    // -- routing -------------------------------------------------------------

    private async Task HandleRouteAsync(PageSlot slot, IRoute route)
    {
        var request = route.Request;
        var verdict = this.screen.Check(request.Url);

        if (request.ResourceType != "document")
        {
            // Screened but never proxied. A scraped page carrying an image on a
            // private address would otherwise fire a blind request into the
            // user's network; aborting needs no fulfilment.
            if (verdict.Allowed)
            {
                await route.ContinueAsync().ConfigureAwait(false);
            }
            else
            {
                await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            }

            return;
        }

        var nav = slot.Navigation;
        if (!verdict.Allowed)
        {
            if (nav is not null)
            {
                nav.Blocked = Blocked(request.Url, verdict);
            }

            await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            return;
        }

        await this.FollowRedirectsAsync(route, request.Url, nav).ConfigureAwait(false);
    }

    private async Task FollowRedirectsAsync(IRoute route, string url, GuardedNavigation? nav)
    {
        for (var hop = 0; hop < MaxRedirectHops; hop++)
        {
            nav?.Hops.Add(url);

            IAPIResponse response;
            try
            {
                response = await route.FetchAsync(
                    new RouteFetchOptions { Url = url, MaxRedirects = 0 }).ConfigureAwait(false);
            }
            catch (PlaywrightException exception)
            {
                if (nav is not null)
                {
                    nav.Blocked = new PageBlockedException(
                        url, RejectionReason.Malformed, exception.Message);
                }

                await route.AbortAsync("failed").ConfigureAwait(false);
                return;
            }

            var location = response.Headers.TryGetValue("location", out var header) ? header : null;
            if (response.Status is >= 300 and < 400 && !string.IsNullOrEmpty(location))
            {
                url = new Uri(new Uri(url), location).AbsoluteUri;
                var verdict = this.screen.Check(url);
                if (!verdict.Allowed)
                {
                    // The case a pre-flight string check cannot catch: a
                    // permitted host redirecting somewhere never vetted.
                    if (nav is not null)
                    {
                        nav.Blocked = Blocked(url, verdict);
                    }

                    await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                    return;
                }

                continue;
            }

            if (nav is not null)
            {
                nav.FinalUrl = url;
            }

            await route.FulfillAsync(new RouteFulfillOptions { Response = response })
                .ConfigureAwait(false);
            return;
        }

        if (nav is not null)
        {
            nav.Blocked = new PageBlockedException(
                url, RejectionReason.Malformed, "too many redirects");
        }

        await route.AbortAsync("failed").ConfigureAwait(false);
    }

    /// <summary>
    /// The rejection, carrying the reason the screen recorded.
    /// </summary>
    /// <remarks>
    /// Python caches only whether a URL passed, so from the second sighting
    /// onward the reason is gone and this fallback fires for everything —
    /// a loopback address gets reported as a malformed URL.
    /// </remarks>
    private static PageBlockedException Blocked(string url, UrlVerdict verdict) =>
        new(url, verdict.Reason ?? RejectionReason.Malformed, verdict.Detail);

    // -- navigation ----------------------------------------------------------

    private async Task<PageSlot> AcquireAsync(CancellationToken cancellationToken)
    {
        if (this.context is null)
        {
            throw new PageException("page source is not started; call StartAsync first");
        }

        await this.available.WaitAsync(cancellationToken).ConfigureAwait(false);
        return this.pool.Take();
    }

    private void Release(PageSlot slot)
    {
        this.pool.Add(slot);
        this.available.Release();
    }

    private async Task<string> GotoAsync(PageSlot slot, string url, TimeSpan remaining)
    {
        var verdict = this.screen.Check(url);
        if (!verdict.Allowed)
        {
            throw Blocked(url, verdict);
        }

        var nav = new GuardedNavigation();
        slot.Navigation = nav;
        try
        {
            await slot.Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Milliseconds(remaining),
            }).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new PageTimeoutException($"{url}: navigation timed out", exception);
        }
        catch (PlaywrightException exception)
        {
            throw nav.Blocked ?? new PageException($"{url}: {exception.Message}", exception);
        }
        finally
        {
            slot.Navigation = null;
        }

        return nav.Blocked is not null ? throw nav.Blocked : nav.FinalUrl ?? url;
    }

    public async Task<bool> HasSessionCookiesAsync(CancellationToken cancellationToken = default)
    {
        if (this.context is null)
        {
            return false;
        }

        var cookies = await this.context.CookiesAsync().ConfigureAwait(false);
        return cookies.Count > 0;
    }

    public async Task<string> OpenPageAsync(string url, CancellationToken cancellationToken = default)
    {
        var slot = await this.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.GotoAsync(slot, url, this.options.OperationBudget).ConfigureAwait(false);
        }
        finally
        {
            this.Release(slot);
        }
    }

    public async Task<TocPage> LoadTocAsync(
        string url,
        string linkSelector,
        bool captureHtml = false,
        string? screenshotPath = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.StartNew();
        var slot = await this.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finalUrl = await this.GotoAsync(slot, url, this.Remaining(deadline)).ConfigureAwait(false);
            var page = slot.Page;

            var html = captureHtml ? await page.ContentAsync().ConfigureAwait(false) : null;

            if (screenshotPath is not null)
            {
                var directory = Path.GetDirectoryName(screenshotPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = true,
                }).ConfigureAwait(false);
            }

            var raw = await page.EvalOnSelectorAllAsync<object?[]>(linkSelector, LinkCollector.Script)
                .ConfigureAwait(false);

            return new TocPage(url, finalUrl, raw ?? [], html);
        }
        finally
        {
            this.Release(slot);
        }
    }

    public async Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        CancellationToken cancellationToken = default)
    {
        // One stopwatch for the whole operation. Navigation and both selector
        // reads draw down the same budget, which is what makes the caller's
        // deadline mean what it says. Python hands each of the three the full
        // configured value independently, so the outer cap always fires first
        // and the selector waits can never use the time they were given.
        var deadline = Stopwatch.StartNew();
        var slot = await this.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finalUrl = await this.GotoAsync(slot, url, this.Remaining(deadline)).ConfigureAwait(false);
            var title = await ReadFieldAsync(slot.Page, titleSelector, finalUrl, this.Remaining(deadline))
                .ConfigureAwait(false);
            var body = await ReadFieldAsync(slot.Page, contentSelector, finalUrl, this.Remaining(deadline))
                .ConfigureAwait(false);

            return new ChapterPage(url, finalUrl, title, body);
        }
        finally
        {
            this.Release(slot);
        }
    }

    private static async Task<string> ReadFieldAsync(
        IPage page, string selector, string url, TimeSpan remaining)
    {
        // A wait, not a bare read. On a hydrated page the element is
        // legitimately absent for a moment after the DOM is ready, and a bare
        // read would raise "not found" — which the fetch loop is told never to
        // retry. Without the wait, "do not retry" encodes a permanent verdict
        // on a transient condition.
        IElementHandle? element;
        try
        {
            element = await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                State = WaitForSelectorState.Attached,
                Timeout = Milliseconds(remaining),
            }).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new SelectorNotFoundException($"{selector} matched nothing on {url}", exception);
        }
        catch (PlaywrightException exception)
        {
            throw new PageException($"{url}: {exception.Message}", exception);
        }

        if (element is null)
        {
            throw new SelectorNotFoundException($"{selector} matched nothing on {url}");
        }

        try
        {
            return await element.InnerTextAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            throw new PageException($"{url}: reading {selector}: {exception.Message}", exception);
        }
    }

    private TimeSpan Remaining(Stopwatch deadline)
    {
        var left = this.options.OperationBudget - deadline.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    private static float Milliseconds(TimeSpan value) => (float)Math.Max(1, value.TotalMilliseconds);

    private sealed class GuardedNavigation
    {
        internal string? FinalUrl { get; set; }

        internal PageBlockedException? Blocked { get; set; }

        internal List<string> Hops { get; } = [];
    }

    /// <summary>One page plus the navigation currently in flight on it.</summary>
    /// <remarks>
    /// The fetch loop is concurrent, so the source must be too. Two navigations
    /// on one page abort each other, and a single shared navigation field would
    /// attribute one redirect chain to another. Both are per-slot for that
    /// reason.
    /// </remarks>
    private sealed class PageSlot(IPage page)
    {
        internal IPage Page { get; } = page;

        internal GuardedNavigation? Navigation { get; set; }
    }

    private sealed class ConcurrentQueueOfSlots
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<PageSlot> queue = new();

        internal void Add(PageSlot slot) => this.queue.Enqueue(slot);

        internal PageSlot Take() =>
            this.queue.TryDequeue(out var slot)
                ? slot
                : throw new PageException("page pool is empty");
    }
}
