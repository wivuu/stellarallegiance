#!/usr/bin/env python3
"""Compose per-model thumbnail PNGs into a labeled contact-sheet grid.

Usage:
  compose.py --thumbs <dir> --out <file.png> [--cols 10] [--cell 320] [--bg 18,20,26]
"""
import argparse
import os
import sys
from PIL import Image, ImageDraw, ImageFont


def load_font(size):
    candidates = [
        "/System/Library/Fonts/SFNSMono.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/Library/Fonts/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
    ]
    for p in candidates:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--thumbs", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cols", type=int, default=10)
    ap.add_argument("--cell", type=int, default=320)
    ap.add_argument("--label", type=int, default=30, help="label strip height px")
    ap.add_argument("--pad", type=int, default=6)
    ap.add_argument("--bg", default="18,20,26")
    args = ap.parse_args()

    bg = tuple(int(x) for x in args.bg.split(","))
    files = sorted(
        f for f in os.listdir(args.thumbs) if f.lower().endswith(".png")
    )
    if not files:
        print("No thumbnails found in " + args.thumbs, file=sys.stderr)
        sys.exit(1)

    cols = args.cols
    rows = (len(files) + cols - 1) // cols
    cell, pad, lab = args.cell, args.pad, args.label
    cw = cell + pad * 2
    ch = cell + lab + pad * 2

    W = cols * cw
    H = rows * ch
    print(f"{len(files)} models -> {cols}x{rows} grid, {W}x{H}px")

    sheet = Image.new("RGB", (W, H), bg)
    draw = ImageDraw.Draw(sheet)
    font = load_font(max(12, int(lab * 0.5)))

    for i, fn in enumerate(files):
        r, c = divmod(i, cols)
        x0 = c * cw
        y0 = r * ch
        # alternating cell backdrop for readability
        cellbg = (28, 31, 40) if (r + c) % 2 == 0 else (23, 26, 34)
        draw.rectangle([x0, y0, x0 + cw - 1, y0 + ch - 1], fill=cellbg)

        try:
            im = Image.open(os.path.join(args.thumbs, fn)).convert("RGBA")
        except Exception as e:
            print(f"  skip {fn}: {e}", file=sys.stderr)
            continue
        im.thumbnail((cell, cell), Image.LANCZOS)
        ix = x0 + pad + (cell - im.width) // 2
        iy = y0 + pad + (cell - im.height) // 2
        sheet.paste(im, (ix, iy), im)

        name = os.path.splitext(fn)[0]
        # shrink-to-fit label
        f = font
        fs = max(12, int(lab * 0.5))
        while fs > 8:
            f = load_font(fs)
            if draw.textlength(name, font=f) <= cell - 4:
                break
            fs -= 1
        tw = draw.textlength(name, font=f)
        tx = x0 + pad + (cell - tw) // 2
        ty = y0 + pad + cell + (lab - fs) // 2 - 2
        draw.text((tx, ty), name, fill=(210, 214, 224), font=f)

    sheet.save(args.out)
    print("wrote " + args.out)


if __name__ == "__main__":
    main()
