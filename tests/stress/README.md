# The load test

Forty processes, fifty chapters each, two thousand chapters, against a local
stand-in for the sites this tool reads.

```
make stress                      # 40 x 50, then verify the output
make stress STRESS_PROCESSES=8   # something smaller
```

or directly:

```
python tests/stress/run_stress.py --processes 40 --chapters 50 --out stress-out
python tests/stress/verify_stress.py stress-out
```

## Why it does not point at the real sites

Two thousand requests aimed at someone else's server is an attack, not a
test, and it would measure the wrong thing: the number that came back would
be their rate limiter and their bot protection, not this program under load,
and it would not be the same twice.

So `mock_site.py` copies the *shapes* and none of the prose. Each archetype in
it mirrors the DOM of one site that was surveyed — where the chapter links
sit, how deeply they nest, whether they arrive with the document or after it
— and fills it with generated filler. That is what actually gets exercised:
selector depth, hydration timing, redirects, flaky responses, page weight,
and titles carrying characters Windows will not put in a filename.

The real sites are still used, separately and gently: a few chapters at a
time, to check that the profiles in `profiles/sites/` still select what they
claim to. That is a correctness check, and it is not this.

## The three pieces

| File | What it is |
|---|---|
| `mock_site.py` | The local site. Four DOM archetypes, with knobs for latency, hydration delay, redirects and injected failures. Runnable alone: `python tests/stress/mock_site.py --port 8900`. |
| `run_stress.py` | Spawns N real `python -m toc_extractor` processes and samples memory, process count and browser count while they run. Writes `stress-report.json`. |
| `verify_stress.py` | Re-reads what the run produced and checks the invariants. Exits non-zero if any fail. |

Running the workers as real processes is deliberate: it puts process start,
the Chromium launch and the exit path inside the measurement, which is where
the interesting behaviour on Windows turned out to be.

## Why verification is separate

Every process exiting 0 proves nothing on its own. The failures worth finding
under load are the quiet ones, so `verify_stress.py` checks, for every worker:

- exactly the expected number of chapter files, none empty or header-only
- no filename Windows would refuse — reserved device names, `<>:"/\|?*`,
  trailing dots or spaces, over-long names
- no two names that differ only by case, which are two files on a Mac and one
  on Windows
- `combined.txt` present, free of CRLF, and actually containing each chapter
- the checkpoint's count, filenames and SHA-256 digests matching what is on
  disk

## What it found

Two bugs, both load-dependent, both fixed, each with a regression test that
fails without the fix:

**A refused connection was reported as a blocked URL.** `PageBlocked` means
the guard refused the target and is never retried, on the reasoning that a
disallowed URL stays disallowed. Transport failures were being reported the
same way, so a refused or reset connection abandoned the chapter on the first
attempt with `--retries` unspent. Under load that is the *common* failure, not
a rare one.

**A page abandoned mid-navigation went back into the pool dirty.** The fetch
loop puts a wall-clock backstop over each chapter; when it fires, the
coroutine is cancelled while Chromium is still navigating, and Playwright does
not abandon a navigation because the caller stopped waiting. The next worker
to take that slot collided with it — "interrupted by another navigation" —
and lost a chapter that was never at fault. The page is now repaired on the
way back out of the pool rather than on release, because releasing happens
during cancellation, where awaiting anything just raises again.

## What it measured

On the machine it was developed against — Ryzen 7 7700X, 16 logical cores,
15.2 GB, Windows 11 — forty processes at the default concurrency of three:

| | |
|---|---|
| Chapters | 2000, all written and verified |
| Wall clock | ~40 s |
| Peak processes | 360, of which 240 were browser processes |
| Peak resident set | 15–19 GB |
| Free memory at peak | 0 |

The last two lines are the finding. Forty concurrent extractions do not fit
in 16 GB: nothing crashes, but every worker ends up waiting on the disk.
Forty processes is a stress test, not a recommendation — the ceiling this
machine actually sustains comfortably is closer to eight. `machine.py` is
what keeps a single run inside what the computer has, and it deliberately
cannot see the other thirty-nine processes; that is the operator's call.

Skipping images, video and webfonts (the default; `--with-images` turns them
back on) was measured too, and it is **not** a memory saving — peak resident
set was the same either way at eight processes. What Chromium costs is its
processes, not the images they decode. It does avoid roughly two and a half
requests per chapter on this page shape, which is bandwidth and load the site
did not have to serve.
