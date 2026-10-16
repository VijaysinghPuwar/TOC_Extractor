Cleaner books and steadier saving, from a month of one Windows 11 machine's log and every chapter it saved: the "stolen from NovelFire, report it on Amazon" notices are gone from the text, saving more chapters into a book's folder works every time, and a connection that drops no longer costs chapters.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.dmg` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.dmg` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64-setup.exe` |

Download only the file for your computer; "Source code" is for developers.

**Mac:** open the `.dmg` and drag TOC Extractor onto Applications. The app is not notarized, so the first time macOS may refuse to open it: go to System Settings > Privacy & Security and click Open Anyway.

**Windows:** run the setup and follow the steps; no administrator needed. It adds TOC Extractor to the Start menu. If SmartScreen appears, choose More info, then Run anyway.

## What is new in 2.6.7

From a real Windows 11 machine (16 cores, 15.8 GB): its log of 23,879 rows from 2.4.0 to 2.6.6, and the 4,229 chapters it saved.

- **Fixed: "This tale has been pilfered from NovelFire. If found on Amazon, kindly file a report."** NovelFire writes notices like this into its chapters, reworded every few chapters (stolen, purloined, unlawfully lifted, misappropriated...), and a voice reads them aloud. With Remove ad markers on (the default) they are now removed, whether a paragraph of their own or added after a story sentence, along with a bare "NovelFire" after a paragraph and "View the correct content at NovelFire". Where the site put its own name in place of the word "novel" ("who loves reading NovelFires") the word is put back. pandanovel.co serves NovelFire's chapters, so its books are cleaned the same way. On the machine's 4,229 saved chapters: 54 kinds of notice removed, not one line of story.
- **Fixed: saving more chapters of a book into the same folder.** Four ways it went wrong, all from the log and the folders on disk: a rescan that found the title in another place on the page was refused ("This folder holds another book's progress"); the same book opened from its chapter list went into a second folder; chapters whose files had been deleted stayed "not saved" however often Save was pressed; and deleting the book's folder while it saved stopped both saves with "Could not find a part of the path". Each now saves into the folder, fetches what is missing, and makes a deleted folder again.
- **Fixed: a page holding two chapters ("Chapter 31/32") cut the book short.** The book file stopped at chapter 31 and left the 18 chapters after it out. Such a page now counts for both chapters.
- **Fixed: a connection that drops now and then cost chapters and whole scans.** A page whose server never answered hung until its time limit and left "unobserved" errors in the log; it now fails at once and is tried again. Chapters are tried five times, seconds apart instead of one. The novel page and its list pages are tried three times: one dropped connection had failed a whole scan, and a 2,443-chapter book had listed only 601.
- **Fixed: a crash when the browser was replaced while a book waited on a check** ("Cannot access a disposed object").
- **Fixed: titles like "Lu Yun&#039;s crazy outburst!"**, which a voice reads as "ampersand hash"; and a title that was really a stray sentence is replaced by the chapter's own "Chapter 34: ..." line.
- **Faster on novelfire.net:** it had been read at a page every 12 seconds for weeks, 10 minutes for 50 chapters, because of two checks the old (before 2.6.3) detector imagined. Those records are forgotten; a real check teaches the careful pace again.
- **Opening the app twice** brings the open window forward, instead of starting a second copy that cannot use the browser.

Tested before release: 3,048 automated tests, with new ones for each fault above (those for the crashes and the same-folder saves were first run against 2.6.6, and failed there with the errors from the log); the stress test of 40 extractions of 50 chapters at once, then 10 more ranges, three times, all saved and checked (3 min 53 s, against 4 min 59 s for 2.6.6 on the same machine minutes apart); and live saves from pandanovel.co (68 of 68 chapters found, where 2.6.6 found 1), novelfire.net (a 2,452-chapter book listed in full) and fanmtl.com. royalroad.com and freewebnovel.com put a Cloudflare check in front of automated browsers, which you can pass in the app's browser window; wtr-lab.com does not allow tools like this app.

## New in 2.6.6

A 429-chapter book was checked word for word against its site: every chapter matched. That check found text the site itself carries that no reader wants.

- **Fixed: hidden site-name tags in the text.** Some sites tag chapters copied from another site with that site's name in look-alike letters from other alphabets, after the last sentence of a paragraph, often cut to one or two letters. 94 of the book's chapters had one, and a text-to-speech voice reads them aloud. With Remove ad markers on (the default) they are now removed. Ordinary words, accented ones included, are kept.
- **Fixed: broken characters** (a question mark in a box) that sites leave in their text are removed too.
- Chapters you saved before keep their text. To get a book without the tags, tick Start over and press Save.

## New in 2.6.5

Every part of the window was rendered at the smallest supported size (940 x 600) and at 1280 x 820 and 1920 x 1080, in light and dark, filled with the longest text a site or a failure can produce. A new test fails if any control ends outside the box it sits in.

- **Fixed: the progress bar in each extraction on the left ran past its card.** It asked for 200 pixels in a list 173 wide.
- **Fixed: a long error pushed the chapter list off the window.** Long messages now scroll inside their own box, and Open folder stays in view.
- **Fixed: a long failure reason squeezed the chapter's title to nothing.** Titles keep their room; the reason is cut short, with the whole of it on hover.
- **Fixed: long file names, site names and chapter titles** were cut off with no way to read them. Each one now ends in "..." and shows in full on hover.
- **Less repetition.** The line above the progress said "Done. 9 of 9 saved." right over "9 of 9 saved". It now says "Done.", and while saving it gives only what the progress line does not. Before a save there is no empty progress bar or "Scan a novel, choose chapters, then save." line under the steps that already say so.
- **Shorter text in Settings**, and the Save button lines up with the boxes above it when Stop is hidden.

Stress tested before release: 40 extractions of 50 chapters at once in the app, all 2,000 chapters saved in under two minutes.

## New in 2.6.4

From a real session's log on Windows 11 (16 cores, 15.8 GB) and live tests on thirteen books.

- **Fixed: novellunar.com found one chapter.** Its chapter list sits behind a "Chapters (1472)" tab that loads only when pressed. The scan now presses it, reads the site's count, and reaches the chapters past the first fifty by their address. The book is also named from its own title rather than the site's logo, so files are no longer called "Novellunar 1-50.txt".
- **Fixed: novelfire.net listed 772 of 1,472 chapters** of End of the Magic Era. Its page buttons show 1-6 and 14-15 and the rest only on later pages; a repeat of page 1 ended the reading early. Every list page is now read, lowest first.
- **Faster: extractions of the same book share their scan.** Saving 1-50, 51-100 and 101-150 in three extractions read the same list three times; the third scan took six and a half minutes and slowed the other two saves. Now the second and third start saving straight away (within 20 minutes of the first scan). Pressing Scan again still scans afresh.
- **Fixed: "Signed in." on sites where nobody signed in.** Sites built with NextAuth give every visitor cookies with "auth" in their names, which the app took for a sign-in.

Tested on this release: novellunar.com, novelfire.net, ranobes.top, fanmtl.com and mtl-novel.com scan and save; royalroad.com, freewebnovel.com and novellive.com put a Cloudflare check in front of automated browsers, which you may be able to pass in the app's browser window; wtr-lab.com does not allow tools like this app.

## New in 2.6.3

From a real session's log on Windows 11 and live tests on nine sites.

- **Fixed: the chosen chapters changed by themselves.** The From and To boxes were held to the chapters the last scan found. Scanning again, or a site that listed only its first chapters this time, quietly turned 1-50 into 1-2 (or 1-1), and Save then saved that. The boxes now keep what you typed. A range past what the scan found says so ("The scan found chapters 1 to 2 only") and Save waits, instead of saving fewer chapters. Scanning a different book still starts from its whole range.
- **Fixed: "Needs you in the browser" with nothing to do.** A short chapter whose story said "her body tensed for just a moment" was taken for the site's "Just a moment..." check page, so saving stopped at that chapter and waited. Everyday words now count only as a page's title, which is where check pages put them, and a page already showing its story is never taken for a check. novelfire.net chapter 8 of Supreme Daily Login System, which stopped every save of 1-50, now saves like any other.
- **Fixed: one false check slowed a site to a page every 12 seconds, for good.** Sites the old check saw ask only once are forgotten, so they start fast again; a real check teaches the careful pace once more.
- **Fixed: some sites' robots.txt rules were partly ignored.** A site that writes each rule under its own "User-agent: *" had only its first rule followed, and rules about the part of an address after "?" never matched. Both now follow the robots.txt standard. Sites whose robots.txt refuses chapter pages (wtr-lab.com, novelping.com) now say so plainly instead of "could not find the story text".

Tested on this release: novelfire.net, royalroad.com, ranobes.top, fanmtl.com and mtl-novel.com scan and save; freewebnovel.com and lightnovelpub.me put a security check in front of automated browsers, which you may be able to pass in the app's browser window; wtr-lab.com and novelping.com do not allow tools like this app.

## New in 2.6.2

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
