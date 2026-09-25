"""The Playwright-backed PageSource.

The only module that imports Playwright. Everything it can raise is
translated into the pagesource vocabulary before it leaves.

The route handling here is more involved than a reader would expect, for a
measured reason. `page.route` fires **once per navigation**, not once per
redirect hop: Chromium follows redirects internally, so a handler that only
inspects the first request never sees where the chain actually ended. Neither
`route.fetch(...)` followed by `route.fulfill(...)` nor `page.on("request")`
fixes it — the former does not re-enter the handler, the latter observes every
hop but cannot block. So the redirect loop lives in the handler: fetch with
`max_redirects=0`, validate the Location target, repeat, and abort the moment
a hop is disallowed.

Two consequences fall out of that:

- `page.url` becomes wrong. The final body is fulfilled at the originally
  requested URL, so the browser never learns a redirect happened. The final
  URL is tracked in the handler and reported from there.
- Only navigations are fulfilled. Proxying every subresource would make
  cookie, encoding, and cache fidelity our problem for no security gain.
  Non-navigation requests are screened and aborted if disallowed, which needs
  no proxying at all.
"""

from __future__ import annotations

import asyncio
import contextlib
import re
from collections.abc import AsyncIterator, Sequence
from contextlib import asynccontextmanager
from functools import partial
from pathlib import Path
from types import TracebackType
from typing import Any
from urllib.parse import urljoin

from playwright.async_api import Browser, BrowserContext, Page, Route, async_playwright
from playwright.async_api import Error as PlaywrightError
from playwright.async_api import TimeoutError as PlaywrightTimeout

from .logging import get_logger
from .pagesource import (
    ChapterPage,
    PageBlocked,
    PageError,
    PageTimeout,
    SelectorNotFound,
    TocPage,
)
from .parser import LINK_COLLECTOR_JS
from .politeness import RejectionReason, UrlGuard

MAX_REDIRECT_HOPS = 20
# The longest the contents page waits for its first chapter link.
LINK_WAIT_MS = 10_000

# Reads a chapter's text the way a reader sees it: inside the content element,
# anything that is not the story is removed first. Scripts, frames and ad slots,
# buttons, forms and navigation, share and comment widgets, and "next" or
# "previous" bars all tend to sit inside the same container as the text on
# real sites, so a content selector alone brings them along. Only descendants
# are removed, never the element itself. Kept identical to READABLE_TEXT_JS in
# BrowserPageSource.cs, so both implementations save the same text.
READABLE_TEXT_JS = r"""(element) => {
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
}"""

log = get_logger("browser")


class _GuardedNavigation:
    """Tracks one navigation's redirect chain and the verdict on each hop."""

    def __init__(self) -> None:
        self.final_url: str | None = None
        self.blocked: PageBlocked | None = None
        self.hops: list[str] = []


class _PageSlot:
    """One page plus the navigation currently in flight on it.

    The fetch loop is concurrent, so the source must be too. Two goto() calls
    on one page abort each other - net::ERR_ABORTED, measured against a live
    server - and a single shared `nav` attribute would attribute one
    navigation's redirect chain to another. Both are per-slot for that reason.
    """

    def __init__(self, page: Page) -> None:
        self.page = page
        self.nav: _GuardedNavigation | None = None


class BrowserPageSource:
    """Loads pages in a real Chromium context, with the URL guard at the request layer."""

    def __init__(
        self,
        *,
        guard: UrlGuard,
        headless: bool = True,
        user_agent: str | None = None,
        storage_state: Path | None = None,
        user_data_dir: Path | None = None,
        navigation_timeout_ms: int = 25_000,
        max_pages: int = 1,
    ) -> None:
        self._guard = guard
        self._headless = headless
        self._user_agent = user_agent
        self._storage_state = storage_state
        self._user_data_dir = user_data_dir
        self._navigation_timeout_ms = navigation_timeout_ms
        self._max_pages = max(1, max_pages)

        self._playwright: Any = None
        self._browser: Browser | None = None
        self._context: BrowserContext | None = None
        self._pool: asyncio.Queue[_PageSlot] | None = None
        self._slots: list[_PageSlot] = []
        # One resolution per host per run. A page of forty images must not mean
        # forty DNS lookups in the screening path.
        self._verdicts: dict[str, bool] = {}

    async def __aenter__(self) -> BrowserPageSource:
        await self.start()
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        tb: TracebackType | None,
    ) -> None:
        await self.aclose()

    async def start(self) -> None:
        self._playwright = await async_playwright().start()
        chromium = self._playwright.chromium

        if self._user_data_dir is not None:
            # Persistent profile: what the GUI uses so a manual login survives
            # between runs. Playwright returns a context directly, with no
            # separate Browser object to close.
            self._context = await chromium.launch_persistent_context(
                user_data_dir=str(self._user_data_dir),
                headless=self._headless,
                user_agent=self._user_agent,
            )
        else:
            self._browser = await chromium.launch(headless=self._headless)
            self._context = await self._browser.new_context(
                user_agent=self._user_agent,
                storage_state=str(self._storage_state) if self._storage_state else None,
            )

        self._context.set_default_navigation_timeout(self._navigation_timeout_ms)
        self._context.set_default_timeout(self._navigation_timeout_ms)

        self._pool = asyncio.Queue()
        for _ in range(self._max_pages):
            page = await self._context.new_page()
            slot = _PageSlot(page)
            # The handler is bound to its slot so redirect state cannot be
            # attributed to a navigation happening on another page.
            await page.route("**/*", partial(self._handle_route, slot))
            self._slots.append(slot)
            self._pool.put_nowait(slot)

    async def aclose(self) -> None:
        for closer in (self._context, self._browser):
            if closer is not None:
                # Teardown must not mask the error that caused it, but a
                # silently broken context is worth a line at debug.
                try:
                    await closer.close()
                except PlaywrightError as exc:
                    log.debug("ignoring error while closing %s: %s", type(closer).__name__, exc)
        if self._playwright is not None:
            await self._playwright.stop()
        self._playwright = None
        self._browser = None
        self._context = None
        self._pool = None
        self._slots = []

    # -- routing ------------------------------------------------------------

    def _screen(self, url: str) -> tuple[bool, RejectionReason | None, str]:
        cached = self._verdicts.get(url)
        if cached is not None:
            return cached, None, ""
        verdict = self._guard.check(url)
        self._verdicts[url] = verdict.allowed
        return verdict.allowed, verdict.reason, verdict.detail

    async def _handle_route(self, slot: _PageSlot, route: Route) -> None:
        request = route.request
        allowed, reason, detail = self._screen(request.url)

        if request.resource_type != "document":
            # Screen but never proxy. A scraped page carrying
            # <img src="http://192.168.1.1/..."> would otherwise fire a blind
            # request into the user's LAN; aborting needs no fulfilment.
            if allowed:
                await route.continue_()
            else:
                await route.abort("blockedbyclient")
            return

        nav = slot.nav
        if not allowed:
            if nav is not None:
                nav.blocked = PageBlocked(request.url, reason or RejectionReason.MALFORMED, detail)
            await route.abort("blockedbyclient")
            return

        await self._follow_redirects(route, request.url, nav)

    async def _follow_redirects(
        self, route: Route, url: str, nav: _GuardedNavigation | None
    ) -> None:
        for _ in range(MAX_REDIRECT_HOPS):
            if nav is not None:
                nav.hops.append(url)
            try:
                response = await route.fetch(url=url, max_redirects=0)
            except PlaywrightError as exc:
                if nav is not None:
                    nav.blocked = PageBlocked(url, RejectionReason.MALFORMED, str(exc))
                await route.abort("failed")
                return

            location = response.headers.get("location")
            if 300 <= response.status < 400 and location:
                url = urljoin(url, location)
                allowed, reason, detail = self._screen(url)
                if not allowed:
                    # The case a pre-flight string check cannot catch: a
                    # permitted host redirecting somewhere that was never vetted.
                    if nav is not None:
                        nav.blocked = PageBlocked(url, reason or RejectionReason.MALFORMED, detail)
                    await route.abort("blockedbyclient")
                    return
                continue

            if nav is not None:
                nav.final_url = url
            await route.fulfill(response=response)
            return

        if nav is not None:
            nav.blocked = PageBlocked(url, RejectionReason.MALFORMED, "too many redirects")
        await route.abort("failed")

    # -- navigation ---------------------------------------------------------

    @asynccontextmanager
    async def _acquire(self) -> AsyncIterator[_PageSlot]:
        if self._pool is None:
            raise PageError("page source is not started; call start() first")
        slot = await self._pool.get()
        try:
            yield slot
        finally:
            self._pool.put_nowait(slot)

    async def _goto(self, slot: _PageSlot, url: str) -> str:
        allowed, reason, detail = self._screen(url)
        if not allowed:
            raise PageBlocked(url, reason or RejectionReason.MALFORMED, detail)

        nav = _GuardedNavigation()
        slot.nav = nav
        try:
            await slot.page.goto(url, wait_until="domcontentloaded")
        except PlaywrightTimeout as exc:
            raise PageTimeout(f"{url}: navigation timed out") from exc
        except PlaywrightError as exc:
            if nav.blocked is not None:
                raise nav.blocked from exc
            raise PageError(f"{url}: {exc}") from exc
        finally:
            slot.nav = None

        if nav.blocked is not None:
            raise nav.blocked
        return nav.final_url or url

    # -- PageSource ---------------------------------------------------------

    async def has_session_cookies(self) -> bool:
        """Whether the browser carries a cookie that belongs to an account.

        Not "any cookie": nearly every site sets analytics, Cloudflare or
        visitor cookies before anyone signs in, and treating those as a
        sign-in would switch the robots.txt override on for everyone. The
        names are checked for what sign-in cookies are called on common
        platforms, and analytics and anonymous session ids are ignored.
        Kept identical to BrowserPageSource.IsAccountCookie in C#.
        """
        if self._context is None:
            return False
        return any(is_account_cookie(cookie["name"]) for cookie in await self._context.cookies())

    async def open_page(self, url: str) -> str:
        async with self._acquire() as slot:
            return await self._goto(slot, url)

    async def load_toc(
        self,
        url: str,
        *,
        link_selector: str,
        capture_html: bool = False,
        screenshot_path: Path | None = None,
    ) -> TocPage:
        async with self._acquire() as slot:
            final_url = await self._goto(slot, url)
            page = slot.page
            await self._wait_for_links(page, link_selector)

            html = await page.content() if capture_html else None
            if screenshot_path is not None:
                screenshot_path.parent.mkdir(parents=True, exist_ok=True)
                await page.screenshot(path=str(screenshot_path), full_page=True)

            raw: Sequence[object] = await page.eval_on_selector_all(
                link_selector, LINK_COLLECTOR_JS
            )
        return TocPage(
            requested_url=url,
            final_url=final_url,
            raw_links=tuple(raw),
            html=html,
        )

    async def _wait_for_links(self, page: Page, link_selector: str) -> None:
        # Many sites fetch the chapter list with a second request after the
        # page has loaded, so reading links the moment navigation ends finds
        # none. Chapter pages already wait for their selectors; the contents
        # page gets a bounded wait too. Nothing arriving is not an error: the
        # selector then reports zero links, as it always has.
        with contextlib.suppress(PlaywrightTimeout):
            await page.wait_for_selector(
                link_selector,
                state="attached",
                timeout=min(LINK_WAIT_MS, self._navigation_timeout_ms),
            )

    async def load_chapter(
        self,
        url: str,
        *,
        title_selector: str,
        content_selector: str,
    ) -> ChapterPage:
        async with self._acquire() as slot:
            final_url = await self._goto(slot, url)
            title = await self._read_field(slot.page, title_selector, final_url)
            body = await self._read_field(slot.page, content_selector, final_url, readable=True)
        return ChapterPage(
            requested_url=url,
            final_url=final_url,
            title=title,
            body=body,
        )

    async def _read_field(
        self, page: Page, selector: str, url: str, *, readable: bool = False
    ) -> str:
        # wait_for_selector, not a bare read. On a JS-hydrated page the element
        # is legitimately absent for a moment after domcontentloaded, and a
        # bare read would raise SelectorNotFound - which the fetch loop is
        # explicitly told never to retry. Without the wait, "do not retry"
        # would be encoding a permanent verdict on a transient condition.
        try:
            element = await page.wait_for_selector(selector, state="attached")
        except PlaywrightTimeout as exc:
            raise SelectorNotFound(f"{selector} matched nothing on {url}") from exc
        except PlaywrightError as exc:
            raise PageError(f"{url}: {exc}") from exc

        if element is None:
            raise SelectorNotFound(f"{selector} matched nothing on {url}")
        try:
            if readable:
                text: str = await element.evaluate(READABLE_TEXT_JS)
                return text
            return first_line(await element.inner_text())
        except PlaywrightError as exc:
            raise PageError(f"{url}: reading {selector}: {exc}") from exc


async def open_browser_source(**kwargs: Any) -> BrowserPageSource:
    """Construct and start a BrowserPageSource."""
    source = BrowserPageSource(**kwargs)
    await source.start()
    return source


__all__ = ["BrowserPageSource", "open_browser_source"]


def first_line(text: str) -> str:
    """A title is one line. Headings often carry the book name or a date under it."""
    visible = re.sub("[\u200b-\u200d\u2060\ufeff]", "", text)
    for line in visible.splitlines():
        if line.strip():
            return line.strip()
    return visible.strip()


_ACCOUNT_COOKIE = re.compile(
    r"(?:user_?id|userid|member|logged|login|auth|remember|access_?token|refresh_?token|jwt"
    r"|wordpress_logged_in|dle_user_id|dle_password|xf_user|ips4_member_id|phpbb\d*_u"
    r"|bb_userid|sessionid_account)",
    re.IGNORECASE,
)
_ANONYMOUS_COOKIE = re.compile(
    r"^(?:_ga|_gid|_gat|_fbp|_ym|__cf|cf_|_cf|_pk|__utm|_hj|viewed|__stripe|phpsessid"
    r"|laravel_session|ci_session|xsrf-token|csrftoken|__gads|__gpi|_clck|_clsk)",
    re.IGNORECASE,
)


def is_account_cookie(name: str) -> bool:
    """A cookie name that sign-in sets, as opposed to analytics or a visitor id."""
    return not _ANONYMOUS_COOKIE.match(name) and bool(_ACCOUNT_COOKIE.search(name))
