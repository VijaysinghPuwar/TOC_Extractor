Reads more sites correctly, and never leaves you waiting on a check that cannot pass.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.zip` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.zip` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64.zip` |

**Mac:** unzip, move TOC Extractor to Applications, then right-click it and choose Open the first time. The app is not notarized, so macOS asks once.

**Windows:** unzip and run `TocExtractor.exe`. If SmartScreen appears, choose More info, then Run anyway.

## What is new

Tested live on new sites, then on 40 books saved at once, 50 chapters each: 1,970 chapters, every one checked on disk, with none missing, none saved twice, no retries and no errors, in 15.5 minutes (average 1.35 processor cores, 4.4 GB of memory).

- **Sites with numbered ids in chapter addresses now work.** Where an address carries the site's own id (`/chapter/4815162/the-gate`), that id was read as the chapter number, so a 104-chapter book scanned as "1 to 173,027". Chapters are now numbered by their titles, or by their place in the book, and each is opened directly.
- **Whole chapter lists on pages that page themselves.** A list with numbered buttons that have no address showed only its first page; the full list is now read.
- **Fixed:** a "Next" link could be mistaken for a search button with the same look.
- **Fixed:** a security check that says it cannot finish, for a person either, left the app waiting for good. It now stops within seconds with a clear message, and never ends or takes over another site's check you are still completing.
- **Fixed:** after a site's check failed, the scan kept opening that site's pages, showing you a new check each time. It now stops.
- A site whose robots.txt closes the novel page now says so plainly.
