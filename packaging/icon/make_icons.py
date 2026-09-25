"""Draw the app icon from its geometry, and write every format the builds need.

The mark is a page with three list rows and an arrow leaving through a gap in
its right edge. It is drawn from numbers rather than scaled from a bitmap, so
every size is sharp and the corners outside the tile stay transparent.

    python3 packaging/icon/make_icons.py

Writes, next to this file:
    icon.svg            the mark alone, black, for documents
    app-icon.png        1024 px tile, used by the window and the Linux build
    TocExtractor.ico    Windows, 16 to 256 px
    TocExtractor.icns   macOS, via iconutil (skipped when not on macOS)

Needs Pillow.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent

# The mark, in the 4096-unit space it was designed in. Centre lines, with a
# round-capped stroke of STROKE units.
STROKE = 171
PAGE = (1023.5, 895.5, 2815.5, 3200.5)  # left, top, right, bottom
PAGE_RADIUS = 214
GAP = (1748, 2347)  # the right edge is open between these two heights
ROWS = (1450, 2047.5, 2645)
DOT = (1365.5, 1450.5)
LINE = (1730.5, 2474.5)
SHAFT = ((2814.5, 2048), (3296, 2048))
HEAD = ((3088, 1834), (3296, 2048), (3088, 2262))

# Bounds of the inked mark, stroke included, for centring it in a tile.
MARK_BOUNDS = (938, 810, 3382, 3286)

# The tile: a white rounded square on the macOS icon grid, 824 of 1024 units
# with 100 units of transparent margin, so it sits right among other icons.
TILE = 1024
TILE_INSET = 100
TILE_RADIUS = 185
MARK_SCALE = 0.62  # the mark's larger side, as a fraction of the tile


def svg() -> str:
    left, top, right, bottom = PAGE
    r = PAGE_RADIUS
    upper, lower = GAP
    # Start at the lower end of the gap and go round: down, bottom, left, top,
    # and back down the right side to the upper end of the gap.
    page = (
        f"M {right} {lower} L {right} {bottom - r} "
        f"A {r} {r} 0 0 1 {right - r} {bottom} L {left + r} {bottom} "
        f"A {r} {r} 0 0 1 {left} {bottom - r} L {left} {top + r} "
        f"A {r} {r} 0 0 1 {left + r} {top} L {right - r} {top} "
        f"A {r} {r} 0 0 1 {right} {top + r} L {right} {upper}"
    )
    rows = "".join(
        f'<line x1="{DOT[0]}" y1="{y}" x2="{DOT[1]}" y2="{y}"/>'
        f'<line x1="{LINE[0]}" y1="{y}" x2="{LINE[1]}" y2="{y}"/>'
        for y in ROWS
    )
    (x1, y1), (x2, y2) = SHAFT
    head = " ".join(f"{x} {y}" for x, y in HEAD)
    x0, y0, x3, y3 = MARK_BOUNDS
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{x0} {y0} {x3 - x0} {y3 - y0}" '
        f'role="img" aria-label="TOC Extractor">\n'
        f'  <g fill="none" stroke="#000000" stroke-width="{STROKE}" '
        f'stroke-linecap="round" stroke-linejoin="round">\n'
        f'    <path d="{page}"/>\n    {rows}\n'
        f'    <line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}"/>\n'
        f'    <polyline points="{head}"/>\n  </g>\n</svg>\n'
    )


def draw_mark(
    draw: ImageDraw.ImageDraw, scale: float, dx: float, dy: float, ink: tuple[int, int, int, int]
) -> None:
    """Draw the mark with round caps, mapping design units to pixels."""

    def p(x: float, y: float) -> tuple[float, float]:
        return (x * scale + dx, y * scale + dy)

    width = STROKE * scale
    cap = width / 2

    def dot(x: float, y: float) -> None:
        cx, cy = p(x, y)
        draw.ellipse((cx - cap, cy - cap, cx + cap, cy + cap), fill=ink)

    def line(a: tuple[float, float], b: tuple[float, float]) -> None:
        draw.line([p(*a), p(*b)], fill=ink, width=round(width))
        dot(*a)
        dot(*b)

    left, top, right, bottom = PAGE
    r = PAGE_RADIUS
    upper, lower = GAP
    # Straight edges.
    line((left + r, top), (right - r, top))
    line((left + r, bottom), (right - r, bottom))
    line((left, top + r), (left, bottom - r))
    line((right, top + r), (right, upper))
    line((right, lower), (right, bottom - r))
    # Corners: arcs of the centre-line radius, drawn as a thick outline.
    outer, inner = r + STROKE / 2, r - STROKE / 2
    for cx, cy, start in (
        (left + r, top + r, 180),
        (right - r, top + r, 270),
        (right - r, bottom - r, 0),
        (left + r, bottom - r, 90),
    ):
        x0, y0 = p(cx - outer, cy - outer)
        x1, y1 = p(cx + outer, cy + outer)
        draw.arc((x0, y0, x1, y1), start, start + 90, fill=ink, width=round((outer - inner) * scale))
    for y in ROWS:
        line((DOT[0], y), (DOT[1], y))
        line((LINE[0], y), (LINE[1], y))
    line(*SHAFT)
    line(HEAD[0], HEAD[1])
    line(HEAD[1], HEAD[2])


def tile(size: int) -> Image.Image:
    """The icon at `size` px, drawn 4x larger and reduced, for smooth edges."""
    big = size * 4
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    unit = big / TILE
    inset, radius = TILE_INSET * unit, TILE_RADIUS * unit
    draw.rounded_rectangle(
        (inset, inset, big - inset, big - inset),
        radius=radius,
        fill=(255, 255, 255, 255),
        outline=(0, 0, 0, 38),
        width=max(1, round(4 * unit)),
    )
    x0, y0, x1, y1 = MARK_BOUNDS
    scale = (big * MARK_SCALE) / max(x1 - x0, y1 - y0)
    dx = big / 2 - (x0 + x1) / 2 * scale
    dy = big / 2 - (y0 + y1) / 2 * scale
    draw_mark(draw, scale, dx, dy, ink=(0, 0, 0, 255))
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> int:
    (HERE / "icon.svg").write_text(svg(), encoding="utf-8")

    master = tile(1024)
    master.save(HERE / "app-icon.png")

    sizes = [16, 24, 32, 48, 64, 128, 256]
    tile(256).save(HERE / "TocExtractor.ico", sizes=[(s, s) for s in sizes])

    if sys.platform == "darwin" and shutil.which("iconutil"):
        with tempfile.TemporaryDirectory() as scratch:
            iconset = Path(scratch) / "TocExtractor.iconset"
            iconset.mkdir()
            for points in (16, 32, 128, 256, 512):
                tile(points).save(iconset / f"icon_{points}x{points}.png")
                tile(points * 2).save(iconset / f"icon_{points}x{points}@2x.png")
            subprocess.run(
                ["iconutil", "-c", "icns", str(iconset), "-o", str(HERE / "TocExtractor.icns")],
                check=True,
            )
    else:
        print("not on macOS: TocExtractor.icns left as it is")

    print("wrote", ", ".join(sorted(p.name for p in HERE.iterdir() if p.suffix != ".py")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
