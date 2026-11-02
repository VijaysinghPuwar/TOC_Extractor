"""Reduce one stress run folder to summary.json and summary.csv.

    python3 summarize.py Test/<machine>/<date>

Reads runs/*/samples.csv, exits.log and summary-*.json (the last line of each
process's manifest). One row per run, so machines compare with a join on
run name.
"""

import csv
import glob
import json
import os
import sys


def growth_per_1000(rows: list[dict], column: str = "tree_pss_mb") -> float | None:
    """Least-squares slope of memory against files written, after warm-up."""
    pts = [(r["files_written"], r[column]) for r in rows if r["files_written"] > 50 and column in r]
    if len(pts) < 5:
        return None
    n = len(pts)
    mx = sum(x for x, _ in pts) / n
    my = sum(y for _, y in pts) / n
    var = sum((x - mx) ** 2 for x, _ in pts)
    if var == 0:
        return None
    return round(1000 * sum((x - mx) * (y - my) for x, y in pts) / var, 1)


def summarize(run_dir: str) -> dict:
    name = os.path.basename(run_dir)
    with open(os.path.join(run_dir, "samples.csv"), encoding="utf-8") as f:
        rows = [{k: float(v) for k, v in r.items()} for r in csv.DictReader(f)]

    exits, elapsed_ms = [], None
    with open(os.path.join(run_dir, "exits.log"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("elapsed_ms="):
                elapsed_ms = int(line.split("=")[1])
            elif "exit=" in line:
                exits.append(int(line.rsplit("=", 1)[1]))

    completed = failed = expected = 0
    for path in glob.glob(os.path.join(run_dir, "summary-*.json")):
        with open(path, encoding="utf-8") as f:
            s = json.load(f)
        completed += s.get("completed", 0)
        failed += len(s.get("failed", []))
        expected += s.get("kept", 0)

    seconds = elapsed_ms / 1000 if elapsed_ms else None
    # Older runs predate the per-component columns; leave those blank.
    parts = {}
    for part in ("app", "driver", "browser"):
        col = f"{part}_pss_mb"
        have = rows and col in rows[0]
        parts[f"peak_{col}"] = int(max(r[col] for r in rows)) if have else None
        parts[f"{part}_growth_mb_per_1000_chapters"] = growth_per_1000(rows, col) if have else None
    has_clock = bool(rows) and "cpu_mhz_avg" in rows[0]
    return {
        "run": name,
        "processes": len(exits),
        "processes_failed": sum(1 for e in exits if e != 0),
        "exit_codes": sorted(set(exits)),
        "elapsed_s": round(seconds, 1) if seconds else None,
        "chapters_expected": expected,
        "chapters_completed": completed,
        "chapters_failed": failed,
        "chapters_per_s": round(completed / seconds, 2) if seconds and completed else None,
        "peak_tree_pss_mb": int(max((r["tree_pss_mb"] for r in rows), default=0)),
        "mem_growth_mb_per_1000_chapters": growth_per_1000(rows),
        "peak_procs": int(max((r["procs"] for r in rows), default=0)),
        "peak_cpu_pct": int(max((r["cpu_pct"] for r in rows), default=0)),
        "mean_cpu_pct": int(sum(r["cpu_pct"] for r in rows) / len(rows)) if rows else None,
        "min_mem_avail_mb": int(min((r["mem_avail_mb"] for r in rows), default=0)),
        "peak_load1": max((r["load1"] for r in rows), default=0),
        "samples": len(rows),
        **parts,
        "mean_cpu_mhz": int(sum(r["cpu_mhz_avg"] for r in rows) / len(rows)) if has_clock else None,
        "peak_pkg_temp_c": int(max(r["pkg_temp_c"] for r in rows)) if has_clock else None,
    }


def run_order(path: str) -> tuple[bool, int]:
    """Single-process runs first, then by the number in the name."""
    name = os.path.basename(path)
    return (not name.startswith("single"), int("".join(c for c in name if c.isdigit()) or 0))


def main() -> None:
    out = sys.argv[1]
    candidates = glob.glob(os.path.join(out, "runs", "*"))
    runs = sorted(
        (d for d in candidates if os.path.isfile(os.path.join(d, "samples.csv"))),
        key=run_order,
    )
    results = [summarize(d) for d in runs]
    with open(os.path.join(out, "summary.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(results, f, indent=2)
        f.write("\n")
    if results:
        with open(os.path.join(out, "summary.csv"), "w", encoding="utf-8", newline="") as f:
            w = csv.DictWriter(f, fieldnames=list(results[0]), lineterminator="\n")
            w.writeheader()
            for r in results:
                w.writerow(
                    {k: (";".join(map(str, v)) if isinstance(v, list) else v) for k, v in r.items()}
                )
    for r in results:
        print(r)


if __name__ == "__main__":
    main()
