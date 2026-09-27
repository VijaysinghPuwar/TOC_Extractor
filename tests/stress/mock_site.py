"""A local stand-in for the sites the extractor is pointed at.

Why this exists rather than pointing the load test at the real sites: forty
processes fetching fifty chapters is two thousand requests, and aimed at
someone else's server that is a denial of service, not a test. It would also
measure the wrong thing. The number that comes back would be their rate
limiter and their Cloudflare rules, not this program's behaviour under load,
and it would not be reproducible from one run to the next.

So the shapes are copied and the prose is not. Each archetype below mirrors
the DOM of one real site that was surveyed - where the chapter links sit, how
deeply they nest, whether they arrive with the document or after it - and
fills it with generated filler. That is what the extractor is actually
exercised against: selector depth, hydration timing, redirects, flaky
responses, and page weight.

Run it alone to look at it:

    python tests/stress/mock_site.py --port 8900 --books 4

Every knob has a default that makes the server behave, so a test that wants
one failure mode turns on that one.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import sys
import threading
import time
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

# Word pool for generated chapter prose. Deliberately meaningless: the point
# is byte count and paragraph structure, not readable text.
WORDS = [
    "amber",
    "beacon",
    "cinder",
    "drift",
    "ember",
    "fathom",
    "glimmer",
    "hollow",
    "ivory",
    "kindle",
    "lantern",
    "marrow",
    "nimbus",
    "onyx",
    "pallid",
    "quarry",
    "rusted",
    "silver",
    "tundra",
    "umber",
    "vellum",
    "whisper",
    "yonder",
    "zenith",
    "arbor",
    "bramble",
    "cavern",
    "dusk",
    "elder",
    "fissure",
    "grotto",
    "harrow",
]


@dataclass(frozen=True, slots=True)
class Archetype:
    """One site's DOM shape: how its TOC nests links and where chapter text sits."""

    name: str
    link_selector: str
    title_selector: str
    content_selector: str
    # Rendered around the <a> list. {links} is substituted.
    toc_template: str
    chapter_template: str
    # Links injected by script after load, as a real single-page TOC does.
    hydrated: bool = False


# Four shapes, each traced from a site in the survey. The selectors are the
# ones a person would pass on the command line for that site, so the load test
# exercises the same selector depth the real thing does.
ARCHETYPES: tuple[Archetype, ...] = (
    # Deeply nested list inside a scroll pane: a real site's shape.
    Archetype(
        name="deep-nested",
        link_selector=".cat_line a",
        title_selector="h1",
        content_selector="#arrticle",
        toc_template="""<div id="dle-content"><div class="r-fullstory-chapters">
<div class="chapters-scroll"><div class="chapters-scroll-in scroll-pane">
<ul class="chapters-scroll-list">{links}</ul></div></div></div></div>""",
        chapter_template="""<div class="page"><div class="body"><div class="body_right">
<h1>{title}</h1><div id="arrticle">{body}</div>
<div class="chapter-nav"><a href="#">Previous</a><a href="#">Next</a></div>
</div></div></div>""",
    ),
    # Flat paged list: the shape of two real sites.
    Archetype(
        name="paged-list",
        link_selector="#chpagedlist ul.chapter-list li a",
        title_selector=".chapter-title",
        content_selector="#content",
        toc_template="""<article id="novel"><div id="chpagedlist">
<ul class="chapter-list">{links}</ul></div></article>""",
        chapter_template="""<main><div id="chapter-article"><div class="titles">
<h1>Book Title</h1><span class="chapter-title">{title}</span></div>
<div id="chapter-container"><div id="content">{body}</div></div>
<div class="recommends"><a href="#">More like this</a></div></div></main>""",
    ),
    # Card list whose links arrive after load: the hydrated single-page TOC.
    Archetype(
        name="hydrated-card",
        link_selector=".m-card a.chapter-item",
        title_selector="h3",
        content_selector=".content.text-break",
        toc_template="""<div class="container"><div class="m-card"><div class="row">
<div class="col-12 col-md-8" id="chapter-host"></div></div></div></div>
<script id="chapter-data" type="application/json">{links_json}</script>""",
        chapter_template="""<div class="container"><div class="m-card">
<h3>{title}</h3><div class="content text-break">{body}</div></div></div>""",
        hydrated=True,
    ),
    # Table rows, the other common list shape.
    Archetype(
        name="table-rows",
        link_selector="table.chapters tbody tr td a",
        title_selector="h1.entry-title",
        content_selector="div.entry-content",
        toc_template="""<section class="toc"><table class="chapters">
<tbody>{links}</tbody></table></section>""",
        chapter_template="""<article><h1 class="entry-title">{title}</h1>
<div class="entry-content">{body}</div></article>""",
    ),
)


def _link_markup(archetype: Archetype, book: int, chapters: int) -> str:
    """The chapter links, wrapped the way this archetype nests them."""
    hrefs = [(f"/book/{book}/chapter/{n}", f"Chapter {n}") for n in range(1, chapters + 1)]

    if archetype.hydrated:
        return json.dumps([{"href": h, "text": t} for h, t in hrefs])

    if archetype.name == "table-rows":
        return "".join(
            f'<tr><td><a href="{h}">{t}</a></td><td>2026-01-01</td></tr>' for h, t in hrefs
        )
    if archetype.name == "deep-nested":
        return "".join(f'<li class="cat_line"><a href="{h}">{t}</a></li>' for h, t in hrefs)
    return "".join(f'<li><a href="{h}">{t}</a></li>' for h, t in hrefs)


HYDRATE_SCRIPT = """
<script>
(function () {
  var delay = %d;
  var data = JSON.parse(document.getElementById('chapter-data').textContent);
  setTimeout(function () {
    var host = document.getElementById('chapter-host');
    var frag = document.createDocumentFragment();
    data.forEach(function (item) {
      var a = document.createElement('a');
      a.className = 'chapter-item';
      a.href = item.href;
      a.textContent = item.text;
      frag.appendChild(a);
    });
    host.appendChild(frag);
  }, delay);
})();
</script>
"""

# Enough CSS to make Chromium lay out a real page rather than a bare string.
PAGE_CSS = """
body{font-family:Georgia,serif;margin:0;background:#faf8f5;color:#222}
.page,.container,main,article{max-width:52rem;margin:0 auto;padding:1.5rem}
h1,h3{line-height:1.2}
#arrticle p,#content p,.content p,.entry-content p{line-height:1.7;margin:0 0 1em}
.chapter-nav,.recommends{margin-top:2rem;opacity:.7}
"""


def _chapter_body(book: int, chapter: int, paragraphs: int, words_per: int) -> str:
    """Generated prose, deterministic per (book, chapter) so runs are comparable."""
    seed = int(hashlib.sha256(f"{book}:{chapter}".encode()).hexdigest()[:8], 16)
    rng = random.Random(seed)
    out = []
    for _ in range(paragraphs):
        words = [rng.choice(WORDS) for _ in range(words_per)]
        words[0] = words[0].capitalize()
        out.append("<p>" + " ".join(words) + ".</p>")
    return "".join(out)


def _png(width: int, height: int) -> bytes:
    """A real PNG, built without a dependency.

    Reading sites carry cover art, ad slots and tracking pixels, and Chromium
    decodes each one into memory. A load test served only text would make
    skipping images look free when the whole point is that it is not.
    """
    import struct
    import zlib

    raw = b"".join(
        b"\x00" + bytes((x * 7 + y * 3) % 256 for x in range(width * 3)) for y in range(height)
    )

    def chunk(kind: bytes, payload: bytes) -> bytes:
        body = kind + payload
        return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 6))
        + chunk(b"IEND", b"")
    )


# Built once: the same bytes every run keeps the comparison honest.
COVER_PNG = _png(600, 900)
BANNER_PNG = _png(728, 90)

# What a chapter page on a reading site actually carries alongside the prose.
CHAPTER_FURNITURE = (
    '<img src="/img/cover.png" alt="" width="200">'
    '<img src="/img/banner.png" alt="" width="728">'
    '<img src="/img/banner.png?slot=2" alt="" width="728">'
)


def _document(title: str, body: str, extra_head: str = "") -> bytes:
    return (
        "<!doctype html><html lang='en'><head><meta charset='utf-8'>"
        f"<title>{title}</title><style>{PAGE_CSS}</style>{extra_head}</head>"
        f"<body>{body}</body></html>"
    ).encode()


@dataclass
class SiteConfig:
    books: int = 4
    chapters: int = 60
    paragraphs: int = 40
    words_per_paragraph: int = 55
    latency_ms: int = 0
    jitter_ms: int = 0
    hydrate_ms: int = 350
    # Fraction of chapter requests answered 503, to exercise the retry path.
    fail_rate: float = 0.0
    # Fraction answered with a redirect to the real URL, for the redirect loop.
    redirect_rate: float = 0.0
    robots: str = "User-agent: *\nAllow: /\n"


class Handler(BaseHTTPRequestHandler):
    config: SiteConfig
    counters: dict[str, int]
    counter_lock: threading.Lock

    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: object) -> None:
        """Silence. A load test's console must show the load test, not the server."""

    def _count(self, key: str) -> None:
        with self.counter_lock:
            self.counters[key] = self.counters.get(key, 0) + 1

    def _delay(self) -> None:
        config = self.config
        if config.latency_ms or config.jitter_ms:
            jitter = random.uniform(0, config.jitter_ms) if config.jitter_ms else 0.0
            time.sleep((config.latency_ms + jitter) / 1000.0)

    def _send(self, body: bytes, status: int = 200, content_type: str = "text/html") -> None:
        self.send_response(status)
        self.send_header("Content-Type", f"{content_type}; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            # A worker that gave up mid-response is a normal load-test event.
            self._count("broken_pipe")

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        path = parsed.path
        config = self.config

        if path.startswith("/img/"):
            self._count("image")
            payload = COVER_PNG if "cover" in path else BANNER_PNG
            self._send(payload, content_type="image/png")
            return

        if path == "/robots.txt":
            self._count("robots")
            self._send(config.robots.encode(), content_type="text/plain")
            return

        if path == "/stats":
            with self.counter_lock:
                snapshot = dict(self.counters)
            self._send(json.dumps(snapshot).encode(), content_type="application/json")
            return

        parts = [p for p in path.split("/") if p]

        # /book/<n>/toc
        if len(parts) == 3 and parts[0] == "book" and parts[2] == "toc":
            self._count("toc")
            self._delay()
            book = int(parts[1])
            archetype = ARCHETYPES[book % len(ARCHETYPES)]
            chapters = int(parse_qs(parsed.query).get("chapters", [config.chapters])[0])
            links = _link_markup(archetype, book, chapters)
            if archetype.hydrated:
                html = archetype.toc_template.format(links_json=links)
                html += HYDRATE_SCRIPT % config.hydrate_ms
            else:
                html = archetype.toc_template.format(links=links)
            self._send(_document(f"Book {book} contents", html))
            return

        # /book/<n>/chapter/<m>
        if len(parts) == 4 and parts[0] == "book" and parts[2] == "chapter":
            book, chapter = int(parts[1]), int(parts[3])
            archetype = ARCHETYPES[book % len(ARCHETYPES)]

            if config.fail_rate and random.random() < config.fail_rate:
                self._count("injected_503")
                self._send(b"<html><body>busy</body></html>", status=503)
                return

            if config.redirect_rate and random.random() < config.redirect_rate:
                target = f"/book/{book}/final/{chapter}"
                self._count("redirect")
                self.send_response(302)
                self.send_header("Location", target)
                self.send_header("Content-Length", "0")
                self.end_headers()
                return

            self._count("chapter")
            self._delay()
            self._send(self._chapter_document(archetype, book, chapter))
            return

        # /book/<n>/final/<m> - where a redirected chapter lands.
        if len(parts) == 4 and parts[0] == "book" and parts[2] == "final":
            book, chapter = int(parts[1]), int(parts[3])
            archetype = ARCHETYPES[book % len(ARCHETYPES)]
            self._count("chapter")
            self._delay()
            self._send(self._chapter_document(archetype, book, chapter))
            return

        self._count("not_found")
        self._send(b"<html><body>not found</body></html>", status=404)

    def _chapter_document(self, archetype: Archetype, book: int, chapter: int) -> bytes:
        config = self.config
        # A title with characters Windows refuses in a filename, on a known
        # cadence: every fifth chapter, so a fifty-chapter run always hits it.
        title = f"Chapter {chapter}: The Hollow Gate"
        if chapter % 5 == 0:
            title = f'Chapter {chapter}: Where? <Now> "Then" | A/B \\ C*'
        if chapter % 25 == 0:
            title = f"Chapter {chapter}: " + "a very long chapter title " * 12
        body = _chapter_body(book, chapter, config.paragraphs, config.words_per_paragraph)
        html = archetype.chapter_template.format(title=title, body=body)
        # Outside the content element, as on a real page: the extractor must
        # not pick these up, and with --with-images Chromium still pays to
        # decode them.
        return _document(title, CHAPTER_FURNITURE + html)


def build_server(config: SiteConfig, port: int = 0) -> tuple[ThreadingHTTPServer, int]:
    """Start nothing; just construct. Returns the server and the bound port."""
    counters: dict[str, int] = {}
    lock = threading.Lock()

    handler = type(
        "BoundHandler",
        (Handler,),
        {"config": config, "counters": counters, "counter_lock": lock},
    )

    # Forty browsers opening connections at once overflows the default listen
    # backlog of 5, and the refusals that follow are the harness's fault, not
    # the program's. A deep queue keeps this server out of the measurement.
    # (Refusals are still worth testing - that is what --fail-rate is for, and
    # an overflow here is how the retry-classification bug was first seen.)
    class DeepQueue(ThreadingHTTPServer):
        request_queue_size = 512
        daemon_threads = True

    server = DeepQueue(("127.0.0.1", port), handler)
    return server, server.server_address[1]


def serve_in_thread(config: SiteConfig, port: int = 0) -> tuple[ThreadingHTTPServer, int]:
    server, bound = build_server(config, port)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server, bound


def selectors_for(book: int) -> Archetype:
    return ARCHETYPES[book % len(ARCHETYPES)]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Local stand-in site for the load test.")
    parser.add_argument("--port", type=int, default=8900)
    parser.add_argument("--books", type=int, default=4)
    parser.add_argument("--chapters", type=int, default=60)
    parser.add_argument("--paragraphs", type=int, default=40)
    parser.add_argument("--latency-ms", type=int, default=0)
    parser.add_argument("--jitter-ms", type=int, default=0)
    parser.add_argument("--hydrate-ms", type=int, default=350)
    parser.add_argument("--fail-rate", type=float, default=0.0)
    parser.add_argument("--redirect-rate", type=float, default=0.0)
    args = parser.parse_args(argv)

    config = SiteConfig(
        books=args.books,
        chapters=args.chapters,
        paragraphs=args.paragraphs,
        latency_ms=args.latency_ms,
        jitter_ms=args.jitter_ms,
        hydrate_ms=args.hydrate_ms,
        fail_rate=args.fail_rate,
        redirect_rate=args.redirect_rate,
    )
    server, port = build_server(config, args.port)
    print(f"serving on http://127.0.0.1:{port}")
    for book in range(args.books):
        archetype = selectors_for(book)
        print(
            f"  book {book} [{archetype.name}] "
            f"--toc http://127.0.0.1:{port}/book/{book}/toc "
            f'--link "{archetype.link_selector}" '
            f'--title "{archetype.title_selector}" '
            f'--content "{archetype.content_selector}"'
        )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopping")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
