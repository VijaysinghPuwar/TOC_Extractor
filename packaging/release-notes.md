Fixes from a 20-book stress test, and an up-to-date guide.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.zip` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.zip` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64.zip` |

**Mac:** unzip, move TOC Extractor to Applications, then right-click it and choose Open the first time. The app is not notarized, so macOS asks once.

**Windows:** unzip and run `TocExtractor.exe`. If SmartScreen appears, choose More info, then Run anyway.

## What is new

Twenty books were saved at once across five sites, 50 chapters each, to find what was left to fix:

- **Fixed:** two different novels with the same title (the same book on two sites) were given one folder, and the second was refused. The second now gets a folder of its own, named with its site.
- **Fixed:** when a site's first-chapter link leads to a cast list or a roster, that page could stand in for chapter 1 in the book. The page whose title names the chapter is now always used.
- **Fixed:** the time estimate ignored a site's careful pace ("about 1 min" for what takes ten). It now says how long, and why.
- **Fewer wasted pages:** the scan stops paging through a list as soon as a page adds nothing.
- **Guide:** new screenshots of this version, and the lines of code in each language.

Everything from 2.2.0 is here too: several books at once, one detailed log, honest file names, audiobook options, learned careful pace for sites that check, and the fix for chapters failing after a browser tab closed.
