"""Write corpus.py out as JSON so a second implementation can read it.

The C# port asserts the same behaviour against the same cases. Retyping the
corpus there would mean two lists that agree until someone edits one, which is
the drift corpus.py exists to prevent in the first place.

corpus.py stays the source of truth. This writes corpus.json beside it:

    ./.venv/bin/python tests/golden/export_corpus.py

test_golden_corpus_export.py fails if the committed JSON has fallen behind, so
a case added without running this is caught rather than silently missing from
the other implementation's suite.
"""

from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
OUTPUT = HERE / "corpus.json"


def _corpus() -> Any:
    """Load corpus.py by path.

    Loaded this way rather than imported so the file works both as a script
    (`python tests/golden/export_corpus.py`) and as a module the sync test
    imports. A plain `from corpus import ...` works in exactly one of those.
    """
    spec = importlib.util.spec_from_file_location("_golden_corpus", HERE / "corpus.py")
    if spec is None or spec.loader is None:  # pragma: no cover - unreachable in-tree
        raise RuntimeError(f"could not load {HERE / 'corpus.py'}")
    module = importlib.util.module_from_spec(spec)
    # Registered before execution: @dataclass resolves annotations through
    # sys.modules, and a module that is not there yet fails on the first
    # decorated class rather than on anything to do with this file.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def case_to_json(case: Any) -> dict[str, Any]:
    return {
        "id": case.id,
        "value": case.value,
        "reason": case.reason,
        "tags": sorted(case.tags),
    }


def payload() -> dict[str, Any]:
    corpus = _corpus()
    return {
        "clean_text_combos": [
            {"name": name, "remove_links": remove_links, "strip_ads": strip_ads}
            for name, remove_links, strip_ads in corpus.CLEAN_TEXT_COMBOS
        ],
        "clean_text": {
            "pinned": [case_to_json(case) for case in corpus.TEXT_PINNED],
            "changing": [case_to_json(case) for case in corpus.TEXT_CHANGING],
        },
        "safe_filename": {
            "pinned": [case_to_json(case) for case in corpus.FILENAME_PINNED],
            "changing": [case_to_json(case) for case in corpus.FILENAME_CHANGING],
        },
    }


def rendered() -> str:
    # ensure_ascii so the committed file is byte-stable regardless of the
    # checkout's encoding, and sorted keys so a re-export is a no-op diff.
    return json.dumps(payload(), indent=2, sort_keys=True, ensure_ascii=True) + "\n"


def main() -> int:
    OUTPUT.write_text(rendered(), encoding="utf-8")
    counts = payload()
    print(
        f"wrote {OUTPUT.name}: "
        f"{len(counts['clean_text']['pinned'])} pinned / "
        f"{len(counts['clean_text']['changing'])} changing clean_text case(s), "
        f"{len(counts['safe_filename']['pinned'])} pinned / "
        f"{len(counts['safe_filename']['changing'])} changing safe_filename case(s)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
