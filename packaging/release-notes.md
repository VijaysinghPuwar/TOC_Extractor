A site that is briefly busy no longer costs chapters, a walk going backward no longer leaves a later walk skipping chapters, and two extractions of one book open each page once even when one runs ahead. Also in this release, from 2.6.1 and 2.6.0: chapter titles left out of the PDF too, saved chapters passed without reopening them, and two bugs that only a load test finds.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.dmg` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.dmg` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64-setup.exe` |

Download only the file for your computer; "Source code" is for developers.

**Mac:** open the `.dmg` and drag TOC Extractor onto Applications. The app is not notarized, so the first time macOS may refuse to open it: go to System Settings > Privacy & Security and click Open Anyway.

**Windows:** run the setup and follow the steps; no administrator needed. It adds TOC Extractor to the Start menu. If SmartScreen appears, choose More info, then Run anyway.

## What is new in 2.6.2

From the load test run again on a Mac with the site made unreliable on purpose: one chapter request in ten answered "503, busy", one in five redirected, every answer 150 ms late.

- **Fixed: a site that was briefly busy cost chapters.** A busy page has no story, so it was read as a chapter whose layout did not match, which is never tried again. With one request in ten busy, 219 of 2,000 chapters were lost and only 1 of 40 extractions finished. Now a busy answer (503, 429 or any server error) is tried again after a pause, like a dropped connection: 2,000 of 2,000 with five tries, and 3 lost with the default three, which is what three tries at one in ten predicts. The same run is also more than twice as fast, because a busy page no longer waits out the whole time limit first. A site's "checking you are human" page, which can also come as a 503, is still shown to you.
- **Fixed: after a walk backward, a later walk forward could skip the chapters asked for.** Walking backward, a chapter's "next" link was recorded as the previous chapter. A later walk forward past that saved chapter then went back instead of on, and the chapters it was asked for were reported as not saved, on every try. Records written by 2.6.1 that point backward are now ignored.
- **Fixed: two extractions of one book walking in opposite directions could follow each other's links** and stop early. A page is now only shared between walks going the same way.
- **Fixed: when one of two extractions of one book ran a page or two ahead, the other opened every page again** (156 pages for 100 chapters). The last few pages opened are kept for the one behind: 104-106 for every book now.
- **Fixed (command line and Python window): an ad inside a chapter could fail that chapter.** A frame that an ad loads was treated as the chapter page itself.
- **Fixed (command line): a tab that could not be replaced was lost from the pool**, and with every tab gone the run waited forever.
- **Fixed (Python window): pictures were blocked in the window where you sign in or pass a picture check.**
- Rebuilding a release's installers by hand now builds that release's own source.

## New in 2.6.1

From a real session's log and a stress test of 40 extractions of 50 chapters each through the real window and browser.

- **Fixed: "Leave out chapter numbers and titles" now applies to the PDF too.** It used to change only the TXT book. And a site that repeats a chapter's title as the first line of its text (some sites do) no longer leaves "Chapter 151 ..." for a voice to read.
- **Fixed: saving 51-100 of a book whose site lists only chapter 1 no longer opens 1-50 again first.** A saved chapter is passed by the next link recorded when it was saved, and the walk starts from the nearest saved chapter. On a site read at a careful pace that was ten minutes with nothing to see; now it starts at once.
- **Fixed: two extractions of the same book started together no longer open every page twice.** One opens the page and the other uses it, so both go at the site's full pace.
- **Fixed: with memory short, every book shared a single browser tab.** A 16 GB machine with a browser and a few apps open is already below the 20% free line, and 40 books crawled on one tab. Up to 4 tabs now keep working while memory is short; only below 8% free does everything share one.
- The window shows progress while it follows links to reach a range, and says when a finished book is waiting its turn for its PDF.

## New in 2.6.0

Load tested with 40 extractions running at once, 50 chapters each - 2,000 chapters - against a local stand-in for the reading sites, on Windows 11 with 15.2 GB and 16 logical cores. Not against the real sites: two thousand requests aimed at someone else's server is an attack, not a test, and it would measure their rate limiter rather than this app. Every chapter was re-read from disk afterwards and checked for gaps, empty files, filenames Windows cannot store, and a matching checkpoint.

Two bugs it found. Each has a regression test that was checked against the unfixed code first, so it is known to catch the bug and not merely to pass.

- **Fixed: a chapter could be lost to a refused connection.** A refused or reset connection was being reported as a *blocked address*. Blocked addresses are deliberately never retried, on the reasoning that a disallowed one stays disallowed, so the chapter was abandoned on its first attempt with its retries unspent. A refused connection does not stay refused, and under load it is the ordinary failure rather than a rare one. This is the one most likely to have been noticed on a slower or busier machine.

- **Fixed: a chapter that ran past its time limit could break the next one.** When the limit fired, the browser tab went back into the pool while Chromium was still loading the abandoned page. The next chapter to use that tab collided with it and failed for a reason that had nothing to do with it. The tab is now replaced before it is handed on.

Also:

- **Fixed on Windows: robots.txt was quietly being skipped for some sites.** Windows ships a list of certificate authorities that still contains long-expired ones, and Python does not ask Windows to check certificates: it copies that list and decides for itself, which could make it reject a site every browser on the same machine opens without complaint. Because a robots.txt that cannot be read is treated as permitting everything, those sites silently lost their robots.txt rules. The check is now handed back to Windows.

- **The command line will not accept more at once than the computer can hold.** It lowers the number to what the machine's memory and cores allow, says that it did, and lowers it further when memory is already short.

- **Pictures, video and web fonts are no longer loaded** while the tool reads on its own, since none of them can affect the text it saves. That is bandwidth the site no longer has to serve. It is not a memory saving: measured, the memory is the same, because what a browser costs is its processes, not the images they decode. `--with-images` puts them back.

- Every browser routing step now copes with a request that has already gone away. A live site produced an error from inside the handler, where it reached no caller and left the page waiting for its own timeout instead of failing cleanly.

- **Developer:** `make` works on Windows now, where it had been looking for a folder layout that only exists on macOS and Linux. `make stress` runs the load test above.

## A note on running many at once

Forty at a time is a stress test, not a recommendation. On a 16 GB machine it finishes and every chapter is correct, but memory runs out and the work ends up waiting on the disk. Around eight at a time is what that machine sustains comfortably.
