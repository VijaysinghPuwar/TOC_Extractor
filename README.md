<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-dark.svg">
    <img src="docs/images/logo.svg" alt="TOC Extractor" width="440">
  </picture>
</p>

<p align="center">
  Save the chapters of a web book or series as clean text files you can read offline.
</p>

---

## What is this?

Many websites publish long writing as a series of chapters, with one page that
lists them all. That list is called a **table of contents** (the "TOC" in the
name).

TOC Extractor takes that one page, visits each chapter in order, pulls out
just the story text (no menus, no ads, no comment sections), and saves it on
your computer. You end up with one tidy file per chapter, plus one file with
everything joined together.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/how-it-works-dark.svg">
    <img src="docs/images/how-it-works.svg" alt="How it works: contents page, find chapters, read politely, your files" width="860">
  </picture>
</p>

It works on almost any site, because it does not have anything built in for
one particular website. Instead, you tell it three things about the site you
are using:

| You tell it | In plain words | Example |
|---|---|---|
| **Link** | Which links on the contents page are chapters | `ol.toc a` |
| **Title** | Where the chapter name sits on a chapter page | `h1.title` |
| **Content** | Where the actual story text sits | `article.reader` |

These are called *CSS selectors*. They look technical, but they are just
short labels that point at parts of a web page. The section
[Finding the three labels](#finding-the-three-labels) walks through it.

## Good manners are built in

This tool is meant for content you own or have permission to save. It is
built to behave like a patient reader, not a bot hammering a website:

- **It follows each site's rules.** Websites publish a file called
  `robots.txt` that says what automated tools may visit. If a site says no,
  the tool stops. There is no switch to turn this off.
- **It takes its time.** It waits between pages, and if a site asks for an
  even longer wait, it waits longer. It never goes faster than you set.
- **It does not break in.** No captcha solving, no disguises, no hidden
  tricks. If a site needs you to sign in, you do that yourself, by hand.
- **It stays on the public web.** Links pointing at private or local network
  addresses are refused.

Please respect each site's Terms of Service and its limits.

## What you need

- A Mac or Linux computer (Windows may work, but it is not tested)
- Python 3.11 to 3.14 (free, from [python.org](https://www.python.org/downloads/))
- About 5 minutes for the first setup

Playwright, the only runtime dependency, is installed for you in the steps
below. It lets the tool open web pages the same way a normal browser does.

## Getting started

Open the **Terminal** app and run these lines one at a time.

**1. Download the project**

```bash
git clone https://github.com/VijaysinghPuwar/TOC_Extractor.git
cd TOC_Extractor
```

**2. Set it up** (only needed once)

```bash
make setup
```

This creates a private workspace for the tool, installs what it needs,
downloads a browser for it to use, and checks that the app window will work.
If you do not have `make`, run these four lines instead:

```bash
python3 -m venv .venv
source .venv/bin/activate
pip install -e ".[dev]"
python -m playwright install chromium
```

**3. Open the app**

```bash
make gui
```

## Using the app

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/three-buttons-dark.svg">
    <img src="docs/images/three-buttons.svg" alt="The three buttons: Launch browser, I'm Ready, Start extraction" width="780">
  </picture>
</p>

1. Paste the address of the contents page into **Table of contents URL**.
2. Fill in the three labels: **Chapter link**, **Title**, and **Content**.
3. Pick an **Output folder**. This is where your files will go.
4. Press **1. Launch browser**. A browser window opens on the contents page.
5. If the site needs you to sign in or tick a "not a robot" box, do it in that
   window now, like you normally would.
6. Press **2. I'm Ready**.
7. Press **3. Start extraction**. Each chapter appears in the list as it saves.

Press **Stop** at any time. Nothing is lost: see
[Stopping and starting again](#stopping-and-starting-again).

## What you get

```
downloads/
  001 - Chapter One.txt
  002 - Chapter Two.txt
  003 - Chapter Three.txt
  combined.txt          every chapter, joined in order
  manifest.jsonl        a record of what was saved (with the jsonl format)
```

You can choose the file type with `--format` (or the tick boxes in the app):

| Format | What it is good for |
|---|---|
| `text` | Plain text. Opens anywhere. This is the default. |
| `markdown` | Keeps chapter headings. Can be turned into an e-book with a free tool called pandoc: `pandoc book.md -o book.epub` |
| `jsonl` | A detailed record of every chapter, for people who want to process the results further |

Web addresses are removed from the chapter text by default so it reads
cleanly. The tool tells you how many it removed. Tick **Include source URLs**
(or use `--include-links`) to keep them.

## Stopping and starting again

You can stop a run halfway and start it again later. The tool remembers which
chapters it already saved and only fetches the ones that are missing. If the
site has added new chapters since last time, it picks those up too.

To throw away saved progress and start fresh, tick **Ignore saved progress**
(or use `--force`).

## Finding the three labels

This is the only fiddly part, and you only do it once per site.

1. Run a **dry run**. It looks at the contents page, lists the chapter links
   it found, and saves nothing:

   ```bash
   python -m toc_extractor --toc https://example.com/toc --link "a" --title "h1" --content "body" --dry-run --dump-html --screenshot
   ```

   The title and content labels are placeholders for now; a dry run does not
   use them. This also saves `toc.html` (the page) and `toc.png` (a picture of it) to
   the output folder. If the picture shows a sign-in wall, use the app instead
   so you can sign in first.

2. **Narrow the link label.** Start with `a` (every link) and make it more
   specific until the dry run lists only chapters.

3. **Check the content label.** Open one chapter in Chrome, right click the
   story text, choose **Inspect**, and look at the box that wraps the text.
   A good content label picks just the story, not the whole page.

4. Save your three labels in a small file, called a **profile**, so you never
   have to type them again (see below).

## Saving your settings in a profile

A profile is a short text file that remembers the labels and options for one
site. There is a ready example at `profiles/example.toml`.

```toml
[selectors]
link = "ol.toc a"          # every chapter link on the contents page
title = "h1.title"         # the chapter title on a chapter page
content = "article.reader" # the box holding the story text

[options]
min_delay = 1.5            # seconds to wait between pages, at least
max_delay = 3.0            # and at most
concurrency = 2            # chapters fetched at the same time
max = 25                   # chapters per run
formats = ["text", "jsonl"]
include_links = false
```

Use it like this:

```bash
python -m toc_extractor --profile my-site.toml --toc https://example.com/toc
```

Anything you type on the command line wins over the profile, so you can
change one setting for one run without editing the file. If the profile has a
typo in a setting name, the tool refuses it and lists the correct names,
rather than quietly ignoring it.

## Common problems

**"Tk is missing" or the app window will not open (Mac).**
The Python that comes from Homebrew does not include the part that draws
windows. Install Python from [python.org](https://www.python.org/downloads/),
then rebuild the workspace with it:

```bash
make clean
make setup PYTHON=/Library/Frameworks/Python.framework/Versions/3.14/bin/python3
```

The command line tool works without Tk either way.

**Every site fails with a certificate error (Mac).**
Python from python.org needs one extra step after installing. Open the
Python folder in Applications and double click `Install Certificates.command`.

**The first run is slow to start.**
The first time the downloaded browser opens, macOS checks it. This takes a
moment and only happens once. It has not frozen.

**The app cannot open the browser.**
A browser from an earlier run may still be open. Close it, then try again.

**Two chapters have the same name.**
The second one gets a number added (for example `Chapter One (2).txt`) so
nothing is overwritten. The log mentions it.

## Things it cannot do

- It cannot make e-books directly. Export `markdown` and use pandoc, as shown
  above.
- If a site's `robots.txt` cannot be reached at all, the rules say the tool
  may continue. It will, but it warns you clearly.
- It checks every page it visits, but images and scripts on a page are only
  blocked, not checked step by step.
- A rare network trick called DNS rebinding could get past the private
  address check. Closing that needs control the browser does not offer.

## Version history

**2.0.1** (2026-09-25)
- Fixed: when a chapter page loaded but your content label matched nothing,
  the tool wrongly called it a timeout and tried twice more. It now says the
  label matched nothing and moves on straight away, which is faster and far
  easier to fix.
- Fixed: a dry run with `--dump-html` now really saves `toc.html`. Before, it
  quietly skipped it.
- Fixed the automatic checks on GitHub, which had been failing since 2.0.0.
  The checks were set up wrongly; the tool itself was not affected.
- Rewrote this guide for people who are new to the project, with pictures.
- Updated the GitHub automation to current versions.

**2.0.0** (2026-08-08)
- Rebuilt from three separate scripts into one tested program.
- New: profiles, resume after stopping, Markdown and JSONL output, fetching
  several chapters at once, and a rebuilt app window.
- New: follows `robots.txt` and each site's requested wait time.
- Changed: file names with runs of odd characters are tidier. `Chapter//One`
  used to become `Chapter__One` and is now `Chapter_One`. Chapter text is
  exactly the same as before.

**1.0.0**
- First release: a command line script and a simple app window.

---

<details>
<summary><strong>For developers</strong></summary>

### Commands

```bash
make setup       # venv, install, Chromium, Tk check
make deps        # venv and install only, no browser
make lint        # ruff check and format check
make typecheck   # mypy, strict, over src/
make test        # full suite, browser tests included
make test-fast   # skips browser tests; what the CI matrix runs
make run ARGS='--toc ... --link ...'
make gui
```

CI runs lint, strict mypy, the test suite on Python 3.11 to 3.14 on Linux and
3.14 on macOS, and the browser tests on one job with Chromium cached.

After changing a flag, regenerate the reference below:

```bash
./.venv/bin/python scripts_gen_readme.py
```

To read the v1 to v2 rewrite: `git diff v1.0.0..v2.0.0`.

### Command line reference

<!-- cli-reference: generated, do not edit by hand -->

```
usage: toc-extractor [-h] [--version] [--gui] [--profile PROFILE] --toc TOC
                     [--link LINK] [--title TITLE] [--content CONTENT]
                     [--max MAX] [--out OUT] [--include-links]
                     [--format FORMAT] [--no-strip-ads] [--dry-run] [--force]
                     [--dump-html] [--screenshot] [-v] [-q] [--ua UA]
                     [--storage-state STORAGE_STATE] [--headful]
                     [--timeout TIMEOUT] [--min-delay MIN_DELAY]
                     [--max-delay MAX_DELAY] [--retries RETRIES]
                     [--wait-after-load WAIT_AFTER_LOAD]
                     [--concurrency CONCURRENCY] [--allow-private-hosts]

Extract chapter text from a table-of-contents page using CSS selectors you supply. Use only on content you own or are permitted to access.

options:
  -h, --help            show this help message and exit
  --version             show program's version number and exit
  --gui                 Open the graphical front end instead of running from
                        the command line

source and selectors:
  --profile PROFILE     TOML file holding the three selectors and optional
                        defaults. Flags you pass explicitly override it. See
                        profiles/example.toml.
  --toc TOC             TOC URL (must start with http/https)
  --link LINK           CSS selector for chapter links on the TOC page
  --title TITLE         CSS selector for title on a chapter page
  --content CONTENT     CSS selector for content on a chapter page

output:
  --max MAX             Max chapters to fetch (default: 20)
  --out OUT             Output folder (default: downloads)
  --include-links       Include source URL in saved files
  --format FORMAT       Output format, repeatable (default: text). Choices:
                        jsonl, markdown, text. For EPUB, export markdown and
                        run: pandoc book.md -o book.epub
  --no-strip-ads        Do NOT strip common ad markers from text

behaviour:
  --dry-run             List discovered chapter URLs and exit
  --force               Discard any saved progress and start over (resume is
                        the default)

debugging:
  --dump-html           Save TOC HTML to out/toc.html
  --screenshot          Save TOC screenshot to out/toc.png
  -v, --verbose         Show debug output
  -q, --quiet           Show warnings and errors only

browser:
  --ua UA               Custom User-Agent string
  --storage-state STORAGE_STATE
                        Path to Playwright storage state JSON (reuses login)
  --headful             Run headed (GUI). Default is headless.
  --timeout TIMEOUT     Navigation timeout ms (default: 25000)

politeness:
  --min-delay MIN_DELAY
                        Min delay between chapters (s)
  --max-delay MAX_DELAY
                        Max delay between chapters (s)
  --retries RETRIES     Retries per chapter on errors (default: 2)
  --wait-after-load WAIT_AFTER_LOAD
                        Extra settle wait per page (ms)
  --concurrency CONCURRENCY
                        Chapters fetched at once (default: 3). The per-host
                        delay still applies.
  --allow-private-hosts
                        Permit hosts resolving to loopback or private ranges
                        (for local testing)
```

<!-- /cli-reference -->

### How it is built

```
TOC page -> parser -> politeness -> fetcher -> exporters -> files
            vets      robots and    bounded    text
            links     rate limit    workers    markdown
                                               jsonl
```

`PageSource` is the seam between the fetch loop and Playwright. Most tests run
against a dict-backed stub with no browser at all; only browser-marked tests
open Chromium.

A few decisions look odd without context. Each came from a measured failure:

- **The rate limiter is acquired inside the concurrency semaphore.** Acquiring
  it first lets every pending worker queue on the limiter, and the concurrency
  ceiling stops meaning anything. A test runs five workers against one host and
  asserts the spacing holds.
- **Redirects are followed by hand.** Playwright's `route` handler fires once
  per navigation, not per redirect hop. The handler fetches with
  `max_redirects=0`, validates each target, and aborts on the first disallowed
  hop. Because the body is fulfilled at the original URL, the final URL is
  tracked in the handler rather than read from `page.url`.
- **One browser page per worker.** Two concurrent `goto()` calls on one page
  abort each other with `net::ERR_ABORTED`. That passed 447 tests and failed
  on the first live run, so the stub now models page exclusivity.
- **"Could not check robots.txt" is not "no rules".** RFC 9309 treats a 404 as
  no restrictions. Treating every failure that way meant a local TLS problem
  silently marked every site unrestricted. Non-404 failures now warn loudly.
- **The robots override needs evidence of a session.** After the GUI's manual
  sign-in step, a signed-in session may pass a `Disallow`. It is keyed on the
  session carrying cookies, not on the Ready button, so an anonymous run still
  gets a hard refusal. Every overridden rule is shown and recorded in the
  manifest.

### One invariant, four checks

Every link is accounted for: `raw == kept + rejected + truncated`. It is
checked in the `LinkCollection` constructor, at the `PageSource` error
boundary, in `run()` before it returns, and where output is merged. Each check
was added after a real bug where content disappeared silently:

| What vanished | How |
|---|---|
| SVG-anchor chapters | `SVGAnimatedString` arrives in Python as `{}`, truthy in JavaScript and falsy in Python, so an `if link` filter dropped it |
| A whole run | a stdlib `TimeoutError` escaped the retry vocabulary and killed the task group |
| One chapter, silently | `asyncio.TaskGroup` absorbs a child's `CancelledError` and discards the task with it |
| Everything a resume did not refetch | the merged file was rebuilt from only the chapters that run fetched |

### Resume rules

Resume is the default, keyed on URL; there is no `--resume` flag. Growth at
either end of the TOC resumes and fetches only new chapters. Removals or
reordering refuse with specifics, because chapter numbers would stop matching
files already written. Chapters added at the start are numbered in fetch order,
and the tool says so.

### Filenames

APFS is case-insensitive, so colliding titles get a numeric suffix and a log
line. Names are normalised to NFC, capped at 255 bytes, and leading dots are
stripped so `.Prologue` does not vanish from Finder.

</details>

## License

MIT. See [LICENSE](LICENSE).
