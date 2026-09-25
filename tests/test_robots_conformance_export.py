"""robots_conformance.json must match what robotparser answers today.

The C# suite asserts every case in that file. If a Python upgrade changed a
robots decision, the committed answers would be stale and the C# side would be
checked against a reference that no longer exists.
"""

from __future__ import annotations

import json
from pathlib import Path

from .golden import export_robots_conformance as export

CORPUS = Path(__file__).parent / "golden" / "robots_conformance.json"


def test_export_is_current() -> None:
    assert CORPUS.read_text(encoding="utf-8") == export.rendered(), (
        "robots_conformance.json is behind robotparser; run:\n"
        "    ./.venv/bin/python tests/golden/export_robots_conformance.py"
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
