"""Drive N extractor processes at once and record what the machine did.

    python tests/stress/run_stress.py --processes 40 --chapters 50

Each worker is a real `python -m toc_extractor` run against the local mock
site - the same command a person types, not an in-process shortcut - so what
is measured includes process start, the Chromium launch, and the exit path.

While the workers run, a sampler thread records available memory, the size of
the process tree, and how many browser processes are alive. That is the part
that matters on Windows: a Chromium launch storm is the failure mode a
concurrency knob invites, and it shows up as memory pressure long before it
shows up as a failed chapter.

The report is written as JSON next to the output so verify_stress.py can check
the files the run claims to have produced.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import shutil
import statistics
import subprocess
import sys
import threading
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from mock_site import ARCHETYPES, SiteConfig, serve_in_thread

try:
    import psutil
except ImportError:  # pragma: no cover - the harness degrades without it
    psutil = None  # type: ignore[assignment]


@dataclass
class WorkerResult:
    index: int
    book: int
    archetype: str
    exit_code: int | None
    seconds: float
    out_dir: str
    stdout_tail: str
    stderr_tail: str
    timed_out: bool = False


@dataclass
class Sample:
    at: float
    available_mb: float
    percent_used: float
    tree_processes: int
    browser_processes: int
    tree_rss_mb: float


@dataclass
class Report:
    processes: int
    chapters: int
    concurrency: int
    stagger_seconds: float
    wall_seconds: float = 0.0
    workers: list[WorkerResult] = field(default_factory=list)
    samples: list[Sample] = field(default_factory=list)
    server_counters: dict[str, int] = field(default_factory=dict)
    machine: dict[str, object] = field(default_factory=dict)


BROWSER_NAMES = ("chrome", "chromium", "headless_shell")
BROWSER_PATH_MARKERS = ("ms-playwright", "playwright")


def _is_driver_browser(process: psutil.Process) -> bool:
    """A browser process the driver started, not one the person had open."""
    try:
        name = (process.name() or "").lower()
        if not any(marker in name for marker in BROWSER_NAMES):
            return False
        exe = (process.exe() or "").lower()
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        return False
    return any(marker in exe for marker in BROWSER_PATH_MARKERS)


class Sampler(threading.Thread):
    """Records machine state every interval until asked to stop."""

    def __init__(self, interval: float = 0.5) -> None:
        super().__init__(daemon=True)
        self._interval = interval
        self._halt = threading.Event()
        self.samples: list[Sample] = []
        self._started_at = time.monotonic()

    def run(self) -> None:
        if psutil is None:
            return
        me = psutil.Process()
        while not self._halt.is_set():
            # A sampler must never be the reason a load test fails.
            with contextlib.suppress(Exception):
                self.samples.append(self._sample(me))
            self._halt.wait(self._interval)

    def _sample(self, me: psutil.Process) -> Sample:
        virtual = psutil.virtual_memory()
        tree = 0
        browsers = 0
        rss = 0
        for child in me.children(recursive=True):
            try:
                tree += 1
                rss += child.memory_info().rss
                # By executable path, not name: Chromium's helper processes are
                # all called chrome.exe, and so is the browser the person has
                # open. Only the one the driver downloaded lives under the
                # Playwright browsers directory.
                if _is_driver_browser(child):
                    browsers += 1
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
        return Sample(
            at=round(time.monotonic() - self._started_at, 2),
            available_mb=round(virtual.available / 1024 / 1024, 1),
            percent_used=virtual.percent,
            tree_processes=tree,
            browser_processes=browsers,
            tree_rss_mb=round(rss / 1024 / 1024, 1),
        )

    def stop(self) -> None:
        self._halt.set()


def machine_facts() -> dict[str, object]:
    facts: dict[str, object] = {
        "platform": sys.platform,
        "python": sys.version.split()[0],
        "cpu_count": os.cpu_count(),
    }
    if psutil is not None:
        virtual = psutil.virtual_memory()
        facts["total_memory_gb"] = round(virtual.total / 1024**3, 1)
        facts["available_memory_gb_at_start"] = round(virtual.available / 1024**3, 1)
    return facts


def run_worker(
    index: int,
    book: int,
    port: int,
    chapters: int,
    concurrency: int,
    out_root: Path,
    timeout: float,
    extra_args: list[str],
) -> WorkerResult:
    archetype = ARCHETYPES[book % len(ARCHETYPES)]
    out_dir = out_root / f"worker-{index:03d}"
    command = [
        sys.executable,
        "-m",
        "toc_extractor",
        "--toc",
        f"http://127.0.0.1:{port}/book/{book}/toc",
        "--link",
        archetype.link_selector,
        "--title",
        archetype.title_selector,
        "--content",
        archetype.content_selector,
        "--max",
        str(chapters),
        "--out",
        str(out_dir),
        "--concurrency",
        str(concurrency),
        # The mock site is on loopback, which the guard rejects by design.
        "--allow-private-hosts",
        # No point pacing against a server we control; the pacing logic has its
        # own unit tests. This measures throughput and resource use.
        "--min-delay",
        "0",
        "--max-delay",
        "0",
        "--wait-after-load",
        "0",
        "--quiet",
        *extra_args,
    ]

    started = time.monotonic()
    timed_out = False
    try:
        completed = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=timeout,
            encoding="utf-8",
            errors="replace",
        )
        code: int | None = completed.returncode
        stdout, stderr = completed.stdout, completed.stderr
    except subprocess.TimeoutExpired as exc:
        timed_out = True
        code = None
        stdout = (
            (exc.stdout or b"").decode("utf-8", "replace")
            if isinstance(exc.stdout, bytes)
            else (exc.stdout or "")
        )
        stderr = (
            (exc.stderr or b"").decode("utf-8", "replace")
            if isinstance(exc.stderr, bytes)
            else (exc.stderr or "")
        )

    return WorkerResult(
        index=index,
        book=book,
        archetype=archetype.name,
        exit_code=code,
        seconds=round(time.monotonic() - started, 2),
        out_dir=str(out_dir),
        stdout_tail=stdout[-800:],
        stderr_tail=stderr[-1500:],
        timed_out=timed_out,
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Load test the extractor against a local site.")
    parser.add_argument("--processes", type=int, default=40)
    parser.add_argument("--chapters", type=int, default=50)
    parser.add_argument("--concurrency", type=int, default=3)
    parser.add_argument("--books", type=int, default=len(ARCHETYPES))
    parser.add_argument(
        "--stagger",
        type=float,
        default=0.0,
        help="Seconds between worker launches. 0 starts them all at once.",
    )
    parser.add_argument("--timeout", type=float, default=900.0)
    parser.add_argument("--latency-ms", type=int, default=0)
    parser.add_argument("--fail-rate", type=float, default=0.0)
    parser.add_argument("--redirect-rate", type=float, default=0.0)
    parser.add_argument("--paragraphs", type=int, default=40)
    parser.add_argument("--out", default="stress-out")
    parser.add_argument("--report", default=None)
    parser.add_argument("--keep-output", action="store_true")
    parser.add_argument(
        "--worker-arg",
        action="append",
        default=[],
        help="Extra flag passed to every worker, repeatable.",
    )
    args = parser.parse_args(argv)

    out_root = Path(args.out)
    if out_root.exists() and not args.keep_output:
        shutil.rmtree(out_root)
    out_root.mkdir(parents=True, exist_ok=True)

    config = SiteConfig(
        books=args.books,
        chapters=max(args.chapters * 2, 120),
        paragraphs=args.paragraphs,
        latency_ms=args.latency_ms,
        fail_rate=args.fail_rate,
        redirect_rate=args.redirect_rate,
    )
    server, port = serve_in_thread(config)
    print(f"mock site on http://127.0.0.1:{port}")
    print(
        f"launching {args.processes} process(es), {args.chapters} chapters each, "
        f"concurrency {args.concurrency}, stagger {args.stagger}s"
    )
    if psutil is None:
        print("psutil not installed: memory and process sampling is off")

    report = Report(
        processes=args.processes,
        chapters=args.chapters,
        concurrency=args.concurrency,
        stagger_seconds=args.stagger,
        machine=machine_facts(),
    )

    sampler = Sampler()
    sampler.start()

    results: list[WorkerResult | None] = [None] * args.processes
    threads: list[threading.Thread] = []
    started_at = time.monotonic()

    def launch(index: int) -> None:
        results[index] = run_worker(
            index=index,
            book=index % args.books,
            port=port,
            chapters=args.chapters,
            concurrency=args.concurrency,
            out_root=out_root,
            timeout=args.timeout,
            extra_args=args.worker_arg,
        )

    for index in range(args.processes):
        thread = threading.Thread(target=launch, args=(index,), daemon=True)
        thread.start()
        threads.append(thread)
        if args.stagger:
            time.sleep(args.stagger)

    for thread in threads:
        thread.join()

    report.wall_seconds = round(time.monotonic() - started_at, 2)
    sampler.stop()
    sampler.join(timeout=3)
    report.samples = sampler.samples
    report.workers = [r for r in results if r is not None]

    try:
        import urllib.request

        with urllib.request.urlopen(f"http://127.0.0.1:{port}/stats", timeout=5) as response:
            report.server_counters = json.loads(response.read())
    except Exception:
        pass

    server.shutdown()
    server.server_close()

    _print_summary(report)

    report_path = Path(args.report) if args.report else out_root / "stress-report.json"
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(
            {
                **{k: v for k, v in asdict(report).items() if k not in {"workers", "samples"}},
                "workers": [asdict(w) for w in report.workers],
                "samples": [asdict(s) for s in report.samples],
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"\nreport: {report_path}")

    failed = [w for w in report.workers if w.exit_code != 0]
    return 1 if failed else 0


def _print_summary(report: Report) -> None:
    workers = report.workers
    ok = [w for w in workers if w.exit_code == 0]
    bad = [w for w in workers if w.exit_code != 0]
    durations = [w.seconds for w in workers] or [0.0]

    print("\n" + "=" * 68)
    print(f"wall clock        {report.wall_seconds}s")
    print(f"workers           {len(ok)}/{len(workers)} exited 0")
    print(
        f"worker seconds    min {min(durations):.1f}  "
        f"median {statistics.median(durations):.1f}  max {max(durations):.1f}"
    )
    if report.samples:
        peak_browsers = max(s.browser_processes for s in report.samples)
        peak_tree = max(s.tree_processes for s in report.samples)
        peak_rss = max(s.tree_rss_mb for s in report.samples)
        low_available = min(s.available_mb for s in report.samples)
        print(f"peak processes    {peak_tree} in tree, {peak_browsers} browser")
        print(f"peak tree memory  {peak_rss / 1024:.2f} GB")
        print(f"lowest available  {low_available / 1024:.2f} GB")
    if report.server_counters:
        print(f"server served     {report.server_counters}")
    if bad:
        print(f"\n{len(bad)} worker(s) did not exit 0:")
        for worker in bad[:6]:
            state = "TIMED OUT" if worker.timed_out else f"exit {worker.exit_code}"
            print(f"  worker {worker.index:03d} [{worker.archetype}] {state}")
            tail = (worker.stderr_tail or worker.stdout_tail).strip().splitlines()
            for line in tail[-4:]:
                print(f"      {line}")
    print("=" * 68)


if __name__ == "__main__":
    sys.exit(main())
