# launcher-art — Game Launcher icons, logo, and nebula backdrop

Generates the static art assets for the Avalonia desktop Game Launcher
(`launcher/App/`), which needs to look like the game's dark "retro-futurist"
sci-fi UI (Void `#05070F` background, cyan `#37E0FF` accent) without
depending on Godot or the game's shaders at runtime. Both scripts read
game-side source art/shaders read-only and write committed PNG/ICO output
into `launcher/App/Assets/`. Regenerate here rather than hand-editing an
output file — re-run after the corresponding game-side source changes.

## `gen_icons.py` — app icon + wordmark

```sh
uv run --with resvg-py==0.5.0 --with pillow==12.3.0 --with numpy==2.5.3 python3 tools/launcher-art/gen_icons.py
```

Inputs: `client/assets/ui/app_icon.svg` (the 1024-intrinsic, full-bleed
square app icon; `app_icon_macos.svg` is the separate macOS-only "raised"
variant and is not used here), `client/assets/ui/logo.png` (the transparent
wordmark/emblem, already used as the engine boot splash).

Outputs:
- `launcher/App/Assets/app.ico` — multi-resolution Windows icon, 16/20/24/32/40/48/64/128/256 px
- `launcher/App/Assets/icon-256.png` — 256x256 RGBA, the window icon and Linux AppImage icon
- `launcher/App/Assets/logo-680.png` — `logo.png` downscaled (LANCZOS, alpha kept, aspect kept) to 680px wide

SVG rasterisation uses **resvg-py** (Rust `resvg` via a prebuilt wheel — no
system libraries needed; verified working on this machine). `cairosvg` is
tried as a fallback only if resvg-py is unavailable, but it needs a system
libcairo this Mac doesn't have; if neither works the script explains why and
exits non-zero rather than fabricating an icon. The SVG is rendered once at
its own intrinsic size (1024x1024) and every smaller size is LANCZOS-downsampled
from that single master, so the nine sizes are all consistent with each other.

Both `app_icon.svg`'s render and `logo.png` are straight-alpha RGBA whose
fully-transparent pixels carry RGB=(0,0,0) — Pillow's plain `Image.resize`
would blend that black into partially-transparent edge pixels regardless of
alpha (a dark fringe/halo), so every downsample goes through a
premultiply-resize-unpremultiply helper (`_resize_straight_alpha`) instead.

## `gen_nebula.py` — static nebula backdrop

```sh
uv run --with numpy==2.5.3 --with pillow==12.3.0 python3 tools/launcher-art/gen_nebula.py
```

Input: `client/scripts/ui/NebulaBackground.cs`, whose inline `canvas_item`
shader (~lines 48-115) draws the animated nebula behind the server-browser
menu (`ServerLobbyOverlay.cs`). This script ports the shader's STATIC maths
— the Void base, the four screen-blended drifting gas-cloud blobs (colours,
falloff exponent and blend mode kept exact), and the top→bottom vignette —
to numpy, evaluated at one fixed instant (`TIME_T = 0.0`, the shader's own
t=0 pose, already a balanced composition — see the script's docstring for
why no other `t` was needed) and at the shader's default `Intensity` (0.60;
`ServerLobbyOverlay` constructs `NebulaBackground` without overriding it).

Output: `launcher/App/Assets/nebula-clouds.png`, 1840x1120 (2x of the 920x560
window it's shown in), opaque RGB. The star-dot lattice and scanlines are
**deliberately not baked in** — the launcher draws those procedurally in
device pixels at render time so they never alias/moiré when this PNG is
scaled. A fixed-seed (`DITHER_SEED = 1337`) uniform dither of ±0.5/255 per
channel is added immediately before 8-bit quantisation to break up banding
in the dark gradients; because every input is a constant, the output is
byte-identical across runs (verified via sha256 when this was authored).

Both scripts' outputs are committed generated assets, not hand-authored —
touch the Python, not the PNG/ICO.
