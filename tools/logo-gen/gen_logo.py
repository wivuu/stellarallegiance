#!/usr/bin/env python3
"""Generate the Stellar Allegiance brand logo lockup as a single, self-contained
transparent SVG (client/assets/ui/logo.svg).

The emblem is lifted from the Claude Design splash reference (Splash.dc.html,
project 28bf0d21-…) with its CSS animations stripped. The STELLAR / ALLEGIANCE
wordmark is set in Michroma and baked to outline <path>s here — Godot's SVG
importer (ThorVG) has no font engine, so the wordmark must be pre-outlined to
render. The result is used as the engine boot splash (rasterized to PNG).

Run:  python3 tools/logo-gen/gen_logo.py
(requires `fonttools`; see tools/logo-gen/README.md)
"""
import os
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.boundsPen import BoundsPen

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
FONT = os.path.join(REPO, "client/assets/fonts/michroma.ttf")
OUT = os.path.join(REPO, "client/assets/ui/logo.svg")

# Everything is authored in the emblem's native 1000-wide coordinate space so the
# emblem markup drops in unscaled; the wordmark is laid out below it.
CX = 500.0

# Wordmark sizing (units in the 1000-wide emblem space). Michroma is very wide, so
# STELLAR is sized to fit the canvas with margins rather than scaled 1:1 from the
# reference px values; ALLEGIANCE keeps the reference proportion.
STELLAR_SIZE, STELLAR_TRACK = 116.0, 16.0
ALLEG_SIZE, ALLEG_TRACK = 45.0, 29.0

CREAM = "#ece6d8"   # STELLAR
STEEL = "#5b7d99"   # ALLEGIANCE + flanking dashes

_font = TTFont(FONT)
_upm = _font["head"].unitsPerEm
_cmap = _font.getBestCmap()
_glyphs = _font.getGlyphSet()


def _cap_height(size_px):
    """Ink height of a capital (measured off 'H'/'S'), in px at the given size."""
    pen = BoundsPen(_glyphs)
    _glyphs[_cmap[ord("S")]].draw(pen)
    _, ymin, _, ymax = pen.bounds
    return (ymax - ymin) * size_px / _upm


def render_word(text, size_px, track_px, baseline_y, fill):
    """A <g> of outlined glyphs for `text`, horizontally centred on CX with its
    baseline at baseline_y. Returns (svg, total_width_px)."""
    s = size_px / _upm
    advances = [_glyphs[_cmap[ord(c)]].width * s for c in text]
    total = sum(advances) + track_px * (len(text) - 1)
    x = CX - total / 2.0
    parts = []
    for c, adv in zip(text, advances):
        gname = _cmap[ord(c)]
        pen = SVGPathPen(_glyphs)
        _glyphs[gname].draw(pen)
        d = pen.getCommands()
        if d:  # skip the space glyph (no outline)
            # font units are y-up with baseline at 0; flip to SVG y-down.
            parts.append(
                f'<path transform="translate({x:.2f} {baseline_y:.2f}) '
                f'scale({s:.5f} {-s:.5f})" d="{d}"/>'
            )
        x += adv + track_px
    return f'<g fill="{fill}">{"".join(parts)}</g>', total


# ---- emblem (reference markup, animations removed) -------------------------------
EMBLEM = """
  <defs>
    <linearGradient id="sp-cream" x1="500" y1="100" x2="500" y2="800" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#f4efe3"/><stop offset="1" stop-color="#d8d1c0"/>
    </linearGradient>
    <linearGradient id="sp-planet" x1="500" y1="720" x2="500" y2="980" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#2a405a"/><stop offset="0.18" stop-color="#16243a"/><stop offset="1" stop-color="#0a1018"/>
    </linearGradient>
    <linearGradient id="sp-ring" x1="180" y1="120" x2="820" y2="820" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#6d92b0"/><stop offset="0.5" stop-color="#3f6480"/><stop offset="1" stop-color="#26425a"/>
    </linearGradient>
    <radialGradient id="sp-spark" cx="0.5" cy="0.5" r="0.5">
      <stop offset="0" stop-color="#ffffff"/><stop offset="0.4" stop-color="#bfe2ff"/><stop offset="1" stop-color="#bfe2ff" stop-opacity="0"/>
    </radialGradient>
    <clipPath id="sp-disc"><circle cx="500" cy="460" r="372"/></clipPath>
  </defs>

  <g clip-path="url(#sp-disc)">
    <ellipse cx="500" cy="1120" rx="430" ry="320" fill="url(#sp-planet)"/>
    <path d="M120 808 Q500 700 880 808" stroke="#5b7d99" stroke-width="3" stroke-opacity="0.55" fill="none"/>
  </g>

  <circle cx="500" cy="460" r="372" fill="none" stroke="url(#sp-ring)" stroke-width="9" stroke-linecap="round"
          stroke-dasharray="470 70 720 70 420 60"/>

  <g fill="#cfe0f0">
    <circle cx="320" cy="300" r="2.4" opacity="0.85"/><circle cx="690" cy="360" r="2" opacity="0.7"/>
    <circle cx="760" cy="540" r="2.6" opacity="0.8"/><circle cx="270" cy="560" r="1.8" opacity="0.6"/>
    <circle cx="610" cy="250" r="1.6" opacity="0.55"/><circle cx="430" cy="520" r="1.6" opacity="0.5"/>
    <circle cx="730" cy="640" r="2" opacity="0.7"/><circle cx="300" cy="440" r="1.5" opacity="0.5"/>
  </g>
  <g fill="#eaf3ff">
    <path d="M360 380 L364 396 L380 400 L364 404 L360 420 L356 404 L340 400 L356 396 Z" opacity="0.9"/>
    <path d="M812 352 L815 364 L827 367 L815 370 L812 382 L809 370 L797 367 L809 364 Z" opacity="0.85"/>
  </g>

  <circle cx="500" cy="430" r="34" fill="url(#sp-spark)"/>
  <path d="M500 396 L505 425 L500 430 L495 425 Z M500 464 L495 435 L500 430 L505 435 Z M468 430 L497 425 L500 430 L497 435 Z M532 430 L503 435 L500 430 L503 425 Z" fill="#ffffff"/>

  <path d="M500 95 L628 500 L888 558 L650 600 L672 786 L582 786 L500 255 L418 786 L328 786 L350 600 L112 558 L372 500 Z" fill="url(#sp-cream)"/>

  <g stroke-linecap="round">
    <line x1="488" y1="720" x2="478" y2="812" stroke="#cdd9e6" stroke-width="4" opacity="0.45"/>
    <line x1="500" y1="724" x2="500" y2="828" stroke="#e6eef7" stroke-width="5" opacity="0.6"/>
    <line x1="512" y1="720" x2="522" y2="812" stroke="#cdd9e6" stroke-width="4" opacity="0.45"/>
  </g>
  <path d="M500 572 L508 612 L520 624 L548 648 L520 650 L516 686 L526 706 L504 696 L500 716 L496 696 L474 706 L484 686 L480 650 L452 648 L480 624 L492 612 Z" fill="#f1ebdf"/>
"""

# ---- wordmark layout -------------------------------------------------------------
GAP_EMBLEM = 34.0      # emblem base (~870) → STELLAR cap top
GAP_LINES = 30.0       # STELLAR baseline → ALLEGIANCE cap top
cap1 = _cap_height(STELLAR_SIZE)
cap2 = _cap_height(ALLEG_SIZE)

stellar_baseline = 870.0 + GAP_EMBLEM + cap1
alleg_cap_top = stellar_baseline + GAP_LINES
alleg_baseline = alleg_cap_top + cap2

stellar_svg, _ = render_word("STELLAR", STELLAR_SIZE, STELLAR_TRACK, stellar_baseline, CREAM)
alleg_svg, alleg_w = render_word("ALLEGIANCE", ALLEG_SIZE, ALLEG_TRACK, alleg_baseline, STEEL)

# Flanking dashes, centred on the ALLEGIANCE caps (46×2px in the reference → ×2.63).
dash_w, dash_h, dash_gap = 121.0, 5.0, 42.0
dash_y = alleg_cap_top + cap2 / 2.0 - dash_h / 2.0
dash_left_x = CX - alleg_w / 2.0 - dash_gap - dash_w
dash_right_x = CX + alleg_w / 2.0 + dash_gap
dashes = (
    f'<g fill="{STEEL}">'
    f'<rect x="{dash_left_x:.2f}" y="{dash_y:.2f}" width="{dash_w}" height="{dash_h}"/>'
    f'<rect x="{dash_right_x:.2f}" y="{dash_y:.2f}" width="{dash_w}" height="{dash_h}"/>'
    f"</g>"
)

vb_h = alleg_baseline + 18.0
svg = (
    f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 {vb_h:.0f}" '
    f'width="1000" height="{vb_h:.0f}" fill="none">'
    f"{EMBLEM}{stellar_svg}{dashes}{alleg_svg}</svg>\n"
)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w") as f:
    f.write(svg)
print(f"wrote {OUT}  (viewBox 0 0 1000 {vb_h:.0f})")
