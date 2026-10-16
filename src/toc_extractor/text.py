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


# Notices a reading site writes into its chapters ("This tale has been
# pilfered from NovelFire. If found on Amazon, kindly file a report."), in
# endless variations, so recognised by what they are made of rather than
# matched against a list. Kept identical to TextCleaner.cs; see
# strip_site_notices.
_SITE_NAME = (
    r"(?-i:(?:[A-Z][a-z]+)*Novel(?:[A-Z][a-z]+)+|(?:[A-Z][a-z]+)+Novel\b"
    r"|\b[a-z]*novel[a-z]*\.(?:com|net|org|co|me|io)\b)"
)
_NOTICE_WHERE = re.compile(
    r"\bamazon\b|\boriginal (?:site|version|source|platform|website|translation)\b"
    r"|\bauthentic version\b|\bgenuine (?:story|version)\b|\breal experience\b|" + _SITE_NAME,
    re.IGNORECASE,
)
# A vaguer "where", which counts only beside a call to read or report.
_NOTICE_ELSEWHERE = re.compile(
    r"\belsewhere\b|\banother (?:site|website)\b|\bother (?:sites|websites)\b", re.IGNORECASE
)
_NOTICE_THEFT = re.compile(
    r"\b(?:stolen|steal\w*|pilfer\w*|purloin\w*|unlawful\w*|illicit\w*|misappropriat\w*"
    r"|lifted|unauthori[sz]ed|theft|infringement|violation)\b|\btaken without\b"
    r"|\bwithout (?:the author['\u2019]?s? )?(?:consent|permission|approval|authori[sz]ation)\b",
    re.IGNORECASE,
)
_NOTICE_ACTION = re.compile(
    r"\b(?:report\w*|visit\w*|support\w*|check it out|head to|help (?:the author|them|out)"
    r"|ensure the author|read(?:ing)? (?:it|this|the|their|on|from))\b",
    re.IGNORECASE,
)
_NOTICE_SUBJECT = re.compile(
    r"\b(?:story|stories|tale|narrative|novel|content|book|chapter|text|work|translation)\b",
    re.IGNORECASE,
)
# A site's tag after the last sentence of a paragraph: " NovelFire",
# " View the correct content at NovelFire".
_SITE_TAIL = re.compile(
    '(?<=[.!?~\u2026"\'\u201d\u2019\\])])[ \\t]+((?:[^.!?\\n"\u201c\u201d]{0,60}?[ \\t])?)'
    + _SITE_NAME
    + r"\.?[ \t]*$",
    re.MULTILINE,
)
_SITE_TAIL_WORDS = re.compile(
    r"\b(?:content|read|visit|view|find|chapter|version|latest|available|exclusive|update|on|at)\b",
    re.IGNORECASE,
)
# The site's name put in place of the word "novel": "who loves reading NovelFires".
_SITE_FOR_NOVEL = re.compile(
    r"\b([A-Za-z][a-z'\u2019]*)([ \t]+)((?:[A-Z][a-z]+)*Novel(?:[A-Z][a-z]+?)+)(s?)\b"
)
# Words before a site's name that mean the site itself ("on NovelFire").
_SITE_ITSELF = frozenset(
    {"on", "at", "from", "to", "via", "by", "visit", "visiting", "and", "or", "with", "for"}
)
# A sentence's first word that makes the name after it a thing, not the site.
_DETERMINERS = frozenset(
    {
        "The",
        "A",
        "An",
        "This",
        "That",
        "These",
        "Those",
        "My",
        "His",
        "Her",
        "Their",
        "Your",
        "Our",
        "Its",
        "Every",
        "Each",
        "Some",
    }
)
_SENTENCE_BREAK = re.compile('[.!?\u2026~]["\'\u201d\u2019)\\]]*[ \\t]+(?=[A-Z\u201c"])')


def is_site_notice(text: str) -> bool:
    """Whether one sentence or paragraph is a reading site's notice."""
    if (
        not 15 <= len(text) <= 260
        or any(q in text for q in '"\u201c\u201d')
        or text[0] in "[\u3010'"
        or len(text.split()) > 45
    ):
        return False
    theft = bool(_NOTICE_THEFT.search(text))
    action = bool(_NOTICE_ACTION.search(text))
    return (
        (bool(_NOTICE_WHERE.search(text)) and (theft or action))
        or (action and bool(_NOTICE_ELSEWHERE.search(text)) and bool(_NOTICE_SUBJECT.search(text)))
        or (theft and action and len(text) <= 160 and bool(_NOTICE_SUBJECT.search(text)))
    )


def _notice_signal(text: str) -> bool:
    """Whether a sentence carries any part of a notice: a site, elsewhere, taking, or a call to act.

    A short lead-in about the story itself ("Love this story?") belongs to the
    notice after it.
    """
    return bool(
        _NOTICE_WHERE.search(text)
        or _NOTICE_ELSEWHERE.search(text)
        or _NOTICE_THEFT.search(text)
        or _NOTICE_ACTION.search(text)
        or (len(text.strip()) <= 40 and _NOTICE_SUBJECT.search(text))
    )


def _cut_trailing_notice(line: str) -> str:
    breaks = list(_SENTENCE_BREAK.finditer(line))
    for found in breaks[max(0, len(breaks) - 2) :]:
        head = line[: found.end()].rstrip()
        if head and is_site_notice(line[found.end() :].strip()):
            return head
    return line


def _drop_site_tail(match: re.Match[str]) -> str:
    lead = match.group(1)
    return "" if not lead or _SITE_TAIL_WORDS.search(lead) else match.group(0)


def _novel_for_site(match: re.Match[str]) -> str:
    word = match.group(1)
    common = word.lower() not in _SITE_ITSELF if word[0].islower() else word in _DETERMINERS
    return word + match.group(2) + "novel" + match.group(4) if common else match.group(0)


def strip_site_notices(text: str) -> str:
    """Remove the notices a reading site writes into its chapters.

    "This tale has been pilfered from NovelFire. If found on Amazon, kindly
    file a report.", "Visit NovelFire for the authentic version.", a bare
    "NovelFire" after a paragraph's last sentence, and the site's name put in
    place of the word "novel". A notice is a short sentence with no dialogue
    that names where to read or who took the text together with taking or a
    call to read or report it; or says the story was taken and asks for a
    report. It goes whether it is a paragraph of its own or was added to the
    end of one. Mirrors TextCleaner.StripSiteNotices.
    """
    if not (
        "novel" in text.lower()
        or _NOTICE_WHERE.search(text)
        or _NOTICE_ELSEWHERE.search(text)
        or _NOTICE_THEFT.search(text)
    ):
        return text
    kept: list[str] = []
    for raw in text.split("\n"):
        carriage = raw.endswith("\r")
        line = raw[:-1] if carriage else raw
        # Notice sentences after the story go first, so a story sentence
        # sharing their paragraph is kept; then a paragraph that is all
        # notice goes whole.
        whole = line
        for _ in range(3):
            cut = _cut_trailing_notice(line)
            if cut == line:
                break
            line = cut
        if is_site_notice(line.strip()) or (
            line != whole and is_site_notice(whole.strip()) and _notice_signal(line)
        ):
            continue
        line = _SITE_TAIL.sub(_drop_site_tail, line)
        line = _SITE_FOR_NOVEL.sub(_novel_for_site, line)
        kept.append(line + "\r" if carriage else line)
    return "\n".join(kept)


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
        text = strip_site_notices(text)
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
