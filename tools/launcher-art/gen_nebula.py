#!/usr/bin/env python3
"""Produce the Game Launcher's static nebula background
(launcher/App/Assets/nebula-clouds.png) by porting the STATIC parts of the
game's animated nebula shader to numpy, evaluated at one fixed time.

Source of truth: client/scripts/ui/NebulaBackground.cs, whose inline
`canvas_item` shader (roughly lines 48-115) draws, in this order:
  1. an opaque Void base colour
  2. four drifting, screen-blended radial "gas cloud" blobs (amber / ember /
     blue / gold; each a soft `pow()` radial falloff in UV space)
  3. a top->bottom Void vignette
  4. a 60px star-dot lattice
  5. 1-in-3-px scanlines

This script ports ONLY 1-3 (the parts that read as a soft, continuous
gradient field) to numpy at a fixed `TIME_T`. The dot lattice and scanlines
are deliberately NOT baked in -- the launcher draws those procedurally in
device pixels at render time so they never go moire when this PNG is scaled
on screen. `Intensity` is the shader's own default (0.60): ServerLobbyOverlay
constructs `new NebulaBackground()` and never sets `.Intensity`, so the game
itself always shows this same default value for this backdrop.

`TIME_T = 0.0` -- the shader's own t=0 rest pose -- is used verbatim as the
"fixed, pleasing" moment: at t=0 the four cloud centres are already well
spread (amber upper-left, ember lower-right, blue near-centre, gold
lower-left; see the shader's aC/bC/cC/dC formulas), so there is no need to
hunt for a more "interesting" time -- any other t only nudges each centre by
a few percent of the canvas (the drift amplitude is 0.04-0.06 UV units).

Output is 1840x1120 (2x a 920x560 window), opaque RGB (no alpha -- this is a
background plate, not a cutout). A tiny fixed-seed uniform dither
(+/-0.5/255 per channel) is added immediately before 8-bit quantisation to
break up banding in the dark gradients; the output is otherwise a pure
function of the constants in this file, so re-running it is byte-identical
(verified via sha256 when this was authored).

Run:
  uv run --with numpy==2.5.3 --with pillow==12.3.0 python3 tools/launcher-art/gen_nebula.py

Or, without uv:
  python3 -m venv .venv && .venv/bin/pip install numpy==2.5.3 pillow==12.3.0
  .venv/bin/python tools/launcher-art/gen_nebula.py
"""

import os

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT_PATH = os.path.join(REPO, "launcher/App/Assets/nebula-clouds.png")

WIDTH, HEIGHT = 1840, 1120  # 2x of the 920x560 window the launcher shows this at

TIME_T = 0.0  # fixed "moment" rendered; see docstring for why t=0 was chosen
INTENSITY = 0.60  # NebulaBackground's own default; ServerLobbyOverlay never overrides it
DITHER_SEED = 1337  # fixed seed -> deterministic, byte-identical re-runs

VOID = np.array([0.0196, 0.0275, 0.0588])  # #05070F, NebulaBackground's `void_color` default


def cloud(uv, center, r):
    """Port of the shader's `cloud()`: soft radial falloff, 1 at `center`
    easing to 0 by radius `r` (UV-space distance, NOT aspect-corrected --
    matches the shader's plain `distance(uv, c)` on a non-square rect)."""
    d = np.linalg.norm(uv - center, axis=-1) / r
    return np.clip(1.0 - d, 0.0, 1.0) ** 1.6


def screen_blend_onto(neb, color, weight):
    """Port of the shader's `screen_blend()`, folded into an accumulator:
    neb = 1 - (1-neb)(1-color*weight), applied per RGB channel."""
    return 1.0 - (1.0 - neb) * (1.0 - color[None, None, :] * weight[..., None])


def render():
    # Pixel-center UV samples, matching a canvas_item fragment shader's UV
    # (0..1 across the rect on both axes, independent of aspect ratio).
    xs = (np.arange(WIDTH) + 0.5) / WIDTH
    ys = (np.arange(HEIGHT) + 0.5) / HEIGHT
    uv_x, uv_y = np.meshgrid(xs, ys)  # each (H, W)
    uv = np.stack([uv_x, uv_y], axis=-1)  # (H, W, 2)

    t = TIME_T
    a_c = np.array([0.30, 0.28]) + np.array([np.sin(t * 0.24) * 0.05, np.cos(t * 0.19) * 0.04])
    c_c = np.array([0.74, 0.74]) + np.array([np.cos(t * 0.15) * 0.06, np.sin(t * 0.17) * 0.05])
    b_c = np.array([0.52, 0.56]) + np.array([np.sin(t * 0.21 + 1.0) * 0.05, np.cos(t * 0.23 + 2.0) * 0.05])
    d_c = np.array([0.24, 0.82]) + np.array([np.cos(t * 0.13 + 3.0) * 0.05, np.sin(t * 0.16 + 1.5) * 0.04])

    a_col = np.array([0.839, 0.486, 0.204])  # amber  214,124,52
    c_col = np.array([0.737, 0.282, 0.118])  # ember  188, 72,30
    b_col = np.array([0.165, 0.376, 0.620])  # blue    42, 96,158
    d_col = np.array([0.910, 0.659, 0.275])  # gold   232,168,70

    ba = cloud(uv, a_c, 0.85) * 0.55
    bc = cloud(uv, c_c, 0.85) * 0.46
    bb = cloud(uv, b_c, 0.70) * 0.50
    bd = cloud(uv, d_c, 0.60) * 0.32

    neb = np.zeros((HEIGHT, WIDTH, 3))
    neb = screen_blend_onto(neb, a_col, ba)
    neb = screen_blend_onto(neb, c_col, bc)
    neb = screen_blend_onto(neb, b_col, bb)
    neb = screen_blend_onto(neb, d_col, bd)

    alpha = 0.35 + np.clip(INTENSITY, 0.0, 1.0) * 0.65
    col = VOID[None, None, :] + neb * alpha

    # top->bottom Void vignette: mix(col, void, mix(0.35, 0.9, uv.y))
    m = (0.35 + 0.55 * uv_y)[..., None]
    col = col * (1.0 - m) + VOID[None, None, :] * m

    col = np.clip(col, 0.0, 1.0)

    rng = np.random.default_rng(DITHER_SEED)
    dither = rng.uniform(-0.5, 0.5, size=col.shape)  # +/- 0.5 of an 8-bit LSB
    quantized = np.clip(col * 255.0 + dither, 0.0, 255.0)
    return np.rint(quantized).astype(np.uint8)


def main():
    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    arr = render()
    img = Image.fromarray(arr, mode="RGB")
    img.save(OUT_PATH, optimize=True)
    print(f"wrote {OUT_PATH}  {img.size[0]}x{img.size[1]}  {os.path.getsize(OUT_PATH)} bytes")


if __name__ == "__main__":
    main()
