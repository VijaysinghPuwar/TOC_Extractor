"""Record what urllib.robotparser answers, so the C# port can be checked against it.

.NET ships no robots.txt parser, so that layer of the C# implementation is
written from scratch rather than ported. Agreeing with the reference across a
corpus is the only honest evidence it behaves the same.

Writes robots_conformance.json beside this file:

    ./.venv/bin/python tests/golden/export_robots_conformance.py

test_robots_conformance_export.py fails if the committed JSON has fallen
behind, and RobotsConformanceTests.cs on the C# side asserts every answer.

Both implementations follow RFC 9309 and must agree on every case. There is
no exception list.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from toc_extractor.politeness import parse_robots

OUTPUT = Path(__file__).resolve().parent / "robots_conformance.json"
ORIGIN = "https://e.com"

BODIES: dict[str, str] = {
    "simple": "User-agent: *\nDisallow: /private/\n",
    "two_groups": (
        "User-agent: *\nDisallow: /private/\nCrawl-delay: 4\n\n"
        "User-agent: TOCExtractor\nDisallow: /members/\nAllow: /members/public/\n"
    ),
    "allow_first": "User-agent: *\nAllow: /a/b/\nDisallow: /a/\n",
    "disallow_first": "User-agent: *\nDisallow: /a/\nAllow: /a/b/\n",
    "star_mid": "User-agent: *\nDisallow: /*/secret\n",
    "dollar": "User-agent: *\nDisallow: /*.pdf$\n",
    "empty_dis": "User-agent: *\nDisallow:\n",
    "root_dis": "User-agent: *\nDisallow: /\n",
    "root_allow": "User-agent: *\nDisallow: /\nAllow: /public/\n",
    "no_groups": "# nothing\n",
    "comment_inline": "User-agent: *\nDisallow: /x/ # trailing comment\n",
    "multi_agent": (
        "User-agent: alpha\nUser-agent: beta\nDisallow: /ab/\n\nUser-agent: *\nDisallow: /star/\n"
    ),
    "case_agent": "User-agent: TOCEXTRACTOR\nDisallow: /caps/\n",
    "equal_len": "User-agent: *\nDisallow: /x/y/\nAllow: /x/y/\n",
    "nested": "User-agent: *\nDisallow: /a/\nDisallow: /a/b/\nAllow: /a/b/c/\n",
    # A wildcard rule is still a prefix: /s*t covers /secret, not /x/secret.
    "star_prefix": "User-agent: *\nDisallow: /s*t\n",
}

AGENTS = [
    "TOCExtractor",
    "TOCExtractor/2.0",
    "tocextractor",
    "MyTOCExtractorBot",
    "SomeOtherBot",
    "alpha",
    "beta",
    "*",
]

PATHS = [
    "/",
    "/private/x",
    "/members/secret",
    "/members/public/x",
    "/a/b/c/d",
    "/a/b/x",
    "/a/z",
    "/x/secret",
    "/doc.pdf",
    "/doc.pdf.html",
    "/public/z",
    "/star/q",
    "/ab/q",
    "/caps/q",
    "/x/y/z",
    "/anything",
]


def payload() -> dict[str, Any]:
    cases = [
        {
            "body": name,
            "user_agent": agent,
            "path": path,
            "allowed": parse_robots(body, origin=ORIGIN, user_agent=agent).can_fetch(ORIGIN + path),
        }
        for name, body in BODIES.items()
        for agent in AGENTS
        for path in PATHS
    ]
    return {"bodies": BODIES, "cases": cases}


def rendered() -> str:
    return json.dumps(payload(), indent=1, sort_keys=True, ensure_ascii=True) + "\n"


def main() -> int:
    OUTPUT.write_text(rendered(), encoding="utf-8")
    cases = payload()["cases"]
    denied = sum(1 for case in cases if not case["allowed"])
    print(
        f"wrote {OUTPUT.name}: {len(BODIES)} bodies x {len(AGENTS)} agents x "
        f"{len(PATHS)} paths = {len(cases)} cases ({denied} denied)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
