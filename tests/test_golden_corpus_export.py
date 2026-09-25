"""corpus.json must match corpus.py.

The C# suite reads corpus.json for its inputs and v1_golden.json for the
expected outputs. A case added to corpus.py without re-exporting would be
tested here and silently absent there, which is the one way two suites over
"one corpus" can quietly stop agreeing.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .golden import export_corpus
from .golden.corpus import FILENAME_CASES, TEXT_CASES

CORPUS_JSON = Path(__file__).parent / "golden" / "corpus.json"


def test_export_is_current() -> None:
    assert CORPUS_JSON.read_text(encoding="utf-8") == export_corpus.rendered(), (
        "corpus.json is behind corpus.py; run:\n"
        "    ./.venv/bin/python tests/golden/export_corpus.py"
    )


def exported() -> dict[str, Any]:
    return json.loads(CORPUS_JSON.read_text(encoding="utf-8"))


def test_every_case_is_exported() -> None:
    payload = exported()
    text = {case["id"] for group in payload["clean_text"].values() for case in group}
    filename = {case["id"] for group in payload["safe_filename"].values() for case in group}

    assert text == {case.id for case in TEXT_CASES}
    assert filename == {case.id for case in FILENAME_CASES}


def test_exported_values_are_the_real_inputs() -> None:
    """Guard against an export that writes ids and drops the values.

    Keyed per section: ids are only unique within one, and "empty",
    "whitespace_only" and "plain" all appear in both with different values.
    """
    payload = exported()
    sources = {
        "clean_text": {case.id: case.value for case in TEXT_CASES},
        "safe_filename": {case.id: case.value for case in FILENAME_CASES},
    }

    for section, by_id in sources.items():
        for group in payload[section].values():
            for case in group:
                assert case["value"] == by_id[case["id"]], f"{section}/{case['id']}"


def test_combos_match() -> None:
    payload = exported()
    combos = [(c["name"], c["remove_links"], c["strip_ads"]) for c in payload["clean_text_combos"]]

    assert combos == export_corpus._corpus().CLEAN_TEXT_COMBOS


def test_every_exported_case_has_a_golden_entry() -> None:
    """A case with no recorded v1 output cannot be asserted against by either suite."""
    golden = json.loads(
        (Path(__file__).parent / "golden" / "v1_golden.json").read_text(encoding="utf-8")
    )
    payload = exported()

    for case in payload["safe_filename"]["pinned"] + payload["safe_filename"]["changing"]:
        assert case["id"] in golden["safe_filename"]["cli"], case["id"]

    for combo in payload["clean_text_combos"]:
        recorded = golden["clean_text"]["cli"][combo["name"]]
        for case in payload["clean_text"]["pinned"]:
            assert case["id"] in recorded, f"{case['id']}/{combo['name']}"
