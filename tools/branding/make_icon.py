"""Draws the OpenSCCT Editor icon into app/branding/.

    python tools/branding/make_icon.py

The mark is OpenSCCT's ring (the O of Open, with its horizontal scan gap) in the editor's accent blue, so the two
read as one family. The game's four aiming ticks become the corners of a selection box and its centre dot a square
handle: the editor's own marks. Outputs: icon.svg (project icon: the window and taskbar) and icon.ico (16 to 256
px, for an exported exe or a shortcut).
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parents[2] / "app" / "branding"

TILE = (19, 21, 27)          # a touch above EditorTheme.Background, so the tile shows on dark taskbars
BLUE = (84, 148, 245)        # EditorTheme.Accent
DIM = (52, 92, 156)          # the selection corners, quieter than the ring

# Proportions of the tile side (the same numbers drive the bitmap and the SVG).
R_OUT, R_IN, GAP = 0.34, 0.25, 0.035
HANDLE = 0.05                # half side of the centre square
BOX, ARM, ARM_W = 0.37, 0.09, 0.026   # corner distance from the centre, arm length, arm half width
RADIUS = 0.2


def corners(s: float) -> list[tuple[float, float, float, float]]:
    """The selection corners as rectangles (x0, y0, x1, y1): two arms per corner."""
    c, b, a, w = s / 2, s * BOX, s * ARM, s * ARM_W
    rects = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = c + sx * b, c + sy * b
            # Horizontal arm along the box edge, then the vertical one, both running inward from the corner's outer
            # edge so they meet in a solid L.
            x0, x1 = x + sx * w, x - sx * a
            y0, y1 = y + sy * w, y - sy * a
            rects.append((min(x0, x1), y - w, max(x0, x1), y + w))
            rects.append((x - w, min(y0, y1), x + w, max(y0, y1)))
    return rects


def draw(size: int) -> Image.Image:
    """The icon on its tile, drawn at 8x and scaled down for smooth edges."""
    s = size * 8
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * RADIUS), fill=TILE + (255,))
    c = s / 2
    r_out, r_in, gap = s * R_OUT, s * R_IN, s * GAP
    d.ellipse([c - r_out, c - r_out, c + r_out, c + r_out], fill=BLUE + (255,))
    d.ellipse([c - r_in, c - r_in, c + r_in, c + r_in], fill=TILE + (255,))
    d.rectangle([c - r_out - 1, c - gap, c + r_out + 1, c + gap], fill=TILE + (255,))
    h = s * HANDLE
    d.rectangle([c - h, c - h, c + h, c + h], fill=BLUE + (255,))
    for r in corners(s):
        d.rectangle(list(r), fill=DIM + (255,))
    return im.resize((size, size), Image.Resampling.LANCZOS)


def svg() -> str:
    """The same icon as SVG on a 256 tile."""
    s, c = 256, 128
    r_out, r_in, gap, h = s * R_OUT, s * R_IN, s * GAP, s * HANDLE
    arms = "".join(f'<rect x="{x0:.2f}" y="{y0:.2f}" width="{x1 - x0:.2f}" height="{y1 - y0:.2f}"/>' for x0, y0, x1, y1 in corners(s))
    rgb = lambda t: f"rgb({t[0]}, {t[1]}, {t[2]})"
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <mask id="cut">
      <rect width="256" height="256" fill="white"/>
      <circle cx="{c}" cy="{c}" r="{r_in:.2f}" fill="black"/>
      <rect x="0" y="{c - gap:.2f}" width="256" height="{2 * gap:.2f}" fill="black"/>
    </mask>
  </defs>
  <rect width="256" height="256" rx="{s * RADIUS:.2f}" fill="{rgb(TILE)}"/>
  <circle cx="{c}" cy="{c}" r="{r_out:.2f}" fill="{rgb(BLUE)}" mask="url(#cut)"/>
  <rect x="{c - h:.2f}" y="{c - h:.2f}" width="{2 * h:.2f}" height="{2 * h:.2f}" fill="{rgb(BLUE)}"/>
  <g fill="{rgb(DIM)}">{arms}</g>
</svg>
"""


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "icon.svg").write_text(svg(), encoding="utf-8", newline="\n")
    draw(256).save(OUT / "icon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"wrote icon.svg and icon.ico to {OUT}")


if __name__ == "__main__":
    main()
