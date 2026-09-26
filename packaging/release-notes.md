Lighter on your computer, and every chapter checked as it is saved.

## Download

| Your computer | File |
|---|---|
| Mac with Apple silicon (M1 or newer) | `TOC-Extractor-*-macos-arm64.zip` |
| Mac with Intel | `TOC-Extractor-*-macos-x64.zip` |
| Windows 10 or 11 | `TOC-Extractor-*-windows-x64.zip` |

**Mac:** unzip, move TOC Extractor to Applications, then right-click it and choose Open the first time. The app is not notarized, so macOS asks once.

**Windows:** unzip and run `TocExtractor.exe`. If SmartScreen appears, choose More info, then Run anyway.

## What is new

Measured on 40 books saved at once, 50 chapters each, against 2.2.1:

| | 2.2.1 | 2.3.0 |
|---|---|---|
| Average processor use | 272% | 142% |
| Average memory | 5.6 GB | 4.8 GB |
| Peak memory | 8.2 GB | 7.1 GB |
| Time taken | 11 min | 11 min |

- **Adapts to your computer.** An 8 GB Mac opens at most 8 browser tabs, a 24 GB one 40. On a Mac, no new tab opens while the system says memory is short. Idle tabs close, and each tab is emptied once its chapter is read, so the page's adverts stop running while it waits. Pictures, video and web fonts are skipped while the app reads on its own; pages load in full while you sign in or complete a check.
- **Nothing skipped or doubled, and you are told if it is.** Every chapter is compared with its page as it is saved. Text left out, text saved twice, or two chapters with the same text is marked **!** on the chapter and named in the status. When everything checks out, the status says that too.
- **Fixed:** an advert frame that redirected while a chapter loaded was recorded as the chapter's address, and could fail the whole chapter.
- **Fixed:** a page a site served without its story was never retried; it is now tried once more.
- **Fixed:** a range past the end of the book showed "Ready to save"; it now says which chapters the book has.
- **Fixed:** very long books could fail to become a PDF.
