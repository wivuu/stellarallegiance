#!/usr/bin/env python3
"""Produce the Stellar Allegiance mouse cursors (client/assets/ui/cursor.png,
cursor_ibeam.png) procedurally.

Both cursors are drawn 8x supersampled and downscaled for clean anti-aliased edges:
  - cursor.png        32x32 angular pointer — Void-dark fill, cyan structural-accent
                      outline (the design system's chrome color). Hotspot = the tip
                      at (2, 2).
  - cursor_ibeam.png  32x32 I-beam for text fields, same palette. Hotspot = (16, 16).

Colors mirror DesignTokens (client/scripts/ui/DesignTokens.cs): Void #05070F,
TeamAccentBase #37E0FF. The cursor uses the BASE accent (never faction-tinted) —
like all chrome it must read the same on every team.

Run:  python3 tools/cursor-gen/gen_cursor.py   (requires Pillow)
"""

import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT_DIR = os.path.join(REPO, "client/assets/ui")

SIZE = 32  # final cursor size in px (Godot default cursor size)
SS = 8  # supersample factor

VOID = (5, 7, 15)  # DesignTokens.Void #05070F
ACCENT = (55, 224, 255)  # DesignTokens.TeamAccentBase #37E0FF
FILL_ALPHA = 235  # near-opaque body so the cursor reads on bright suns too
OUTLINE_W = 0.9  # outline width in final px
SCALE = 0.4  # glyph scale within the 32px canvas (shrunk 60% from the first cut)

# Pointer silhouette in final-px coordinates BEFORE SCALE (applied around the tip, so
# the tip — and the hotspot in UiCursor.cs — stays at (2, 2)). Classic tailed arrow,
# but with hard facets (no curves) so it sits with the chamfer-geometry UI language.
ARROW = [
    (2.0, 2.0),  # tip  (hotspot)
    (2.0, 24.0),  # straight left edge
    (8.4, 18.4),  # barb notch
    (12.2, 27.2),  # tail leg, outer
    (16.6, 25.2),  # tail foot
    (12.8, 16.6),  # tail leg, inner
    (21.0, 16.6),  # right wing
]
TIP = ARROW[0]
ARROW = [(TIP[0] + (x - TIP[0]) * SCALE, TIP[1] + (y - TIP[1]) * SCALE) for x, y in ARROW]


def _canvas():
    im = Image.new("RGBA", (SIZE * SS, SIZE * SS), (0, 0, 0, 0))
    return im, ImageDraw.Draw(im)


def _up(pts):
    return [(x * SS, y * SS) for x, y in pts]


def _save(im, name):
    out = im.resize((SIZE, SIZE), Image.LANCZOS)
    path = os.path.join(OUT_DIR, name)
    out.save(path)
    print(f"wrote {path}")


def gen_pointer():
    im, d = _canvas()
    pts = _up(ARROW)
    d.polygon(pts, fill=VOID + (FILL_ALPHA,))
    # Outline as a closed line loop (polygon outline= draws hairline-only).
    d.line(pts + [pts[0]], fill=ACCENT + (255,), width=int(OUTLINE_W * SS), joint="curve")
    _save(im, "cursor.png")


def gen_ibeam():
    im, d = _canvas()
    # Sized to match the shrunken pointer (SCALE applied around the canvas center,
    # with readability floors on the thin strokes).
    cx = 16.0
    half_h = 11.0 * SCALE
    top, bot = 16.0 - half_h, 16.0 + half_h
    serif = 3.6 * SCALE  # serif half-width
    w = max(0.5, 0.9 * SCALE)  # stem half-width
    serif_t = max(0.9, 1.8 * SCALE)  # serif thickness

    def bar(x0, y0, x1, y1):
        d.rectangle([x0 * SS, y0 * SS, x1 * SS, y1 * SS], fill=ACCENT + (255,))

    bar(cx - w, top, cx + w, bot)  # stem
    bar(cx - serif, top, cx + serif, top + serif_t)  # top serif
    bar(cx - serif, bot - serif_t, cx + serif, bot)  # bottom serif
    _save(im, "cursor_ibeam.png")


def main():
    gen_pointer()
    gen_ibeam()


if __name__ == "__main__":
    main()
