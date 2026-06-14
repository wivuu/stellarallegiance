"""Bake tileable PBR texture sets, one per *material kind* (not per ship).

Each kind (``hull``, ``cockpit``, ``engine``, ``trim``) yields three seamless, repeat-wrapped
maps shared by every part using that kind, keeping GLBs small:

  * albedo : sRGB-encoded base color (neutral on the hull — team tint is applied at runtime).
  * normal : tangent-space normal map, OpenGL +Y convention.
  * ORM    : R = ambient occlusion, G = roughness, B = metalness (linear).

Seamlessness comes from building height/detail out of integer-frequency sinusoids and taking
periodic (wrap-around) gradients, so a REPEAT sampler shows no seams.
"""

from __future__ import annotations

import zlib

import numpy as np
from PIL import Image

# Per-kind look: base albedo (linear), roughness, metalness, panel-grid cells across the tile
# (0 = no panel lines), and detail/panel bump strengths.
KINDS = {
    "hull":    {"albedo": (0.42, 0.44, 0.47), "rough": 0.58, "metal": 0.15, "cells": 6, "panel": 1.0, "detail": 0.25},
    "cockpit": {"albedo": (0.02, 0.03, 0.06), "rough": 0.12, "metal": 0.00, "cells": 0, "panel": 0.0, "detail": 0.05},
    "engine":  {"albedo": (0.07, 0.07, 0.08), "rough": 0.42, "metal": 0.85, "cells": 0, "panel": 0.0, "detail": 0.45},
    "trim":    {"albedo": (0.55, 0.56, 0.58), "rough": 0.48, "metal": 0.30, "cells": 4, "panel": 0.7, "detail": 0.20},
}
DEFAULT_KIND = "hull"


def bake_kind(kind: str, seed: int, size: int = 512):
    """Return (albedo, normal, orm) PIL RGB images for ``kind``, deterministic in ``seed``."""
    spec = KINDS.get(kind, KINDS[DEFAULT_KIND])
    # zlib.crc32 is a stable hash (Python's str hash is per-process randomized -> non-reproducible).
    rng = np.random.default_rng((seed * 2654435761 + zlib.crc32(kind.encode())) & 0xFFFFFFFF)

    detail = _tile_fbm(size, rng, octaves=4, base_freq=4)            # [-1,1]
    grooves = _panel_grooves(size, spec["cells"]) if spec["cells"] else np.zeros((size, size), np.float32)

    # Height: fine surface detail plus recessed panel grooves.
    height = spec["detail"] * detail - spec["panel"] * grooves
    normal = _normal_from_height(height, strength=2.0)

    # Albedo: base tone, gently mottled, darkened in the grooves; sRGB-encoded.
    mottle = 0.5 + 0.12 * detail
    base = np.asarray(spec["albedo"], np.float32)[None, None, :]
    lin = base * mottle[..., None] * (1.0 - 0.35 * grooves[..., None])
    albedo = _to_img(_linear_to_srgb(lin))

    # ORM: AO from grooves, roughness/metal varied slightly by detail.
    ao = np.clip(1.0 - 0.6 * grooves, 0.0, 1.0)
    rough = np.clip(spec["rough"] + 0.10 * detail, 0.04, 1.0)
    metal = np.full((size, size), spec["metal"], np.float32)
    orm = _to_img(np.stack([ao, rough, metal], axis=-1))

    return albedo, normal, orm


# ---------------------------------------------------------------------------

def _tile_fbm(size, rng, octaves=4, base_freq=4) -> np.ndarray:
    """Seamless fractal noise in [-1,1]: a sum of integer-frequency 2D sinusoids (periodic
    over the unit tile), amplitudes halving per octave."""
    u = (np.arange(size, dtype=np.float32) + 0.5) / size
    X, Y = np.meshgrid(u, u)
    acc = np.zeros((size, size), np.float32)
    amp_total = 0.0
    amp = 1.0
    freq = base_freq
    for _ in range(octaves):
        terms = 3
        for _ in range(terms):
            fx = int(rng.integers(1, freq + 1))
            fy = int(rng.integers(1, freq + 1))
            phase = float(rng.uniform(0, 2 * np.pi))
            acc += amp * np.sin(2 * np.pi * (fx * X + fy * Y) + phase)
        amp_total += amp * terms
        amp *= 0.5
        freq *= 2
    return (acc / max(amp_total, 1e-6)).astype(np.float32)


def _panel_grooves(size, cells) -> np.ndarray:
    """Periodic grid of thin grooves (1 in a groove, 0 on a plate), tiling at ``cells``."""
    u = (np.arange(size, dtype=np.float32) + 0.5) / size
    fx = np.abs((u * cells) % 1.0 - 0.5) * 2.0     # 0 at line center, 1 mid-plate
    line = np.clip(1.0 - fx / 0.06, 0.0, 1.0)       # groove ~6% of a cell wide
    gx = np.tile(line[None, :], (size, 1))
    gy = np.tile(line[:, None], (1, size))
    return np.maximum(gx, gy).astype(np.float32)


def _normal_from_height(h, strength=2.0) -> Image.Image:
    gx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5
    gy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5
    n = np.stack([-gx * strength, gy * strength, np.ones_like(h)], axis=-1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True).clip(1e-9)
    return _to_img(n * 0.5 + 0.5)


def _linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def _to_img(arr) -> Image.Image:
    return Image.fromarray(np.clip(arr * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGB")
