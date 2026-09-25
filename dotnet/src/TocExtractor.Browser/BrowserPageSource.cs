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
public sealed partial class BrowserPageSource : IPageSource, IPageProbe, IHumanGate
{
    private const int MaxRedirectHops = 20;

    /// <summary>The least time a probe watches a page before trusting a stable answer.</summary>
    private static readonly TimeSpan MinimumSettle = TimeSpan.FromSeconds(1.5);

    /// <summary>The longest the contents page waits for its first chapter link.</summary>
    private static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(10);

    /// <summary>Whether a page is a site's "verify you are human" page rather than content.</summary>
    /// <remarks>
    /// The wording alone is not enough, since a chapter can mention a
    /// security check. It also has to be a short page, or carry an actual
    /// challenge widget, as every real check page does.
    /// </remarks>
    internal const string HumanCheckScript = """
        () => {
          const body = document.body ? document.body.innerText : '';
          const words = (document.title + ' ' + body.slice(0, 4000)).toLowerCase();
          const says = /just a moment|security check|verify you are (?:a )?human|checking your browser|abnormal activity|if you are human, click|are you a robot|attention required|try another captcha/.test(words);
          const widget = !!document.querySelector('iframe[src*="challenges.cloudflare.com"], .cf-turnstile, #challenge-form, #cf-challenge-running, .g-recaptcha, .h-captcha, [data-sitekey]');
          return says && (body.length < 6000 || widget);
        }
        """;

    /// <summary>Whether a chapter page shows only part of the chapter until the reader signs in.</summary>
    /// <remarks>
    /// Saving such a page would write half a chapter that looks complete.
    /// The wording has to sit next to something that signs in (a login link,
    /// button or form), so a story that mentions logging in does not count.
    /// </remarks>
    internal const string LockedScript = """
        () => {
          const says = new RegExp([
            'log ?in to (?:access|read|continue|unlock|view)',
            'sign in to (?:access|read|continue|unlock|view)',
            'unlock (?:this|the) chapter', 'this chapter is locked',
            'register to (?:read|continue)',
          ].join('|'), 'i');
          const signs = 'a[href*="login"], a[href*="signin"], a[href*="sign-in"], '
            + 'a[href*="auth"], button, form, input[type=password]';
          for (const el of document.querySelectorAll('div, section, p, span, h2, h3, h4')) {
            const text = (el.innerText || '').trim();
            if (text.length > 400 || !says.test(text)) continue;
            const box = el.closest('section, div') || el;
            if (box.querySelector(signs)) return true;
          }
          return false;
        }
        """;

    /// <summary>Reads a chapter's text the way a reader sees it.</summary>
    /// <remarks>
    /// Inside the content element, anything that is not the story is removed
    /// first: scripts, frames and ad slots, buttons, forms and navigation,
    /// share and comment widgets, and "next" or "previous" bars, which on real
    /// sites sit in the same container as the text. Only descendants are
    /// removed, never the element itself. Kept identical to READABLE_TEXT_JS
    /// in browser.py, so both implementations save the same text.
    /// </remarks>
    internal const string ReadableTextScript = """
        (element) => {
          const junk = [
            'script', 'style', 'noscript', 'template', 'iframe', 'frame', 'object',
            'embed', 'ins', 'video', 'audio', 'canvas', 'svg', 'button', 'select',
            'form', 'nav', 'aside', '[hidden]', '[aria-hidden="true"]',
            '[role="navigation"]', '[role="complementary"]',
          ].join(', ');
          const marker = new RegExp(
            '(?:^|[\\s_-])(?:ads?|adsbygoogle|advert\\w*|sponsor\\w*|banner|promo\\w*'
            + '|share|sharing|social|comments?|disqus|related|recommend\\w*|newsletter'
            + '|subscribe|popup|modal|cookie\\w*|chapter-?nav\\w*|nav|navigation'
            + '|pagination|pager|breadcrumbs?|toolbar|prev|next|notice|notif\\w*'
            + '|report\\w*|tips?|feedback|rating|donat\\w*)(?:[\\s_-]|$)',
            'i');
          for (const node of element.querySelectorAll(junk)) node.remove();
          for (const node of element.querySelectorAll('[class], [id]')) {
            const names = (node.getAttribute('class') || '') + ' '
              + (node.getAttribute('id') || '');
            if (marker.test(names)) node.remove();
          }
          // Zero-width characters some sites put between words.
          return element.innerText.replace(/[\u200B-\u200D\u2060\uFEFF]/g, '');
        }
        """;

    private readonly UrlScreen screen;
    private readonly BrowserPageSourceOptions options;
    private readonly List<PageSlot> slots = [];
    private readonly SemaphoreSlim available;
    private readonly ConcurrentQueueOfSlots pool = new();
    private readonly SemaphoreSlim growing = new(1, 1);

    private IPlaywright? playwright;
    private int people;
    private volatile bool closed;

    // The site being read, for telling its own cookies from ad networks'.
    private string? siteUrl;
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

        // Ad scripts on reading sites open pop-up tabs. While the app is
        // working on its own they are closed as they appear; while a person
        // is using the window (signing in, passing a check) nothing is touched.
        // Only pages another page opened count: the app's own worker pages
        // raise the same event and have no opener.
        this.context.Page += async (_, popup) =>
        {
            try
            {
                if (!this.PersonAtWindow && await popup.OpenerAsync().ConfigureAwait(false) is not null)
                {
                    await popup.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (PlaywrightException)
            {
                // Already gone.
            }
        };

        // The person can close the browser window; every later call would
        // then fail, so the owner needs to know to start a new one.
        this.context.Close += (_, _) => this.closed = true;

        var budget = (float)this.options.OperationBudget.TotalMilliseconds;
        this.context.SetDefaultNavigationTimeout(budget);
        this.context.SetDefaultTimeout(budget);

        for (var i = 0; i < Math.Max(1, this.options.MaxPages); i++)
        {
            await this.AddSlotAsync().ConfigureAwait(false);
        }
    }

    /// <summary>True once the browser has gone away, closed by the person or crashed.</summary>
    public bool IsClosed => this.closed || this.context is null;

    private async Task AddSlotAsync()
    {
        var slot = await this.NewSlotAsync().ConfigureAwait(false);
        lock (this.slots)
        {
            this.slots.Add(slot);
        }

        this.pool.Add(slot);
        this.available.Release();
    }

    private async Task<PageSlot> NewSlotAsync()
    {
        var page = await this.context!.NewPageAsync().ConfigureAwait(false);
        var slot = new PageSlot(page);
        page.Crash += (_, _) => slot.Crashed = true;

        // Bound to its slot, so redirect state cannot be attributed to a
        // navigation happening on another page.
        await page.RouteAsync("**/*", route => this.HandleRouteAsync(slot, route))
            .ConfigureAwait(false);
        return slot;
    }

    /// <summary>
    /// A fresh tab in place of one that was closed or crashed.
    /// </summary>
    /// <remarks>
    /// A tab can go away under the app: the person closes it, a site's script
    /// closes it, or it crashes. Handing the dead tab out again would fail
    /// every later chapter at once, so it is replaced before it is used.
    /// </remarks>
    private async Task<PageSlot> HealAsync(PageSlot slot)
    {
        if (!slot.Page.IsClosed && !slot.Crashed)
        {
            return slot;
        }

        if (this.IsClosed)
        {
            this.Release(slot);
            throw new PageException("the browser was closed");
        }

        PageSlot fresh;
        try
        {
            fresh = await this.NewSlotAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            this.Release(slot);
            throw new PageException($"the browser tab was closed and a new one could not be opened: {exception.Message}", exception);
        }

        if (!slot.Page.IsClosed)
        {
            try
            {
                await slot.Page.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // Crashed tabs can refuse to close; it is out of the pool either way.
            }
        }

        lock (this.slots)
        {
            var at = this.slots.IndexOf(slot);
            if (at >= 0)
            {
                this.slots[at] = fresh;
            }
            else
            {
                this.slots.Add(fresh);
            }
        }

        return fresh;
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
        lock (this.slots)
        {
            this.slots.Clear();
        }

        this.available.Dispose();
        this.growing.Dispose();
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

        // Every page busy: with room to grow, open another rather than wait,
        // so one book's work never queues behind another's.
        if (!await this.available.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await this.GrowAsync(cancellationToken).ConfigureAwait(false);
            await this.available.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await this.HealAsync(this.pool.Take()).ConfigureAwait(false);
    }

    private async Task GrowAsync(CancellationToken cancellationToken)
    {
        if (this.options.GrowTo is not { } limit)
        {
            return;
        }

        await this.growing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int open;
            lock (this.slots)
            {
                open = this.slots.Count;
            }

            // Another caller may have grown the pool while this one waited.
            if (open < limit && this.available.CurrentCount == 0 && !this.IsClosed)
            {
                try
                {
                    await this.AddSlotAsync().ConfigureAwait(false);
                }
                catch (PlaywrightException exception)
                {
                    throw new PageException($"could not open another browser tab: {exception.Message}", exception);
                }
            }
        }
        finally
        {
            this.growing.Release();
        }
    }

    private void Release(PageSlot slot)
    {
        this.pool.Add(slot);
        this.available.Release();
    }

    private async Task<string> GotoAsync(PageSlot slot, string url, TimeSpan remaining)
    {
        this.siteUrl ??= url;
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

    /// <summary>
    /// Open <paramref name="url"/> and run a detection script on it, waiting
    /// until the answer stops changing.
    /// </summary>
    /// <remarks>
    /// Used by the scanner, which has to see a page the way a reader does:
    /// chapter lists that arrive by a second request, content injected after
    /// load. The script is run every half second until two runs in a row
    /// agree, or <paramref name="settle"/> passes, so a list still filling in
    /// is not counted half way. The navigation goes through the same guard
    /// and redirect checks as every other load.
    /// </remarks>
    /// <returns>The final URL and the script's last answer, as JSON.</returns>
    public async Task<(string FinalUrl, string Json)> ProbeAsync(
        string url,
        string script,
        TimeSpan settle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);

        var deadline = Stopwatch.StartNew();
        var slot = await this.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var finalUrl = await this.GotoAsync(slot, url, this.Remaining(deadline)).ConfigureAwait(false);
            await ThrowIfHumanCheckAsync(slot.Page, url).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            string? previous = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current;
                try
                {
                    current = await slot.Page.EvaluateAsync<string>(script).ConfigureAwait(false);
                }
                catch (PlaywrightException exception)
                {
                    throw new PageException($"{finalUrl}: could not inspect the page: {exception.Message}", exception);
                }

                // A floor as well as a ceiling: two empty answers half a
                // second apart are not evidence the list is finished loading.
                var floor = settle < MinimumSettle ? settle : MinimumSettle;
                if ((current == previous && watch.Elapsed >= floor) || watch.Elapsed >= settle)
                {
                    return (finalUrl, current);
                }

                previous = current;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            this.Release(slot);
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForPersonAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (this.context is null)
        {
            return false;
        }

        this.PersonArrived();
        try
        {
            return await this.WaitForPersonCoreAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.PersonLeft();
        }
    }

    /// <summary>Looks in a row, two seconds apart, that a check must stay gone for.</summary>
    private const int ClearLooks = 3;

    private async Task<bool> WaitForPersonCoreAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (this.context is not { } context)
        {
            return false;
        }

        var watch = Stopwatch.StartNew();
        var shown = false;
        var clear = 0;
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocked = false;
            foreach (var page in context.Pages)
            {
                if (!await IsHumanCheckAsync(page).ConfigureAwait(false))
                {
                    continue;
                }

                blocked = true;
                if (!shown)
                {
                    // Put the check in front of the person, once.
                    await page.BringToFrontAsync().ConfigureAwait(false);
                    shown = true;
                }
            }

            // A check reloads itself after a click, so one clear look can be the
            // blink between two checks. Only a page that stays clear has passed.
            clear = blocked ? 0 : clear + 1;
            if (clear >= ClearLooks)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> IsLockedAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(LockedScript).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<bool> WaitForSignInAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        this.WaitForSignInAsync(this.siteUrl, timeout, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> WaitForSignInAsync(string? siteUrl, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        this.PersonArrived();
        try
        {
            var watch = Stopwatch.StartNew();
            if (this.context is { Pages.Count: > 0 } open)
            {
                // The tab showing that site, when several sites are open.
                var host = siteUrl is not null && Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) ? site.Host : null;
                var page = open.Pages.FirstOrDefault(p => host is not null && Uri.TryCreate(p.Url, UriKind.Absolute, out var at) && at.Host == host)
                    ?? open.Pages[0];
                await page.BringToFrontAsync().ConfigureAwait(false);
            }

            while (watch.Elapsed < timeout)
            {
                if (await this.HasSessionCookiesAsync(siteUrl, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            this.PersonLeft();
        }
    }

    private static async Task<bool> IsHumanCheckAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(HumanCheckScript).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Mid-navigation, which is what passing a check looks like.
            return false;
        }
    }

    private static async Task ThrowIfHumanCheckAsync(IPage page, string url)
    {
        if (await IsHumanCheckAsync(page).ConfigureAwait(false))
        {
            throw new HumanCheckException($"{url}: the site is asking to check that you are human");
        }
    }

    public Task<bool> HasSessionCookiesAsync(CancellationToken cancellationToken = default) =>
        this.HasSessionCookiesAsync(this.siteUrl, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> HasSessionCookiesAsync(string? siteUrl, CancellationToken cancellationToken = default)
    {
        if (this.context is null || siteUrl is null)
        {
            return false;
        }

        // Only the site's own cookies: ad networks set user-id cookies on
        // their own domains in the same browser, and those sign no one in.
        try
        {
            var cookies = await this.context.CookiesAsync([siteUrl]).ConfigureAwait(false);
            return cookies.Any(cookie => IsAccountCookie(cookie.Name));
        }
        catch (PlaywrightException)
        {
            // The browser was closed.
            return false;
        }
    }

    /// <summary>
    /// True while a person is using the browser window (signing in, passing a
    /// check). Pop-ups are left alone then; a sign-in can open one.
    /// </summary>
    /// <remarks>
    /// A count, not a flag: with several books saving at once, two can be
    /// waiting for the person together, and the first to finish must not
    /// declare the window free while the other still waits.
    /// </remarks>
    public bool PersonAtWindow => Volatile.Read(ref this.people) > 0;

    /// <summary>The person has started using the window.</summary>
    public void PersonArrived() => Interlocked.Increment(ref this.people);

    /// <summary>The person has finished with the window.</summary>
    public void PersonLeft()
    {
        // Never below zero, whatever order the callers finish in.
        var seen = Volatile.Read(ref this.people);
        while (seen > 0)
        {
            var was = Interlocked.CompareExchange(ref this.people, seen - 1, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>How many tabs are open, the app's own included. For tests.</summary>
    internal int OpenTabs => this.context?.Pages.Count ?? 0;

    /// <summary>Close every tab the app works in, as a person clicking their close buttons would. For tests.</summary>
    internal async Task CloseWorkTabsAsync()
    {
        List<PageSlot> open;
        lock (this.slots)
        {
            open = [.. this.slots];
        }

        foreach (var slot in open)
        {
            await slot.Page.CloseAsync().ConfigureAwait(false);
        }
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
            await ThrowIfHumanCheckAsync(page, url).ConfigureAwait(false);
            await WaitForLinksAsync(page, linkSelector, this.Remaining(deadline)).ConfigureAwait(false);

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

    public Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        CancellationToken cancellationToken = default) =>
        this.LoadChapterAsync(url, titleSelector, contentSelector, nextSelector: null, cancellationToken);

    public async Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        string? nextSelector,
        CancellationToken cancellationToken)
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
            await ThrowIfHumanCheckAsync(slot.Page, url).ConfigureAwait(false);
            var title = await ReadFieldAsync(slot.Page, titleSelector, finalUrl, this.Remaining(deadline))
                .ConfigureAwait(false);

            if (await IsLockedAsync(slot.Page).ConfigureAwait(false))
            {
                throw new HumanCheckException(
                    $"{url}: the site shows only part of this chapter unless you are signed in", needsSignIn: true);
            }

            // Before the body: reading the body removes navigation from the
            // page, and the next link is navigation.
            var next = nextSelector is null
                ? null
                : await NextLinkAsync(slot.Page, nextSelector).ConfigureAwait(false);

            var body = await ReadFieldAsync(slot.Page, contentSelector, finalUrl, this.Remaining(deadline), readable: true)
                .ConfigureAwait(false);

            return new ChapterPage(url, finalUrl, title, body, next);
        }
        finally
        {
            this.Release(slot);
        }
    }

    /// <summary>Where the page's next-chapter link points, or null if it has none.</summary>
    /// <remarks>
    /// The last chapter often keeps the button but points it nowhere useful:
    /// at "#", at "javascript:;", or back at the contents page. Only a real web
    /// address counts, and it is resolved against the page, as a click would be.
    /// </remarks>
    private static async Task<string?> NextLinkAsync(IPage page, string nextSelector)
    {
        try
        {
            var href = await page.EvaluateAsync<string?>(
                """
                selector => {
                  const link = document.querySelector(selector);
                  if (!link) return null;
                  const raw = link.getAttribute('href');
                  if (!raw || raw.startsWith('#') || raw.toLowerCase().startsWith('javascript:')) return null;
                  try { return new URL(raw, document.baseURI).href; } catch (e) { return null; }
                }
                """,
                nextSelector).ConfigureAwait(false);
            return href is not null && (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ? href
                : null;
        }
        catch (PlaywrightException)
        {
            // An invalid selector is the person's to fix; the preview says so.
            return null;
        }
    }

    private static async Task<string> ReadFieldAsync(
        IPage page, string selector, string url, TimeSpan remaining, bool readable = false)
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
            return readable
                ? await element.EvaluateAsync<string>(ReadableTextScript).ConfigureAwait(false)
                : FirstLine(await element.InnerTextAsync().ConfigureAwait(false));
        }
        catch (PlaywrightException exception)
        {
            throw new PageException($"{url}: reading {selector}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Give a chapter list that arrives after the page has loaded a moment to
    /// appear.
    /// </summary>
    /// <remarks>
    /// Many sites fetch their chapter list with a second request once the page
    /// is up, so reading links the instant navigation finishes finds none and
    /// reports an empty book. Chapter pages already wait for their selectors;
    /// the contents page now does the same. It is not an error if nothing
    /// arrives: a selector that matches nothing is reported as zero links, as
    /// before, just after a short wait rather than immediately.
    /// </remarks>
    private static async Task WaitForLinksAsync(IPage page, string linkSelector, TimeSpan remaining)
    {
        var wait = remaining < LinkWait ? remaining : LinkWait;
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await page.WaitForSelectorAsync(
                linkSelector,
                new PageWaitForSelectorOptions
                {
                    State = WaitForSelectorState.Attached,
                    Timeout = (float)wait.TotalMilliseconds,
                }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Nothing matched in time; the count below will say so.
        }
    }

    /// <summary>A cookie name that sign-in sets, as opposed to analytics or a visitor id.</summary>
    /// <remarks>
    /// Not "any cookie": nearly every site sets analytics, Cloudflare or
    /// visitor cookies before anyone signs in, and treating those as a sign-in
    /// would switch the robots.txt override on for everyone. Kept identical to
    /// is_account_cookie in browser.py.
    /// </remarks>
    internal static bool IsAccountCookie(string name) =>
        !AnonymousCookie().IsMatch(name) && AccountCookie().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex("[\u200B-\u200D\u2060\uFEFF]")]
    private static partial System.Text.RegularExpressions.Regex ZeroWidth();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:user_?id|userid|member|logged|login|auth|remember|access_?token|refresh_?token|jwt|wordpress_logged_in|dle_user_id|dle_password|xf_user|ips4_member_id|phpbb\d*_u|bb_userid|sessionid_account)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AccountCookie();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:_ga|_gid|_gat|_fbp|_ym|__cf|cf_|_cf|_pk|__utm|_hj|viewed|__stripe|phpsessid|laravel_session|ci_session|xsrf-token|csrftoken|__gads|__gpi|_clck|_clsk)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AnonymousCookie();

    /// <summary>A title is one line. Headings often carry the book name or a date under it.</summary>
    internal static string FirstLine(string text)
    {
        text = ZeroWidth().Replace(text, "");
        foreach (var line in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.Trim();
            }
        }

        return text.Trim();
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

        internal bool Crashed { get; set; }
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
