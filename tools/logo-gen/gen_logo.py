#!/usr/bin/env python3
"""Produce the Stellar Allegiance boot logo (client/assets/ui/logo.png) from the
brand artwork (source_logo.png).

The source art is composed on a near-black background. We key that background out to
a true-transparent alpha by un-premultiplying it (subtract the flat bg, derive alpha
from the residual luminance, then divide it back out) — this gives clean soft edges
with NO dark fringe on any background, unlike a hard luminance cut. The emblem's own
dark interior (space inside the ring) keys out too, which is correct: on the dark boot
screen the transparent areas show the matching bg_color, reproducing the artwork.

Run:  python3 tools/logo-gen/gen_logo.py   (requires Pillow)
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
SRC = os.path.join(HERE, "source_logo.png")
OUT = os.path.join(REPO, "client/assets/ui/logo.png")

TARGET_H = 1000        # output height in px (boot splash shows at native size)
FLOOR = 12.0           # residual luminance at/below this => fully transparent (kills bg grain)
KNEE = 46.0            # residual luminance at/above this => fully opaque
PAD = 24               # transparent padding around the trimmed content (px, pre-resize)


def main():
    im = Image.open(SRC).convert("RGBA")
    w, h = im.size
    px = im.load()

    # Flat background colour: average the four corners.
    corners = [px[0, 0], px[w - 1, 0], px[0, h - 1], px[w - 1, h - 1]]
    br = sum(c[0] for c in corners) / 4.0
    bg = sum(c[1] for c in corners) / 4.0
    bb = sum(c[2] for c in corners) / 4.0

    out = Image.new("RGBA", (w, h))
    op = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b, _ = px[x, y]
            dr = max(0.0, r - br)
            dg = max(0.0, g - bg)
            db = max(0.0, b - bb)
            lum = 0.299 * dr + 0.587 * dg + 0.114 * db
            a = (lum - FLOOR) / (KNEE - FLOOR)
            if a <= 0.0:
                op[x, y] = (0, 0, 0, 0)
                continue
            if a > 1.0:
                a = 1.0
            # un-premultiply: straight fg = residual/a + bg
            sr = int(min(255, dr / a + br))
            sg = int(min(255, dg / a + bg))
            sb = int(min(255, db / a + bb))
            op[x, y] = (sr, sg, sb, int(a * 255))

    # Trim to the content bounding box (+ padding), then scale to target height.
    bbox = out.getbbox()
    if bbox:
        l, t, r, btm = bbox
        l = max(0, l - PAD); t = max(0, t - PAD)
        r = min(w, r + PAD); btm = min(h, btm + PAD)
        out = out.crop((l, t, r, btm))
    scale = TARGET_H / out.height
    out = out.resize((round(out.width * scale), TARGET_H), Image.LANCZOS)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    out.save(OUT)
    print(f"wrote {OUT}  {out.width}x{out.height}  (bg keyed from {int(br)},{int(bg)},{int(bb)})")


if __name__ == "__main__":
    main()
