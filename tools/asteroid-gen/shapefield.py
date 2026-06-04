"""Deterministic asteroid shape field — the single source of randomness.

A star-shaped asteroid is defined by a radial field ``r(u)`` over unit
directions ``u`` (radius as a function of direction, so there are no overhangs):

    r(u) = R0 * ( 1
                  + sum_i  a_i * exp(s_i * (u·L_i - 1))     # low-freq lobes (lumps)
                  + sum_j  b_j * sin(F_j · u + p_j) )       # high-freq detail (craters)

The surface point is ``P(u) = radius * r(u) * u``.

ALL randomness lives in :func:`params_from_seed`. OpenSCAD (``asteroid.scad``) and
the normal-map baker (``bake_normals.py``) both consume the *same* explicit params,
so the mesh and the normal map are mathematically guaranteed to align — no RNG runs
in two languages, so nothing can drift.

Everything is closed-form, so the surface normal is computed analytically (not from
triangle faces): the map captures the full-resolution shape regardless of mesh density.
"""

from __future__ import annotations

import numpy as np


# ---------------------------------------------------------------------------
# Seed -> shape parameters
# ---------------------------------------------------------------------------

def params_from_seed(
    seed: int,
    *,
    radius: float = 1.0,
    lobes: int = 7,
    lumpiness: float = 0.35,
    detail: float = 0.12,
    detail_terms: int = 28,
    detail_freq: tuple[float, float] = (3.0, 11.0),
) -> dict:
    """Derive a fully-explicit shape description from an integer seed.

    The returned dict is JSON-serialisable (after :func:`params_to_jsonable`) and is
    the only thing the OpenSCAD lib and the baker need — given identical params they
    produce identical geometry.

    Parameters
    ----------
    radius      : overall scale (world units) applied to the unit field.
    lobes       : number of low-frequency bumps (overall lumpy silhouette).
    lumpiness   : amplitude of the lobes (fraction of radius).
    detail      : amplitude of the high-frequency surface detail.
    detail_terms: number of band-limited plane-wave terms for fine detail.
    detail_freq : (min, max) spatial frequency range for the detail terms.
    """
    rng = np.random.default_rng(int(seed) & 0xFFFFFFFF)

    # --- low-frequency lobes: random directions, amplitudes, sharpness ---
    L = _random_unit_vectors(rng, lobes)
    lobe_amp = lumpiness * rng.uniform(0.4, 1.0, lobes)
    lobe_sharp = rng.uniform(1.5, 6.0, lobes)

    # --- high-frequency detail: plane waves with pink-ish amplitude falloff ---
    fmin, fmax = detail_freq
    freq_dirs = _random_unit_vectors(rng, detail_terms)
    freq_mag = rng.uniform(fmin, fmax, detail_terms)
    F = freq_dirs * freq_mag[:, None]
    phase = rng.uniform(0.0, 2.0 * np.pi, detail_terms)
    # amplitude ~ 1/frequency keeps higher octaves subtle and the surface bounded
    det_amp = detail * rng.uniform(0.3, 1.0, detail_terms) * (fmin / freq_mag)

    return {
        "seed": int(seed),
        "radius": float(radius),
        "R0": 1.0,
        "lobes": {"L": L, "amp": lobe_amp, "sharp": lobe_sharp},
        "detail": {"F": F, "phase": phase, "amp": det_amp},
    }


# ---------------------------------------------------------------------------
# Field evaluation (radius, gradient, analytic surface normal)
# ---------------------------------------------------------------------------

def _eval(u: np.ndarray, p: dict) -> tuple[np.ndarray, np.ndarray]:
    """Return (r, grad_r) for an array of directions ``u`` of shape (..., 3).

    ``grad_r`` is the gradient of ``r`` treated as a function of the raw vector
    components — used to derive the analytic surface normal.
    """
    u = np.asarray(u, dtype=float)
    lob, det, R0 = p["lobes"], p["detail"], p["R0"]

    # lobes: a * exp(s * (u·L - 1)); d/du = a*s*exp(...) * L
    dotL = u @ lob["L"].T                              # (..., nlobe)
    e = np.exp(lob["sharp"] * (dotL - 1.0))            # (..., nlobe)
    r = 1.0 + (lob["amp"] * e).sum(-1)
    grad = ((lob["amp"] * lob["sharp"] * e)[..., None] * lob["L"]).sum(-2)

    # detail: b * sin(F·u + p); d/du = b*cos(F·u + p) * F
    arg = u @ det["F"].T + det["phase"]                # (..., nterm)
    r = r + (det["amp"] * np.sin(arg)).sum(-1)
    grad = grad + ((det["amp"] * np.cos(arg))[..., None] * det["F"]).sum(-2)

    return R0 * r, R0 * grad


def radius(u: np.ndarray, p: dict) -> np.ndarray:
    """Unit-field radius for directions ``u`` (does NOT include ``p['radius']``)."""
    return _eval(u, p)[0]


def points(u: np.ndarray, p: dict) -> np.ndarray:
    """Surface points ``radius * r(u) * u`` in world units."""
    u = _normalize(np.asarray(u, dtype=float))
    r = radius(u, p)
    return (p["radius"] * r)[..., None] * u


def surface_normal(u: np.ndarray, p: dict) -> np.ndarray:
    """Analytic outward unit normal of the radial surface at directions ``u``.

    Uses the implicit surface ``G(x) = |x| - r(x̂) = 0``, whose gradient on the
    surface is ``x̂ - (1/r) * grad_tangential(r)``.
    """
    u = _normalize(np.asarray(u, dtype=float))
    r, grad = _eval(u, p)
    grad_tan = grad - np.sum(grad * u, -1, keepdims=True) * u
    n = u - grad_tan / r[..., None]
    return _normalize(n)


# ---------------------------------------------------------------------------
# Lat/long tessellation (shared by the GLB builder; STL is built in OpenSCAD)
# ---------------------------------------------------------------------------

def lonlat_grid(nlat: int, nlon: int) -> dict:
    """A UV-sphere direction grid with a duplicated seam column for clean UVs.

    Returns directions (M,3), texture UVs (M,2, origin top-left), and triangle
    indices (K,3). Poles are included as degenerate rows; vertex attributes are
    computed analytically so the degenerate triangles never produce NaNs.

    y-up convention (matches Godot):
        u = (cos(lat)cos(lon), sin(lat), cos(lat)sin(lon))
    """
    lats = np.linspace(-np.pi / 2, np.pi / 2, nlat + 1)        # south -> north
    lons = np.linspace(0.0, 2.0 * np.pi, nlon + 1)             # seam duplicated
    lat, lon = np.meshgrid(lats, lons, indexing="ij")          # (nlat+1, nlon+1)

    cl = np.cos(lat)
    dirs = np.stack([cl * np.cos(lon), np.sin(lat), cl * np.sin(lon)], axis=-1)
    dirs = dirs.reshape(-1, 3)

    u_tex = lon / (2.0 * np.pi)
    v_tex = 1.0 - (lat + np.pi / 2) / np.pi                     # north pole at top
    uv = np.stack([u_tex, v_tex], axis=-1).reshape(-1, 2)

    cols = nlon + 1
    faces = []
    for i in range(nlat):
        for j in range(nlon):
            a = i * cols + j
            b = a + 1
            c = a + cols
            d = c + 1
            faces.append((a, c, b))
            faces.append((b, c, d))
    faces = np.asarray(faces, dtype=np.uint32)

    return {"dirs": dirs, "uv": uv, "faces": faces}


# ---------------------------------------------------------------------------
# JSON helpers
# ---------------------------------------------------------------------------

def params_to_jsonable(p: dict) -> dict:
    """Convert numpy arrays in a params dict to plain lists for JSON / manifests."""
    return {
        "seed": p["seed"],
        "radius": p["radius"],
        "R0": p["R0"],
        "lobes": {k: np.asarray(v).tolist() for k, v in p["lobes"].items()},
        "detail": {k: np.asarray(v).tolist() for k, v in p["detail"].items()},
    }


# ---------------------------------------------------------------------------
# Small vector utilities
# ---------------------------------------------------------------------------

def _normalize(v: np.ndarray) -> np.ndarray:
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(n == 0.0, 1.0, n)


def _random_unit_vectors(rng: np.random.Generator, n: int) -> np.ndarray:
    """``n`` uniformly-distributed directions on the unit sphere."""
    v = rng.standard_normal((n, 3))
    return _normalize(v)
