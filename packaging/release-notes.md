Save several books at the same time, with a detailed log of everything the app does.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.zip` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.zip` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64.zip` |

**Mac:** unzip, move TOC Extractor to Applications, then right-click it and choose Open the first time. The app is not notarized, so macOS asks once.

**Windows:** unzip and run `TocExtractor.exe`. If SmartScreen appears, choose More info, then Run anyway.

On first start the app downloads its own copy of Chromium (about 150 MB). Nothing is installed system wide.

## What is new

- **Several books at once.** Press **New extraction**, paste another novel's page, pick chapters and save. You never wait for the first book to finish. Every extraction has its own entry on the left with a progress bar, and there is no limit on how many run together.
- **Two parts of one book at once.** Save 101-150 and 151-200 of the same novel side by side; they share the book's record of what is saved, so neither undoes the other.
- **Tidy folders.** The book's folder now holds just your PDF/TXT books and one Chapters folder with the app's per-chapter working files, so 500 chapters never bury the book.
- **For audiobooks.** In Settings: leave chapter numbers and titles out of the TXT book and Copy text, and remove symbols a text-to-speech voice would read aloud (===== lines, # _ = * ~ and the like). Words and punctuation are never changed.
- **Settings screen.** Press **Settings** at the bottom left: appearance (automatic, light or dark), pace, text options, and **Open log folder**.
- **One detailed log.** Every extraction writes into one CSV log as it works: each page opened and how long it took, retries and why, checks that needed you, every chapter saved or failed with the reason, and full details of any error, including crashes. A column says which extraction each row belongs to.
- **Honest file names.** A book file is named for exactly the chapters in it. Ask for 1-50 and have chapter 27 fail, and the file is "1-26", never "1-50".
- **Never miss a check.** When a site asks to check you're a person, a bar across the window (with a Show me button), a Mac notification and reminders make sure you notice. The app waits for you for as long as it takes, and the check stays put on its tab while you click. Afterwards the window says when it is going slower on purpose, so it never looks stuck.
- **Faster, with fewer checks.** Pages now load one to two seconds apart by default (measured safe on four sites, 100 chapters each). A site that asks to check you're a person is remembered and read at a careful pace from then on, which kept its checks away in testing. Settings, Sites, lists these and lets you choose speed instead.
- **Short chapters flagged.** A chapter far shorter than the rest of its book is named when saving finishes, so a page that loaded without its story never slips into your book unnoticed.
- **Fixed:** books that restart chapter numbers in each part ("Arc 9: Chapter 38") are numbered by their place in the whole book, not by those titles.
- **Fixed:** scans that missed a book's first chapters, sites that write paragraphs as divs, and chapters whose title is laid out differently from the rest.
- **Fixed:** when a browser tab was closed or crashed during a long download, every chapter after it failed with "Target page, context or browser has been closed". The app now opens a fresh tab, or a fresh browser, and carries on.
- **Fixed:** pressing Stop now still writes the TXT and PDF of the chapters saved so far.
- **Fixed:** four-digit chapter numbers no longer look cut off in the chapter boxes.
