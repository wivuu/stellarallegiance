#!/usr/bin/env python3
"""Produce the Game Launcher's window/taskbar icons from the game's app-icon
artwork, and a launcher-sized copy of the game's wordmark logo.

Sources (read-only, never modified):
  - client/assets/ui/app_icon.svg        the Stellar Allegiance app icon: a
    full-bleed 32x32-viewBox square (intrinsic width/height=1024) -- the void
    plate + orbit ring + chevron. (app_icon_macos.svg is the separate,
    macOS-only "raised" variant and is intentionally NOT used here.)
  - client/assets/ui/logo.png            the transparent wordmark/emblem art
    used as the engine boot splash (see tools/logo-gen/).

Outputs (launcher/App/Assets/):
  - app.ico          multi-resolution Windows icon: 16/20/24/32/40/48/64/128/256 px
  - icon-256.png      256x256 RGBA, used as the window icon and the Linux AppImage icon
  - logo-680.png      logo.png downscaled to 680px wide (keeps alpha + aspect ratio;
                      the launcher shows it at ~340 DIP, so 680px covers 2x displays)

SVG rasterisation: this script needs a pure-Python/uv-installable SVG
rasteriser with no system libraries (cairosvg needs a system libcairo, which
this Mac does not have by default). In order of preference:
  1. resvg-py  -- Rust `resvg` via a prebuilt wheel, no system deps. WORKS on
     this machine (verified) and is what this script uses.
  2. cairosvg  -- falls back to this only if resvg-py is unavailable; may
     fail here without `brew install cairo`.
If neither works, the script prints what's missing and exits non-zero rather
than fabricating icon output.

The 1024x1024 master render (resvg-py rendered directly at the SVG's own
intrinsic size) is LANCZOS-downsampled to all nine .ico/PNG sizes, so every
size traces back to one crisp source render instead of the rasteriser being
asked to draw the vector at 16px directly (thin strokes like the 1.7px orbit
ring can hint inconsistently at tiny direct-render sizes).

Both the icon master and logo.png are straight-alpha RGBA whose fully
transparent pixels happen to carry RGB=(0,0,0) (confirmed by inspection).
Pillow's plain `Image.resize` filters each channel independently, so a naive
downsample blends that transparent black RGB into partially-transparent edge
pixels regardless of alpha -- a dark fringe/halo, most visible on the logo's
bright cream/blue art. `_resize_straight_alpha` avoids this the standard way:
premultiply RGB by alpha, resize premultiplied RGB and alpha separately, then
un-premultiply -- so a transparent neighbour can never pull an edge pixel's
*color* dark, only its alpha down.

Run:
  uv run --with resvg-py==0.5.0 --with pillow==12.3.0 --with numpy==2.5.3 python3 tools/launcher-art/gen_icons.py

Or, without uv:
  python3 -m venv .venv && .venv/bin/pip install resvg-py==0.5.0 pillow==12.3.0 numpy==2.5.3
  .venv/bin/python tools/launcher-art/gen_icons.py
"""

import io
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

SVG_SRC = os.path.join(REPO, "client/assets/ui/app_icon.svg")
LOGO_SRC = os.path.join(REPO, "client/assets/ui/logo.png")
OUT_DIR = os.path.join(REPO, "launcher/App/Assets")

ICO_OUT = os.path.join(OUT_DIR, "app.ico")
PNG256_OUT = os.path.join(OUT_DIR, "icon-256.png")
LOGO_OUT = os.path.join(OUT_DIR, "logo-680.png")

MASTER_SIZE = 1024  # == the SVG's own intrinsic width/height; no upscaling needed
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
LOGO_WIDTH = 680


def rasterise_svg(svg_path, size):
    """Render `svg_path` to an RGBA PIL Image at size x size px. Tries
    resvg-py first, then cairosvg. Raises RuntimeError with an explanation
    if neither rasteriser is usable -- callers must not fake an icon."""
    try:
        import resvg_py

        png_bytes = resvg_py.svg_to_bytes(svg_path=svg_path, width=size, height=size)
        return Image.open(io.BytesIO(bytes(png_bytes))).convert("RGBA")
    except ImportError:
        pass
    except Exception as exc:  # pragma: no cover - environment-dependent
        print(f"resvg-py failed to rasterise {svg_path}: {exc}", file=sys.stderr)

    try:
        import cairosvg  # needs a system libcairo; may not be installed here

        png_bytes = cairosvg.svg2png(url=svg_path, output_width=size, output_height=size)
        return Image.open(io.BytesIO(png_bytes)).convert("RGBA")
    except ImportError:
        pass
    except OSError as exc:  # pragma: no cover - environment-dependent
        print(f"cairosvg is installed but its system libcairo is missing: {exc}", file=sys.stderr)

    raise RuntimeError(
        "No working SVG rasteriser found (tried resvg-py, then cairosvg). "
        "Install one of them (see this script's docstring) -- refusing to "
        "fabricate icon output."
    )


def _resize_straight_alpha(im, size, resample=Image.LANCZOS):
    """LANCZOS-resize a straight-alpha RGBA image without dark-fringing its
    edges. Plain `Image.resize` filters R/G/B/A independently, so wherever a
    fully-transparent neighbour's RGB differs from the edge color (here: 0,0,0
    black), the filtered edge pixel's *color* gets pulled toward that
    neighbour even though its alpha correctly fades out -- a dark halo. Fix:
    premultiply, resize, un-premultiply (the standard technique)."""
    r, g, b, a = im.split()
    rgb = np.asarray(Image.merge("RGB", (r, g, b)), dtype=np.float32)
    alpha = np.asarray(a, dtype=np.float32) / 255.0

    premult = np.clip(rgb * alpha[..., None], 0, 255).astype(np.uint8)
    premult_resized = Image.fromarray(premult, "RGB").resize(size, resample)
    alpha_resized = a.resize(size, resample)

    premult_r = np.asarray(premult_resized, dtype=np.float32)
    alpha_r = np.asarray(alpha_resized, dtype=np.float32) / 255.0
    safe_alpha = np.where(alpha_r > (0.5 / 255.0), alpha_r, 1.0)
    straight = np.clip(premult_r / safe_alpha[..., None], 0, 255)
    straight = np.where(alpha_r[..., None] > (0.5 / 255.0), straight, 0.0)

    out = Image.fromarray(straight.astype(np.uint8), "RGB").convert("RGBA")
    out.putalpha(alpha_resized)
    return out


def make_icons():
    master = rasterise_svg(SVG_SRC, MASTER_SIZE)
    print(f"rendered {SVG_SRC} -> {master.size[0]}x{master.size[1]} master (resvg-py)")

    sized = {s: _resize_straight_alpha(master, (s, s)) for s in ICO_SIZES}

    sized[256].save(PNG256_OUT)
    print(f"wrote {PNG256_OUT}  256x256  {os.path.getsize(PNG256_OUT)} bytes")

    base = sized[256]
    extras = [sized[s] for s in ICO_SIZES if s != 256]
    base.save(
        ICO_OUT,
        format="ICO",
        sizes=[(s, s) for s in ICO_SIZES],
        append_images=extras,
    )
    print(f"wrote {ICO_OUT}  {os.path.getsize(ICO_OUT)} bytes")

    # Verify by re-opening fresh, as required.
    reopened = Image.open(ICO_OUT)
    contained = sorted(reopened.info.get("sizes", []))
    print(f"verify: app.ico reports sizes = {contained}")
    expected = sorted((s, s) for s in ICO_SIZES)
    if contained != expected:
        print(f"FAIL: expected {expected}", file=sys.stderr)
        sys.exit(1)


def make_logo():
    src = Image.open(LOGO_SRC).convert("RGBA")
    sw, sh = src.size
    dh = round(sh * (LOGO_WIDTH / sw))
    out = _resize_straight_alpha(src, (LOGO_WIDTH, dh))
    out.save(LOGO_OUT)
    print(f"source {LOGO_SRC}  {sw}x{sh}  {os.path.getsize(LOGO_SRC)} bytes")
    print(f"wrote  {LOGO_OUT}  {out.size[0]}x{out.size[1]}  {os.path.getsize(LOGO_OUT)} bytes")


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    make_icons()
    make_logo()
    print("\nOK")


if __name__ == "__main__":
    main()
