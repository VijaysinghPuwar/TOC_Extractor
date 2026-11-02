# omarchy: Lenovo IdeaPad 3 15IAU7, 1–2 November 2026

The C# CLI (`feat/dotnet-port` at `73cd62a`, Release build) completed every
single-process run with no lost chapters, up to 32 tabs and 2,334 chapters.
It broke at 20 processes at once: the load average reached 218 and 31 of
46,680 chapters were lost to a misclassified timeout. Memory grows with every
chapter, about 1.1–1.8 GB per 1,000 at full speed, and 86% of that growth is
Chromium. The laptop's own limit turned out to be its clock: turbo is off,
and after about three hours of load every core was held at 400 MHz.

## The machine

Full list in `2026-11-01/specs.json`. In short: Intel Core i3-1215U (2
performance + 4 efficiency cores, 8 threads), 23.2 GB RAM, Micron 512 GB
NVMe SSD, Omarchy on kernel 7.2.8, .NET SDK 10.0.403, on mains power.

Two settings matter for every number below:

- **Turbo is disabled** (`intel_pstate/no_turbo = 1`). Performance cores top
  out at 1,200 MHz and efficiency cores at 900 MHz, where the chip can boost
  to 4,400 and 3,300. Every figure here is at roughly a quarter of the chip's
  peak clock.
- **After about three hours of load, every core sat at 400 MHz**, the minimum,
  at 67 °C. The OS-side limits were normal (max 1,200 MHz, max_perf 100%, AC
  online), and the kernel log had nothing, so the clamp comes from the
  firmware or the embedded controller. The CPU recorded 236,932 core and
  443,996 package throttle events since boot. The kernel does not record
  their durations on this machine, so they cannot be tied to particular runs.

## What was run

| Folder | What |
|---|---|
| `2026-11-01/real-site/wikisource-61` | one real book, Pride and Prejudice (1817) from Wikisource, 61 chapters, default settings (3 tabs, 1.2 s between requests) |
| `2026-11-01/runs/single-c*` | a local 2,334-chapter copy, one process, 3, 8, 16 and 32 tabs, no delays |
| `2026-11-01/runs/swarm-n20` | 20 processes at once, 4 tabs each, each pulling all 2,334 chapters |
| `2026-11-02-throttle-check` | the finished kit, 300 chapters at 8 tabs plus 2 processes, run while the clock clamp was active |

The 2026-11-01 runs used early versions of the scripts now in `Test/tools/`,
converted to the kit's layout afterwards. Their `samples.csv` files have the
first seven columns only: the per-component memory split, clock and
temperature were added after these runs. Their samples counted PSS per
process, with threads excluded, the same as the kit.

## Results

| Run | Chapters | Lost | Time | Chapters/s | Peak memory | Peak CPU | Min RAM free |
|---|---|---|---|---|---|---|---|
| Wikisource, real site | 61/61 | 0 | 77 s | 0.8 (rate-limited) | 1,777 MB | 246% | n/a |
| 1 process, 3 tabs | 2,334/2,334 | 0 | 258 s | 9.05 | 5,282 MB | 415% | 14,018 MB |
| 1 process, 8 tabs | 2,334/2,334 | 0 | 152 s | **15.34** | 5,735 MB | 576% | 13,780 MB |
| 1 process, 16 tabs | 2,334/2,334 | 0 | 224 s | 10.41 | 4,344 MB | 611% | 15,269 MB |
| 1 process, 32 tabs | 2,334/2,334 | 0 | 379 s | 6.16 | 4,520 MB | 585% | 15,180 MB |
| 20 processes × 4 tabs | 46,649/46,680 | **31** | 7,989 s | 5.84 total | 14,802 MB | 812% | 3,573 MB |

Memory is PSS for the whole process tree: the app, the Playwright driver and
every Chromium process. CPU 800% means all 8 threads busy.

Checks that passed:

- Every chapter of every single-process run saved, numbered in order, none
  retried.
- The Wikisource book downloaded twice gave identical SHA-256 hashes for all
  61 chapters, and all 61 differ from each other.
- The rate limit held: 61 requests over 71.8 s, one every 1.20 s on average,
  which is the configured minimum.
- No browser or driver process outlived a run. (The kit's first version
  left .NET build servers running after its build; it now builds with
  `--disable-build-servers`.)

## What the software does under load

**1. Memory grows with every chapter and is mostly Chromium.** One process at
full speed grew 1.1–1.8 GB per 1,000 chapters, from about 600 MB after
start-up to 5.7 GB at 2,334 chapters. A 1,000-chapter diagnostic at 8 tabs
split the growth up: Chromium 1,562 MB per 1,000 chapters (86%), the
Playwright driver 172 MB (9%), the .NET app 82 MB (5%). Chromium's share once
fell from 2.2 to 1.8 GB late in the run, so it does give memory back. That
looks like pages piling up faster than Chromium reclaims them, not a pure
leak: in the swarm each process ran 20 times slower and grew only about
0.4 GB over its whole book. The app keeps three to 32 tabs open for the whole
run in one browser context (`BrowserPageSource.cs`, the page pool). Closing
and reopening each tab every few hundred chapters is the obvious thing to try.
The raw data of that 1,000-chapter diagnostic was not kept; the 300-chapter
throttle check shows the same split.

**2. Under heavy load a slow page is recorded as a missing one, and never
retried.** All 31 lost chapters in the swarm failed with `article.reader
matched nothing`, after one attempt. Every page has that element. When the
25 s attempt budget runs out while waiting for the selector,
`BrowserPageSource.cs:809` turns the `TimeoutException` into a
`SelectorNotFoundException`. `Fetcher.cs:460` treats that as permanent and
does not retry it. The comment above `ReadFieldAsync` already names this
trap; the wait fixed the fast case but not the case where the wait itself
times out. A fix would separate "the budget ran out" (retry) from "the page
finished loading and the element is not there" (do not retry). Nothing
disappears silently: the 17 processes that lost chapters list them in their
manifests and exit 1. But a person has to notice and rerun.

**3. Eight tabs was the fastest, but that may partly be the clock.** 8 tabs
gave 15.3 chapters/s; 16 gave 10.4 and 32 gave 6.2. The 16 and 32 tab runs
came after the 8 tab run, and clock speed was not logged then. A throttled
8-tab run later gave 5.8–6.6 chapters/s, close to the 32-tab figure. More
tabs past 8 did not help on this machine, but how much of the drop is the
tab count and how much is the clamp needs a rerun on a cool machine.

**4. `--max-delay` does not do what its help text says.** `CommandLine.cs:130`
calls it "Max delay between chapters". The gap between chapters is always
exactly `--min-delay`; `--max-delay` only caps the retry backoff. The Python
version behaves the same, so this is the wording, not a porting difference.

## Where this laptop's limit is

- **CPU first.** One process with 8 tabs used 576% of 800%. 20 processes
  held the CPU at 700–785% for 2 h 13 min, with the 1-minute load average
  between 166 and 218.
- **Memory second.** The swarm peaked at 14.8 GB of the 18 GB cap, with 3.6 GB
  of RAM still free. At its growth rate (0.18 GB per 1,000 chapters across all
  20) the cap would be reached around 64,000 chapters. That is an estimate
  from the trend, not something that happened.
- **Throughput collapses when oversubscribed.** 20 processes delivered 5.8
  chapters/s in total, against 15.3 from one process with 8 tabs, and lost
  chapters doing it.
- **Sustained heat or power.** After about three hours the firmware held every
  core at 400 MHz, cutting throughput to about 40% of the fresh figure at the
  same CPU percentage.

For this machine: one process, about 8 tabs, and expect around 5–6 GB for a
2,500-chapter book at full speed.

## What was lacking

In the software:

- Memory has no ceiling: a long book at full speed keeps growing (finding 1).
- Timeouts under load lose chapters instead of retrying them (finding 2).
- `--max-delay` help text is wrong (finding 4).

In this test:

- **Nothing ran on Windows.** This session had no access to a Windows
  machine. File locking, CRLF handling on resume and long paths are untested.
- **Novelfire was not run.** Its `robots.txt` disallows `Claude-User` by name,
  so Claude did not drive a download there. The desktop app was left open
  for a person to run Reverend Insanity or Shadow Slave themselves.
- **The swarm stopped at 20 processes.** 30 and 40 were planned; 20 already
  lost chapters, which is where the script stops.
- **The first swarm's exit codes are unreliable.** Three processes with no
  failures were recorded as exiting 1. A 20-process repeat on a small book
  gave exit 0 for all 20, so the recorder was at fault: it used `wait` on
  20 PIDs after two hours. `exits.log` in `swarm-n20` is rebuilt from each
  process's manifest (exit 1 exactly when a chapter failed, as the pipeline
  does); the original is kept as `exits-raw.log`. The kit now writes each
  exit code from inside the process's own subshell.
- **The sampler starved under the swarm**: 57 samples in 7,989 s instead of
  one every 3 s. Totals are right; the curve is coarse.
- **Clock and temperature were not logged on 2026-11-01.** That is why finding
  3 cannot be settled from this data. The kit logs both now.
- **No network faults were tested.** Local pages never fail, and Wikisource
  did not either, so the retry and backoff paths were not exercised.
- **Not tested at all:** resuming an interrupted run, the desktop app under
  load, the Python implementation.

## Next runs worth doing here

1. Reboot, let the machine cool, then run `Test/tools/run_stress.sh omarchy`
   with the clock column, to settle finding 3.
2. The same with turbo on (`echo 0 | sudo tee
   /sys/devices/system/cpu/intel_pstate/no_turbo`), then set it back, to
   measure what the setting costs.
3. After a fix for finding 2, the 20-process swarm again; it should lose
   nothing.
