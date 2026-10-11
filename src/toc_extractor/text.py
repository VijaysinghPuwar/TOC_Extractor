"""Canonical text cleaning and filename sanitisation.

This is the single implementation that replaces the two drifted copies in the v1
scripts. `tests/golden/v1_golden.json` records what those copies did; anything
here that differs from it is a deliberate change listed in the CHANGING section
of `tests/golden/corpus.py`.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

# v1 capped titles at 150 characters. Kept because the golden pins it.
NAME_MAX_CHARS = 150

# APFS and HFS+ cap a single path component at 255 *bytes*, not characters. v1
# used a character cap only, so a 150-character CJK title produced a 450-byte
# component that the filesystem rejects.
NAME_MAX_BYTES = 255

_AD_PATTERNS = (
    re.compile(r"Ads by\s+\w+", re.IGNORECASE),
    re.compile(r"Sponsored\s+Content", re.IGNORECASE),
)
# Some sites tag copied chapters with the source site's name spelled in
# look-alike characters from other alphabets (a bold R, a Greek A, a
# double-struck N and so on), often cut to a letter or two, after the last
# sentence of a paragraph. Spellings vary endlessly, so the tag is recognised
# by its shape, not its letters: a final "word" after sentence-ending
# punctuation, in a line of plain Latin prose, that is one or two unusual
# letters, or mixes alphabets or symbol sets. Real
# words, accented ones included, keep to one alphabet. Broken characters
# (U+FFFD) carry no text and always go.
_WATERMARK_TAIL = re.compile(
    "(?<=[.!?\"'\u201d\u2019\\]\u3011)\u2026*~\u300d\u300f])[ \t]+(\\S{1,24})[ \t]*$", re.MULTILINE
)
_REPLACEMENT = "\ufffd"
# Symbol categories a look-alike may borrow from (currency, other, math).
_SYMBOLS = ("Sc", "So", "Sm")
# The plain-letter form of the same tag, cut to one letter.
_WATERMARK_WORDS = frozenset({"R"})


def _graphemes(token: str) -> list[str]:
    """Characters with their combining marks attached."""
    out: list[str] = []
    for ch in token:
        if out and unicodedata.category(ch).startswith("M"):
            out[-1] += ch
        else:
            out.append(ch)
    return out


# Where a character comes from, coarsely. Kept identical to TextCleaner.cs so
# both implementations make the same call on the same text.
_FAMILIES = (
    (0x0000, 0x02FF, "latin"),
    (0x1D00, 0x1DBF, "latin"),
    (0x1E00, 0x1EFF, "latin"),
    (0x2C60, 0x2C7F, "latin"),
    (0xA720, 0xA7FF, "latin"),
    (0xAB30, 0xAB6F, "latin"),
    (0x0370, 0x03FF, "greek"),
    (0x1F00, 0x1FFF, "greek"),
    (0x0400, 0x052F, "cyrillic"),
    (0x2DE0, 0x2DFF, "cyrillic"),
    (0xA640, 0xA69F, "cyrillic"),
    (0x20A0, 0x20CF, "currency"),
    (0x2100, 0x214F, "letterlike"),
    (0xFF00, 0xFFEF, "fullwidth"),
    (0x1D400, 0x1D7FF, "math"),
)


def _family(ch: str) -> str:
    """The alphabet or symbol set a character comes from."""
    cp = ord(ch)
    for first, last, name in _FAMILIES:
        if first <= cp <= last:
            return name
    return f"block-{cp >> 7}"


def _is_watermark(token: str, before: str) -> bool:
    if token in _WATERMARK_WORDS:
        return True
    if token.isascii():
        return False
    letters = [c for c in before if c.isalpha()]
    if not letters or sum(c.isascii() for c in letters) < 0.9 * len(letters):
        return False
    shapes = _graphemes(token)
    if any(not (s[0].isalpha() or unicodedata.category(s[0]) in _SYMBOLS) for s in shapes):
        return False
    # At least one letter. The letterlike block (U+2100-214F) holds
    # look-alikes such as a prescription R, so it counts as letters here.
    if not any(s[0].isalpha() or "\u2100" <= s[0] <= "\u214f" for s in shapes):
        return False
    if len(shapes) <= 2:
        # Not a short word in ordinary accented letters ("J\u00f3", "L\u00fc").
        return any(not ("\u00c0" <= c <= "\u00ff" or c.isascii()) for c in token)
    return len(shapes) <= 9 and len({_family(s[0]) for s in shapes}) >= 2


def _drop_watermark(match: re.Match[str]) -> str:
    line_start = match.string.rfind("\n", 0, match.start()) + 1
    before = match.string[line_start : match.start()]
    return "" if _is_watermark(match.group(1), before) else match.group(0)


def strip_watermarks(text: str) -> str:
    """Remove broken characters and look-alike site tags left at the end of paragraphs."""
    text = text.replace(_REPLACEMENT, "")
    return _WATERMARK_TAIL.sub(_drop_watermark, text)


_URL_RE = re.compile(r"https?://\S+")
_WS_BEFORE_NL = re.compile(r"\s+\n")
_WS_AFTER_NL = re.compile(r"\n\s+")
_BLANK_RUN = re.compile(r"\n{3,}")

_LINEBREAKS = re.compile(r"[\t\n\r]")
# Remaining C0 controls and DEL. v1 handled only tab/newline/CR, so a title
# carrying a NUL produced a name that os.open rejects outright.
_CONTROLS = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
# Canonical form is the v1 CLI regex, which has the trailing + that the v1 GUI
# regex lacked. Collapsing runs *increases* collision probability: "A//B" and
# "A/B" both become "A_B". That is only acceptable because FilenameAllocator
# resolves collisions case-insensitively before anything touches the disk. Drop
# the allocator and this line becomes silent data loss.
_FORBIDDEN = re.compile(r'[\\/:*?"<>|]+')
_WHITESPACE = re.compile(r"\s+")
_LEADING_DOTS = re.compile(r"^\.+")


@dataclass(frozen=True, slots=True)
class CleanedText:
    """Cleaned body text plus what cleaning removed.

    `stripped_urls` exists because v1 deleted URLs from prose with no record.
    The deletion itself is unchanged; returning the count as a peer of the text
    is what makes it impossible for a caller to report the text without the loss.
    """

    text: str
    stripped_urls: int


def clean_text(text: str, *, remove_links: bool = True, strip_ads: bool = True) -> CleanedText:
    """Normalise extracted body text.

    Defaults match v1 exactly. Both flags are wired to CLI options, so the
    substitution order below is load-bearing: URLs are removed before whitespace
    collapses, which is why removing a URL on its own line leaves no blank gap.
    """
    if strip_ads:
        for pattern in _AD_PATTERNS:
            text = pattern.sub("", text)
        text = strip_watermarks(text)

    stripped_urls = 0
    if remove_links:
        text, stripped_urls = _URL_RE.subn("", text)

    text = _WS_BEFORE_NL.sub("\n", text)
    text = _WS_AFTER_NL.sub("\n", text)
    text = _BLANK_RUN.sub("\n\n", text)
    return CleanedText(text=text.strip(), stripped_urls=stripped_urls)


def truncate_utf8(value: str, max_bytes: int) -> str:
    """Trim to at most `max_bytes` UTF-8 bytes without splitting a character."""
    encoded = value.encode("utf-8")
    if len(encoded) <= max_bytes:
        return value
    return encoded[:max_bytes].decode("utf-8", errors="ignore")


def safe_filename(
    name: str,
    *,
    max_chars: int = NAME_MAX_CHARS,
    max_bytes: int = NAME_MAX_BYTES,
) -> str:
    """Turn a chapter title into one safe path component.

    Not collision-free on its own — use FilenameAllocator for that.
    """
    # NFC first, because macOS returns NFD from the filesystem while the DOM
    # supplies NFC. Without this the same title compares unequal depending on
    # whether it came from a page or from a directory listing, which breaks
    # both dedup and resume.
    name = unicodedata.normalize("NFC", name)
    name = _LINEBREAKS.sub(" ", name).strip()
    name = _CONTROLS.sub("", name)
    name = _FORBIDDEN.sub("_", name)
    name = _WHITESPACE.sub(" ", name)
    # A leading dot hides the file in Finder and in ls, so a chapter titled
    # ".Prologue" silently vanishes from the output directory.
    name = _LEADING_DOTS.sub("", name).strip()

    name = name or "untitled"
    name = name[:max_chars]
    return truncate_utf8(name, max_bytes) or "untitled"


@dataclass(frozen=True, slots=True)
class AllocatedName:
    """One allocated path component and how it was reached."""

    name: str
    collided_with: str | None = None

    @property
    def deduplicated(self) -> bool:
        return self.collided_with is not None


class FilenameAllocator:
    """Hands out unique path components for one output directory.

    Uniqueness is case-insensitive because APFS is case-insensitive by default:
    "Chapter One.txt" and "chapter one.txt" are the same file, and v1 let the
    second silently overwrite the first.
    """

    def __init__(self, *, suffix: str = ".txt", max_bytes: int = NAME_MAX_BYTES) -> None:
        self._suffix = suffix
        self._max_bytes = max_bytes
        self._taken: dict[str, str] = {}

    @staticmethod
    def _key(value: str) -> str:
        # NFC and casefold together, matching how the filesystem compares.
        return unicodedata.normalize("NFC", value).casefold()

    def allocate(self, index: int, title: str) -> AllocatedName:
        """Allocate `NNN - Title.txt`, deduplicating against earlier calls."""
        prefix = f"{index:03d} - "
        reserved = len(prefix.encode("utf-8")) + len(self._suffix.encode("utf-8"))
        base = safe_filename(title, max_bytes=max(1, self._max_bytes - reserved))

        candidate = f"{prefix}{base}{self._suffix}"
        key = self._key(candidate)
        if key not in self._taken:
            self._taken[key] = candidate
            return AllocatedName(name=candidate)

        original = self._taken[key]
        counter = 2
        while True:
            marker = f" ({counter})"
            # Re-trim the title so the marker cannot push the component over the
            # byte limit on a name that was already at it.
            trimmed = truncate_utf8(
                base, max(1, self._max_bytes - reserved - len(marker.encode("utf-8")))
            )
            candidate = f"{prefix}{trimmed}{marker}{self._suffix}"
            key = self._key(candidate)
            if key not in self._taken:
                self._taken[key] = candidate
                return AllocatedName(name=candidate, collided_with=original)
            counter += 1
