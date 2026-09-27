Made for Windows as much as for the Mac: many books at once without running the computer out of memory, and no book lost to a busy moment.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.dmg` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.dmg` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64-setup.exe` |

Download only the file for your computer; "Source code" is for developers.

**Mac:** open the `.dmg` and drag TOC Extractor onto Applications. The app is not notarized, so the first time macOS may refuse to open it: go to System Settings > Privacy & Security and click Open Anyway.

**Windows:** run the setup and follow the steps; no administrator needed. It adds TOC Extractor to the Start menu. If SmartScreen appears, choose More info, then Run anyway.

## What is new

Stress tested on Windows 11 (16 GB, 8 cores) with 25 extractions at once. On
sites without a check, 25 books of 10 chapters finished in 4.5 minutes with
every one checked on disk, the same speed as 2.4.0. The Mac is unchanged.

- **Fixed: with many books at once, one could stop with "Collection was
  modified".** When two sites asked for a check at the same moment, the scan
  that was waiting could fail as other books opened and closed tabs. It
  happened in every 25-book run on Windows where sites asked for checks, and
  is fixed on every system.
- **Windows: the app now notices when memory is short.** It used to open
  browser tabs up to its limit whatever else the computer was doing, and on a
  16 GB machine with other programs open, memory fell to 6% free. Now, as on
  the Mac, it stops opening tabs and closes idle ones while memory is short.
- **Windows: the browser moved to the local app folder.** Chromium and its
  profile (about 430 MB) now live in `%LOCALAPPDATA%\TOC Extractor`, not the
  roaming folder that work networks copy at every sign-out. The first launch
  moves them over, keeping any site you signed in to.
- **Windows: the taskbar button flashes** when a site needs you, where a Mac
  shows a notification.
- **A slow novel page is tried once more** before the scan gives up, as a slow
  chapter already was. In a 25-book run one site took over 30 seconds once, and
  that whole book was lost.
- Windows: saving progress no longer fails when antivirus or search indexing
  has the file open for a moment.
- Windows: a book whose title ends in dots, or is a name Windows reserves
  (CON, NUL, COM1), gets a folder Windows can create; long paths work; the
  message for a browser left open from an earlier run now appears on Windows
  too; and the app starts faster.
