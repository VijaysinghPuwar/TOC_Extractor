# TOC Extractor: the C# implementation

A second implementation of the extractor, built on .NET 10 and
Microsoft.Playwright. It is also what the desktop app is made of. The Python package under `../src/toc_extractor` remains the
reference implementation.

## Why two

The two exist to be compared. Where both implement the same rule, a shared
corpus asserts they agree rather than each testing itself in isolation, the
text-cleaning and filename rules are pinned by `../tests/golden/v1_golden.json`,
which both suites read. A behaviour that differs between them is either a bug or
a decision recorded below, never an accident.

## Status

| Layer | C# | Ported from |
|---|---|---|
| Rejection reasons | done | `politeness.py` |
| Text cleaning, filename allocation | done | `text.py` |
| URL guard, robots policy, rate limiter | done | `politeness.py` |
| Link collection, selector set | done | `parser.py`, `models.py` |
| Fetch loop | done | `fetcher.py`, `pagesource.py`, `sinks.py` |
| Exporters, checkpoint | done | `exporters/`, `checkpoint.py` |
| Playwright page source | done | `browser.py` |
| CLI, TOML profiles | done | `cli.py`, `config.py` |
| Scanner, range planner, desktop app | C# only | none |

## Deliberate differences from Python

Recorded as they are made, so a divergence is never mistaken for drift.

**Case folding in `FilenameAllocator`.** Python keys the collision table with
`str.casefold()`; .NET has no equivalent, so this uses `ToLowerInvariant()`.
Full case folding is more aggressive than lowercasing, so Python catches a
collision between "STRASSE" and "strasse" that this does not. Both catch every
ASCII case clash, which is what the corpus covers and what the target
filesystems actually collide on. Inventing a folding table for a case APFS and
ext4 do not agree on either would be worse than the gap.

**robots.txt is no longer a difference.** An earlier version recorded that
Python's `urllib.robotparser` chose a group by substring while this chose it
by product token, and that the two disagreed on three conformance cases.
Python now has its own RFC 9309 evaluator (longest match, `*` and `$`, Allow
winning ties, groups by product token), because `robotparser` also answered
differently on different Python versions. Both sides now agree on every case
in the corpus.

**`LinkCollection` is named `LinkTally`.** A .NET type whose name ends in
`Collection` is expected to implement `ICollection`; this one is a count of
what happened to every candidate, so the analyser is right to object and
"tally" is the more accurate word anyway. Same fields, same invariant.

**One deadline per attempt, not three.** Python gives the same `--timeout`
value to the navigation, to each selector wait, and to the fetch loop's own
wrapper, independently. Three 25s budgets nest inside an outer 25s, so the
outer one always wins: a page that loads in 20s and needs 3s for its selector
is reported as a timeout and retried, and the inner waits can never use the
budget they were handed. Here the option is named `PageBudget`, the fetch loop
turns it into one cancellation token, and `IPageSource` is required to honour
that token rather than impose a timeout of its own.

**Locking the progress tally and sink writes.** Python needs neither: its event
loop is single-threaded, so a coroutine step with no `await` between read and
write cannot interleave, and the sinks mutate their state in exactly such a
step. .NET continuations run on the thread pool and genuinely do run at the
same time, so the same code is a data race. The tally is a concurrent
dictionary and sink writes go through a gate. This is the one place the two
concurrency models differ in a way a port cannot paper over.

**`Task.WhenAll` rather than a task group.** Python's task group cancels its
siblings when a child raises. No child here is expected to raise, every path
records either a record or a failure, so the difference is unobservable, and
the run accounting is the backstop either way.

**Merged files are assembled by URL, in table-of-contents order.** Python
merges by chapter index, where fresh chapters are numbered by today's position
and resumed ones keep the number the checkpoint recorded. After a prepend the
two schemes collide: the resumed chapter's number is already taken, the merge
skips it as "already present", and a chapter sitting on disk vanishes from
`combined.txt` and from the manifest. Every existing guard passes while it
happens, because the counts still balance. A URL cannot collide with another
chapter's URL.

**The checkpoint records one output per format.** Python records the text
exporter's filename alone, and the markdown exporter guesses its own by
swapping the extension - which produces nothing to guess from when text was not
among the chosen formats. Markdown then skipped every resumed chapter and wrote
a shorter book with no error, while the text exporter refused the same
condition. Both refuse now, and `ISink.OutputsFor` asks each exporter what it
actually wrote. This is why the schema version is 2 rather than 1.

**One definition of when two URLs are the same chapter.** Python has two - an
exact string match for deduplication and the resume check, a normalising one
for comparing link sets - and they disagree in both directions. A table of
contents offering `/ch1` and `/ch1#top` fetches one page twice and writes it
under two numbers; a site that adds a trailing slash reports "identical" and
then refetches everything. `UrlIdentity` is the single definition all three use.

**The screening cache keeps the whole verdict, and the DNS cache is keyed on
the host.** Python keeps one cache, on the full URL, storing only whether the
URL passed. Both halves are wrong: keyed on the URL it never reuses a host's
resolution across the forty images its own comment says it exists to avoid
(measured: forty lookups); storing a boolean throws the reason away, so from
the second sighting a loopback address is reported as a malformed URL. Verdicts
are cached per URL, resolutions per host, and failures are remembered too.

**Starting the page source is failure-atomic.** The driver process can be up
before the browser launch fails. `StartAsync` tears down on that path, so the
process cannot outlive the run; Python starts the source outside the block that
owns its disposal and leaks it.

**A dry run asks for no screenshot and no HTML dump.** Python takes the
screenshot while collecting, before the dry-run check, so `--dry-run
--screenshot` creates the output directory and leaves a `toc.png` in it from a
run that reports having written nothing - contradicting the invariant its own
fetch loop states.

**Every profile value is type-checked where it is read.** Python checks only
the format list, so `max = "twenty"` loads without complaint and dies later
with a TypeError from inside the option translation. Each key is validated
against its expected type, and the message names the file, the key, the type
expected and the value found.

**robots.txt is evaluated for the agent the browser actually sends.** Python
passes no user agent when fetching robots, so a run with `--ua` identifies as
one agent to the site and gets robots decisions meant for another.

**A narrowed table of contents still resumes.** Python treats any shrinkage as
divergence and demands a restart, so asking for fewer chapters than last time
throws away a completed run. A shorter list that is still a prefix leaves every
remaining chapter's number exactly where it was; only a non-prefix removal
shifts the numbering and stays ambiguous.

### Matched on purpose, where the runtimes differ by default

Not divergences - places where the naive C# would have diverged silently.

**Whitespace.** Python's `\s` and `str.strip()` treat U+001C to U+001F as
whitespace; .NET's `\s` and `char.IsWhiteSpace` do not, because those four are
category Cc rather than Z. Every whitespace pattern here uses an explicit class
that includes them, and trimming goes through `Whitespace.Trim`. Measured, not
assumed: U+0085, NBSP, U+2028 and U+3000 match on both sides.

**The Crawl-delay host key.** Python lets callers pass a host string to the
rate limiter, and its two callers build it differently: the override is stored
under the authority while the fetch loop looks it up under the lowercased
host, so an explicit port files the delay somewhere it is never read - while
the log still reports it is being honoured. Every entry point here takes a
`Uri` and derives the key itself; there is no string overload to disagree
through, and a test asserts none appears.

**The character cap counts code points.** Python slices strings by code point;
`value[..150]` in C# counts UTF-16 units, which would cut a 100-emoji title to
75 while reporting the same cap.

**`BlankRun` is unreachable in both.** `\n{3,}` can never match, because the
substitution before it has already collapsed every newline run to a single
newline. Verified by exhaustive search over every input up to length six over
{a, newline, space, tab, CR}, and by the fact that no v1 output in the golden
contains a blank line. The consequence - cleaning does not preserve paragraph
breaks - is pinned by a test in both suites rather than left to be inferred
from a rule that never fires. The regex stays because this is a port.

## Layout

```
Directory.Build.props     shared compiler settings; warnings are errors
Directory.Packages.props  central package versions
src/TocExtractor.Core     policy and the fetch loop; no browser dependency
src/TocExtractor.Browser  the Playwright page source, and nothing else
src/TocExtractor.App      scanner, range planner, sessions, shared pipeline
src/TocExtractor.Cli      System.CommandLine front end and TOML profiles
src/TocExtractor.Desktop  Avalonia window and view models
tests/                    xUnit v3, self-hosting on Microsoft Testing Platform
```

The browser tests live in their own project so `make cs-test` stays fast and
needs no Chromium, the way the Python suite deselects its browser marker. Run
them with `make cs-browser-install` once, then `make cs-test-browser`.

The C# suite reads three fixtures generated on the Python side and copied in at
build time: `corpus.json` (cleaning and filename inputs), `v1_golden.json` (what
v1 produced for them) and `robots_conformance.json` (2048 robots decisions and
the Python evaluator's answer to each). Regenerate them with
`tests/golden/export_corpus.py` and `tests/golden/export_robots_conformance.py`;
Python tests fail if either falls behind its source.

`TocExtractor.Core` must stay free of any browser driver, the fetch loop and
every policy decision are testable without one, and `AssemblyBoundaryTests`
fails the build if that stops being true.

## Running it

The SDK version is pinned in `../global.json`.

```
make cs-build    # or: dotnet build dotnet/TocExtractor.slnx
make cs-test     # core and CLI suites; no browser needed
make cs-lint     # build + dotnet format --verify-no-changes

make cs-browser-install   # once, downloads Chromium
make cs-test-browser
```

To run the extractor itself:

```
make cs-run ARGS='--toc https://example.com/toc \
  --link "ol.toc a" --title "h1.title" --content "article.reader" \
  --out downloads'
```

Both implementations produce byte-identical output for the same input. Against
the repository's own fixtures, `combined.txt` and `book.md` hash the same from
either side.
