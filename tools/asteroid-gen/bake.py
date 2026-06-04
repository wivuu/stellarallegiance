"""Bake equirectangular texture maps from the analytic shape field.

Two passes, both processing rows in chunks (bounded memory + latitude-band bump culling):

  * bake_normal(...)  -> RGB tangent-space normal map (high res). For each texel we build
        the spherical tangent basis (the basis the GLB mesh is authored against) and store
        the analytic surface normal in it:  (n·T0, n·B0, n·N0) -> RGB. OpenGL/+Y convention.

  * bake_surface(...) -> (albedo RGB, ORM RGB, height I;16) (mid res). Shares the field eval:
        - albedo : per-type tones blended by low-freq mottling, darkened in cavities (sRGB-encoded).
        - ORM    : R = ambient occlusion, G = roughness, B = metalness (linear data).
        - height : detail displacement, 16-bit (sidecar map for parallax/displacement later).

Image origin is top-left with the north pole on the top row, matching the GLB UV layout.
"""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor

import numpy as np
from PIL import Image

import shapefield

# fixed height-map scale (world-units of detail displacement mapped to [0,1]); deterministic
_HEIGHT_SCALE = 0.18


def _run_bands(height, block, fn, jobs):
    """Apply ``fn(y0, y1)`` over row-bands, optionally across ``jobs`` threads.

    Bands write into disjoint output rows, so threads never race; the heavy numpy work
    (exp/sin/BLAS) releases the GIL, giving real parallelism. Deterministic regardless of
    ``jobs`` (each band is independent and writes its own slice)."""
    bands = [(y, min(height, y + block)) for y in range(0, height, block)]
    if jobs and jobs > 1:
        with ThreadPoolExecutor(max_workers=jobs) as ex:
            list(ex.map(lambda b: fn(*b), bands))
    else:
        for b in bands:
            fn(*b)


def _row_geometry(width, height, y0, y1):
    """Directions + spherical tangent basis + latitude band for rows [y0, y1)."""
    su = (np.arange(width, dtype=np.float32) + 0.5) / width
    sv = (np.arange(y0, y1, dtype=np.float32) + 0.5) / height
    lon = su * 2.0 * np.pi
    lat = (0.5 - sv) * np.pi
    lon, lat = np.meshgrid(lon, lat)
    cl, sl, clon, slon = np.cos(lat), np.sin(lat), np.cos(lon), np.sin(lon)
    u = np.stack([cl * clon, sl, cl * slon], axis=-1)
    dlon = np.stack([-cl * slon, np.zeros_like(cl), cl * clon], axis=-1)
    dlat = np.stack([-sl * clon, cl, -sl * slon], axis=-1)
    T0, B0 = _normalize(dlon), _normalize(dlat)
    deg = np.linalg.norm(dlon, axis=-1) < 1e-7
    T0[deg] = np.array([1.0, 0.0, 0.0], np.float32)
    B0[deg] = np.cross(u[deg], T0[deg])
    return u, T0, B0, float(lat.min()), float(lat.max())


def bake_normal(params, width=4096, height=2048, block=64, jobs=1) -> Image.Image:
    out = np.empty((height, width, 3), np.uint8)

    def band(y0, y1):
        u, T0, B0, lo, hi = _row_geometry(width, height, y0, y1)
        n, _ = shapefield.surface(u, params, lo, hi)
        ts = np.stack([np.sum(n * T0, -1), np.sum(n * B0, -1), np.sum(n * u, -1)], -1)
        ts = _normalize(ts)
        out[y0:y1] = np.clip((ts * 0.5 + 0.5) * 255.0 + 0.5, 0, 255).astype(np.uint8)

    _run_bands(height, block, band, jobs)
    return Image.fromarray(out, "RGB")


def bake_surface(params, width=2048, height=1024, block=64, jobs=1):
    col = params["colour"]
    tone_a = np.asarray(col["tone_a"], np.float32)
    tone_b = np.asarray(col["tone_b"], np.float32)

    albedo = np.empty((height, width, 3), np.uint8)
    orm = np.empty((height, width, 3), np.uint8)
    hgt = np.empty((height, width), np.uint16)

    def band(y0, y1):
        u, _, _, lo, hi = _row_geometry(width, height, y0, y1)
        n, det = shapefield.surface(u, params, lo, hi)

        slope = np.clip(np.sum(n * u, -1), 0.0, 1.0)                 # 1 = facing out, low = crevice wall
        cavity = np.clip(0.55 + 7.0 * det, 0.0, 1.0)                  # raised bright, recessed dark
        ao = np.clip(0.30 + 0.70 * cavity * (0.45 + 0.55 * slope), 0.05, 1.0)

        mottle = 0.5 + 0.5 * np.clip(shapefield.planewave(u, col["mottle"]), -1, 1)
        rvar = np.clip(shapefield.planewave(u, col["rough_var"]), -1, 1)

        # albedo: mineral mottling between shadow/lit tone, then darkened by AO
        base = tone_b[None, None, :] + mottle[..., None] * (tone_a - tone_b)[None, None, :]
        lin = base * (0.45 + 0.55 * ao)[..., None]
        albedo[y0:y1] = np.clip(_linear_to_srgb(lin) * 255.0 + 0.5, 0, 255).astype(np.uint8)

        rough = np.clip(col["rough_base"] + 0.14 * rvar - 0.10 * np.clip(8 * det, 0, 1), 0.18, 1.0)
        metal = np.clip(col["metal"] + 0.05 * rvar, 0.0, 1.0)
        orm[y0:y1, :, 0] = np.clip(ao * 255 + 0.5, 0, 255).astype(np.uint8)
        orm[y0:y1, :, 1] = np.clip(rough * 255 + 0.5, 0, 255).astype(np.uint8)
        orm[y0:y1, :, 2] = np.clip(metal * 255 + 0.5, 0, 255).astype(np.uint8)

        h01 = np.clip(det / _HEIGHT_SCALE * 0.5 + 0.5, 0.0, 1.0)
        hgt[y0:y1] = (h01 * 65535.0 + 0.5).astype(np.uint16)

    _run_bands(height, block, band, jobs)
    return (Image.fromarray(albedo, "RGB"),
            Image.fromarray(orm, "RGB"),
            Image.fromarray(hgt, "I;16"))


def _linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def _normalize(v):
    nrm = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(nrm == 0.0, 1.0, nrm)
