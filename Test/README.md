# Stress tests, one folder per machine

How the C# extractor behaves under load on different hardware. Each machine
gets `Test/<machine>/<date>/`, written by the same script with the same
columns, so results join on run name across machines.

## Running it on a new machine

Linux only for now (the sampler reads `/proc` and `/sys`; see "Gaps" below).
Needs the .NET SDK pinned in `../global.json`, Python 3, `pstree`, and
Chromium for Playwright (`make cs-browser-install` once).

```
Test/tools/run_stress.sh [machine-name]      # defaults to the hostname
```

Plug the machine in and close heavy apps first. A full run takes about
three hours on a low-end laptop, almost all of it in the swarm phase.

Every page comes from 127.0.0.1. The book is built from `tools/corpus.jsonl.gz`,
the 61 chapters of Pride and Prejudice (1817, public domain, taken from
Wikisource), repeated in order to 2,334 chapters. Every machine serves
byte-identical pages and no site sees any traffic.

Phases, each pulling the whole book with every delay at zero:

| Run | What |
|---|---|
| `single-c3`, `-c8`, `-c16`, `-c32` | one process, that many browser tabs |
| `swarm-n20`, `-n30`, `-n40` | that many processes at once, 4 tabs each; stops at the first size where any process fails |

Everything runs in a systemd scope capped at 75% of RAM with swap off, so
running out of memory kills the test and not the desktop. Stop it any time
with `systemctl --user stop` on the scope; the temporary folder is removed.

Environment overrides: `CHAPTERS`, `TABS`, `SWARM`, `SWARM_TABS`, `MEM_CAP`,
`PORT`. An empty `TABS` or `SWARM` skips that phase.

## What a run folder holds

| File | Contents |
|---|---|
| `specs.json` | hardware and software, from `tools/collect_specs.sh` |
| `commit.txt` | the commit the CLI was built from |
| `summary.json`, `summary.csv` | one row per run, from `tools/summarize.py` |
| `runs/<run>/samples.csv` | one row every 3 s while the run is alive |
| `runs/<run>/summary-*.json` | the last line of each process's manifest |
| `runs/<run>/exits.log` | each process's exit code, and wall time |
| `runs/<run>/*.log` | the CLI's own output, paths replaced by `<work>` |

`samples.csv` columns: seconds since start; PSS of the whole process tree in
MB; process count; CPU % summed over the tree (800 = 8 threads busy); system
MemAvailable in MB; 1-minute load average; files written so far; then PSS
split into the .NET app, the Playwright driver and Chromium; average clock
across cores in MHz; CPU package temperature in °C.

PSS rather than RSS because Chromium's processes share most of their pages:
summed RSS counts those once per process and overstates the total several
times over.

## What is never recorded

Serial numbers, UUIDs, MAC and IP addresses, the username, home paths and
the network. The machine folder name is whatever you pass; pick one that is
not your name if that matters. Model names stay, because they are what
explains a difference between two machines. `collect_specs.sh` is short
enough to read before running it.

## Gaps

- **No Windows or macOS runner yet.** The CLI runs on both, but the sampler
  is bash over `/proc`. A PowerShell sampler would need the same columns.
- **Under very heavy load the sampler itself is starved.** On the first
  machine, 20 processes stretched the 3 s interval to about 140 s. The
  totals stay right, but the curve is coarse.
- **The local server is Python's `http.server`.** It is fast enough at these
  rates on the first machine, but nothing proves it is never the
  bottleneck.
