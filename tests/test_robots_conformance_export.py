"""robots_conformance.json must match what the Python robots rules answer today.

The C# suite asserts every case in that file. If a change to the rules moved a
robots decision, the committed answers would be stale and the C# side would be
checked against a reference that no longer exists. The answers no longer
depend on the interpreter: they come from politeness.py, not urllib.robotparser.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from .golden import export_robots_conformance as export

CORPUS = Path(__file__).parent / "golden" / "robots_conformance.json"


def test_export_is_current() -> None:
    # Compared as parsed cases and reported by count, never as one string: an
    # 11,000-line string mismatch sends pytest's differ into a quadratic diff
    # that ran for over three hours in CI before hitting a RecursionError.
    committed = json.loads(CORPUS.read_text(encoding="utf-8"))
    current = export.payload()
    stale = [
        f"{new['body']} | {new['user_agent']} | {new['path']}"
        for old, new in zip(committed["cases"], current["cases"], strict=False)
        if old != new
    ]
    stale_shape = committed["bodies"] != current["bodies"] or len(committed["cases"]) != len(
        current["cases"]
    )
    if stale or stale_shape:
        pytest.fail(
            f"robots_conformance.json is stale ({len(stale)} changed answers, "
            f"first: {stale[:3]}); run:\n"
            "    ./.venv/bin/python tests/golden/export_robots_conformance.py",
            pytrace=False,
        )


def test_corpus_exercises_both_outcomes() -> None:
    """Agreement is trivial if nothing is ever refused."""
    cases = json.loads(CORPUS.read_text(encoding="utf-8"))["cases"]

    assert any(case["allowed"] for case in cases)
    assert any(not case["allowed"] for case in cases)


def test_every_case_names_a_known_body() -> None:
    payload = json.loads(CORPUS.read_text(encoding="utf-8"))

    for case in payload["cases"]:
        assert case["body"] in payload["bodies"]
