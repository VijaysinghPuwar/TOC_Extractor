"""Check that a load test actually produced the files it claims.

    python tests/stress/verify_stress.py stress-out

A run where every process exits 0 proves nothing on its own: the interesting
failures under load are silent ones - a chapter written twice under two names,
a combined.txt missing a chapter, a checkpoint pointing at a file that is not
there, a title that became an illegal filename. So this re-reads the output of
every worker and checks the invariants the program promises.

Exits non-zero if any check fails, with the failures named.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path

# Names Windows refuses whatever the extension, and characters it refuses in a
# path segment. A cross-platform tool has to avoid both, because output written
# on a Mac gets opened on Windows.
WINDOWS_RESERVED = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{n}" for n in range(1, 10)),
    *(f"LPT{n}" for n in range(1, 10)),
}
ILLEGAL_CHARS = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

COMBINED = "combined.txt"
CHECKPOINT_CANDIDATES = (".toc_extractor_state.json",)


@dataclass
class Failure:
    worker: str
    check: str
    detail: str


@dataclass
class Verification:
    workers_checked: int = 0
    chapters_seen: int = 0
    failures: list[Failure] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    def fail(self, worker: str, check: str, detail: str) -> None:
        self.failures.append(Failure(worker, check, detail))


def find_checkpoint(directory: Path) -> Path | None:
    for name in CHECKPOINT_CANDIDATES:
        candidate = directory / name
        if candidate.exists():
            return candidate
    # Fall back to any single json file that looks like a checkpoint.
    for candidate in directory.glob("*.json"):
        try:
            data = json.loads(candidate.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(data, dict) and "completed" in data:
            return candidate
    return None


def check_filename(name: str) -> str | None:
    """The reason this name would not survive a round trip, or None."""
    stem = name.split(".")[0].upper()
    if stem in WINDOWS_RESERVED:
        return f"reserved device name {stem!r}"
    if ILLEGAL_CHARS.search(name):
        found = ILLEGAL_CHARS.findall(name)
        return f"illegal character(s) {found!r}"
    if name != name.rstrip(" ."):
        return "trailing space or dot, which Windows silently strips"
    if len(name.encode("utf-8")) > 255:
        return f"name is {len(name.encode('utf-8'))} bytes, over the 255 limit"
    return None


def verify_worker(directory: Path, expected_chapters: int, result: Verification) -> None:
    worker = directory.name
    if not directory.exists():
        result.fail(worker, "output directory", "does not exist")
        return

    chapter_files = sorted(p for p in directory.glob("*.txt") if p.name != COMBINED)
    result.chapters_seen += len(chapter_files)

    if len(chapter_files) != expected_chapters:
        result.fail(
            worker,
            "chapter count",
            f"expected {expected_chapters} .txt files, found {len(chapter_files)}",
        )

    # Empty or header-only files: a chapter that "succeeded" with no prose is
    # the silent failure this whole exercise is looking for.
    for path in chapter_files:
        size = path.stat().st_size
        if size == 0:
            result.fail(worker, "empty chapter", path.name)
            continue
        text = path.read_text(encoding="utf-8")
        body = text.split("\n\n", 1)[1] if "\n\n" in text else ""
        if len(body.strip()) < 200:
            result.fail(worker, "chapter body too short", f"{path.name}: {len(body.strip())} chars")

    for path in chapter_files:
        reason = check_filename(path.name)
        if reason is not None:
            result.fail(worker, "unportable filename", f"{path.name}: {reason}")

    # Case-insensitive collisions: two names that are distinct on a Mac and the
    # same file on Windows.
    lowered: dict[str, str] = {}
    for path in chapter_files:
        key = unicodedata.normalize("NFC", path.name).casefold()
        if key in lowered:
            result.fail(worker, "case collision", f"{lowered[key]} vs {path.name}")
        lowered[key] = path.name

    combined = directory / COMBINED
    if not combined.exists():
        result.fail(worker, "combined.txt", "missing")
    else:
        merged = combined.read_text(encoding="utf-8")
        if b"\r\n" in combined.read_bytes():
            result.fail(worker, "line endings", "combined.txt contains CRLF")
        # Every chapter's body must appear in the merged file.
        for path in chapter_files:
            text = path.read_text(encoding="utf-8")
            probe = text.strip().splitlines()
            if len(probe) < 3:
                continue
            # A line from the middle of the chapter, long enough to be unique.
            middle = next(
                (line for line in probe[2:] if len(line) > 80),
                None,
            )
            if middle is not None and middle not in merged:
                result.fail(worker, "combined.txt omits a chapter", f"{path.name} body not present")

    checkpoint_path = find_checkpoint(directory)
    if checkpoint_path is None:
        result.notes.append(f"{worker}: no checkpoint file found")
        return

    try:
        checkpoint = json.loads(checkpoint_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        result.fail(worker, "checkpoint is not valid JSON", str(exc))
        return

    completed = checkpoint.get("completed")
    entries = list(completed.values()) if isinstance(completed, dict) else list(completed or [])
    if len(entries) != expected_chapters:
        result.fail(
            worker,
            "checkpoint count",
            f"records {len(entries)} completed, expected {expected_chapters}",
        )

    for entry in entries:
        if not isinstance(entry, dict):
            continue
        name = entry.get("output_name") or ""
        if not name:
            result.fail(worker, "checkpoint entry", "has no output_name")
            continue
        path = directory / name
        if not path.exists():
            result.fail(worker, "checkpoint points at a missing file", name)
            continue
        recorded = entry.get("output_sha256") or ""
        if recorded:
            actual = hashlib.sha256(path.read_bytes()).hexdigest()
            if actual != recorded:
                result.fail(worker, "checkpoint hash mismatch", name)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify load-test output.")
    parser.add_argument("directory", help="The --out directory the load test used")
    parser.add_argument(
        "--chapters",
        type=int,
        default=None,
        help="Expected chapters per worker. Read from the report when omitted.",
    )
    args = parser.parse_args(argv)

    root = Path(args.directory)
    expected = args.chapters
    report_path = root / "stress-report.json"
    report: dict[str, object] = {}
    if report_path.exists():
        report = json.loads(report_path.read_text(encoding="utf-8"))
        if expected is None:
            expected = int(report.get("chapters", 0))
    if not expected:
        print("cannot tell how many chapters to expect: pass --chapters", file=sys.stderr)
        return 2

    result = Verification()
    worker_dirs = sorted(p for p in root.glob("worker-*") if p.is_dir())
    if not worker_dirs:
        print(f"no worker directories under {root}", file=sys.stderr)
        return 2

    # Only verify workers the report says exited 0. A worker that failed is
    # already reported as a failure; its partial output is expected.
    clean: set[str] = set()
    if report:
        for worker in report.get("workers", []):  # type: ignore[union-attr]
            if isinstance(worker, dict) and worker.get("exit_code") == 0:
                clean.add(Path(str(worker.get("out_dir", ""))).name)

    for directory in worker_dirs:
        if clean and directory.name not in clean:
            result.notes.append(f"{directory.name}: skipped, the run reported it as failed")
            continue
        result.workers_checked += 1
        verify_worker(directory, expected, result)

    print(f"verified {result.workers_checked} worker(s), {result.chapters_seen} chapter file(s)")
    for note in result.notes[:10]:
        print(f"  note: {note}")
    if result.notes[10:]:
        print(f"  ... and {len(result.notes) - 10} more notes")

    if not result.failures:
        print("all checks passed")
        return 0

    by_check: dict[str, list[Failure]] = {}
    for failure in result.failures:
        by_check.setdefault(failure.check, []).append(failure)

    print(f"\n{len(result.failures)} failure(s) across {len(by_check)} check(s):")
    for check, failures in sorted(by_check.items(), key=lambda kv: -len(kv[1])):
        print(f"\n  {check}  ({len(failures)})")
        for failure in failures[:5]:
            print(f"    {failure.worker}: {failure.detail}")
        if failures[5:]:
            print(f"    ... and {len(failures) - 5} more")
    return 1


if __name__ == "__main__":
    sys.exit(main())
