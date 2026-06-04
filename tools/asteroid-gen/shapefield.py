"""Deterministic asteroid shape field — the single source of randomness.

A star-shaped asteroid is a radial field ``r(u)`` over unit directions ``u`` (radius
as a function of direction, so there are no overhangs). The field has two layers:

  * BASE  — the overall silhouette, one of several distinguishable *kinds*. This drives
            the low-poly mesh (OpenSCAD mirrors it) and the normal map's low frequencies.
  * DETAIL — high-frequency roughness + scattered "rocks" (tight bumps). This feeds ONLY
            the normal map, so the mesh silhouette stays clean while the surface looks rocky.

Kinds (base shape):
  * "bulbous"     — smooth Gaussian lobes (rounded, lumpy).
  * "crystalline" — convex faceted gem: r(u) = min_i d_i / max(u·N_i, eps). Flat faces, sharp edges.
  * "angular"     — faceted base with concave gouges subtracted (chunky, fractured rock).

ALL randomness lives in :func:`params_from_seed`. OpenSCAD (``asteroid.scad``) and the
baker both consume the *same* explicit params; only the BASE needs mirroring in OpenSCAD
(the detail layer is map-only), so the mesh and map cannot drift.

Everything is closed-form, so the surface normal is analytic (computed from the implicit
surface ``G(x) = |x| - r(x̂) = 0``), capturing full-resolution detail at any mesh density.
"""

from __future__ import annotations

import numpy as np

EPS = 1e-3
KINDS = ("bulbous", "crystalline", "angular")


# ---------------------------------------------------------------------------
# Seed -> shape parameters
# ---------------------------------------------------------------------------

def params_from_seed(
    seed: int,
    *,
    kind: str = "bulbous",
    radius: float = 20.0,
    # base silhouette
    lobes: int = 7,
    lumpiness: float = 0.35,
    facets: int = 11,
    gouges: int = 4,
    # detail layer (normal map only)
    roughness: float = 0.05,
    roughness_terms: int = 56,
    roughness_freq: tuple[float, float] = (8.0, 70.0),
    rocks: int = 320,
    rock_amp: float = 0.05,
    rock_sharp: tuple[float, float] = (120.0, 600.0),
) -> dict:
    """Derive a fully-explicit shape description from an integer seed.

    ``kind`` selects the base silhouette. Detail params (roughness/rocks) apply to all
    kinds and control how busy the normal map's small-scale relief is.
    """
    if kind not in KINDS:
        raise ValueError(f"unknown kind {kind!r}; expected one of {KINDS}")
    rng = np.random.default_rng(int(seed) & 0xFFFFFFFF)

    base = _base_params(rng, kind, lobes, lumpiness, facets, gouges)

    # --- detail: high-frequency roughness (band-limited plane waves) ---
    fmin, fmax = roughness_freq
    fdir = _random_unit_vectors(rng, roughness_terms)
    fmag = rng.uniform(fmin, fmax, roughness_terms)
    F = fdir * fmag[:, None]
    rough = {
        "F": F,
        "phase": rng.uniform(0.0, 2.0 * np.pi, roughness_terms),
        # amplitude ~ 1/frequency keeps the surface bounded (pink-ish spectrum)
        "amp": roughness * rng.uniform(0.3, 1.0, roughness_terms) * (fmin / fmag),
    }

    # --- detail: scattered rocks/pits (tight Gaussian bumps) ---
    rsign = np.where(rng.uniform(size=rocks) < 0.8, 1.0, -0.6)  # mostly bumps, some pits
    rock = {
        "C": _random_unit_vectors(rng, rocks),
        "sharp": rng.uniform(rock_sharp[0], rock_sharp[1], rocks),
        "amp": rock_amp * rng.uniform(0.4, 1.0, rocks) * rsign,
    }

    return {
        "seed": int(seed),
        "kind": kind,
        "radius": float(radius),
        "R0": 1.0,
        "base": base,
        "detail": {"roughness": rough, "rocks": rock},
    }


def _base_params(rng, kind, lobes, lumpiness, facets, gouges) -> dict:
    if kind == "bulbous":
        return {
            "L": _random_unit_vectors(rng, lobes),
            "amp": lumpiness * rng.uniform(0.4, 1.0, lobes),
            "sharp": rng.uniform(1.5, 6.0, lobes),
        }
    # faceted kinds: random planes + 6 axis planes (guarantee directional coverage so
    # the convex support is bounded in every direction).
    axes = np.array([[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]], float)
    rand = _random_unit_vectors(rng, facets)
    N = np.concatenate([axes, rand], axis=0)
    d = rng.uniform(0.78, 1.12, len(N))
    out = {"N": N, "d": d}
    if kind == "angular":
        out["gouge"] = {
            "C": _random_unit_vectors(rng, gouges),
            "amp": rng.uniform(0.10, 0.22, gouges),
            "sharp": rng.uniform(8.0, 22.0, gouges),
        }
    return out


# ---------------------------------------------------------------------------
# Field evaluation
# ---------------------------------------------------------------------------

def _bumps(u, C, amp, sharp):
    """Sum of Gaussian bumps and its gradient: a*exp(s*(u·C - 1))."""
    e = amp * np.exp(sharp * (u @ C.T - 1.0))         # (..., n)
    r = e.sum(-1)
    grad = ((e * sharp)[..., None] * C).sum(-2)
    return r, grad


def _crystal(u, N, d):
    """Convex faceted support r = min_i d_i/max(u·N_i, eps), and its gradient.

    The implicit-surface normal of this radial field recovers the active facet normal.
    """
    udotN = u @ N.T                                    # (..., M)
    denom = np.maximum(udotN, EPS)
    t = d / denom
    idx = np.argmin(t, axis=-1)                        # (...)
    r = np.take_along_axis(t, idx[..., None], -1)[..., 0]
    Nsel = N[idx]                                      # (..., 3)
    denom_sel = np.take_along_axis(denom, idx[..., None], -1)[..., 0]
    grad = -(r / denom_sel)[..., None] * Nsel
    return r, grad


def eval_base(u: np.ndarray, p: dict) -> tuple[np.ndarray, np.ndarray]:
    """Base radial field value + gradient (mirrored in OpenSCAD)."""
    u = np.asarray(u, float)
    b, R0, kind = p["base"], p["R0"], p["kind"]
    if kind == "bulbous":
        rb, gb = _bumps(u, b["L"], b["amp"], b["sharp"])
        return R0 * (1.0 + rb), R0 * gb
    r, grad = _crystal(u, b["N"], b["d"])
    if kind == "angular":
        rg, gg = _bumps(u, b["gouge"]["C"], b["gouge"]["amp"], b["gouge"]["sharp"])
        r = r - rg
        grad = grad - gg
    return r, grad


def eval_detail(u: np.ndarray, p: dict) -> tuple[np.ndarray, np.ndarray]:
    """High-frequency detail value + gradient (normal map only)."""
    u = np.asarray(u, float)
    rough, rock = p["detail"]["roughness"], p["detail"]["rocks"]
    arg = u @ rough["F"].T + rough["phase"]
    r = (rough["amp"] * np.sin(arg)).sum(-1)
    grad = ((rough["amp"] * np.cos(arg))[..., None] * rough["F"]).sum(-2)
    rr, gr = _bumps(u, rock["C"], rock["amp"], rock["sharp"])
    return r + rr, grad + gr


def radius(u: np.ndarray, p: dict) -> np.ndarray:
    """Unit-field BASE radius (mesh silhouette); excludes detail. No ``p['radius']``."""
    return eval_base(_normalize(np.asarray(u, float)), p)[0]


def points(u: np.ndarray, p: dict) -> np.ndarray:
    """Surface points ``radius * r_base(u) * u`` in world units (mesh geometry)."""
    u = _normalize(np.asarray(u, float))
    return (p["radius"] * radius(u, p))[..., None] * u


def surface_normal(u: np.ndarray, p: dict) -> np.ndarray:
    """Analytic outward unit normal of the full (base + detail) surface at ``u``.

    Uses ``G(x) = |x| - r(x̂)``; on the surface the normal is
    ``x̂ - (1/r) * grad_tangential(r)``.
    """
    u = _normalize(np.asarray(u, float))
    rb, gb = eval_base(u, p)
    rd, gd = eval_detail(u, p)
    r = rb + rd
    grad = gb + gd
    grad_tan = grad - np.sum(grad * u, -1, keepdims=True) * u
    return _normalize(u - grad_tan / r[..., None])


# ---------------------------------------------------------------------------
# Lat/long tessellation (shared by the GLB builder; STL is built in OpenSCAD)
# ---------------------------------------------------------------------------

def lonlat_grid(nlat: int, nlon: int) -> dict:
    """A UV-sphere direction grid with a duplicated seam column for clean UVs.

    Returns directions (M,3), texture UVs (M,2, origin top-left, north pole at top),
    and triangle indices (K,3). y-up (matches Godot):
        u = (cos(lat)cos(lon), sin(lat), cos(lat)sin(lon))
    """
    lats = np.linspace(-np.pi / 2, np.pi / 2, nlat + 1)        # south -> north
    lons = np.linspace(0.0, 2.0 * np.pi, nlon + 1)             # seam duplicated
    lat, lon = np.meshgrid(lats, lons, indexing="ij")

    cl = np.cos(lat)
    dirs = np.stack([cl * np.cos(lon), np.sin(lat), cl * np.sin(lon)], axis=-1).reshape(-1, 3)
    uv = np.stack([lon / (2.0 * np.pi), 1.0 - (lat + np.pi / 2) / np.pi], axis=-1).reshape(-1, 2)

    cols = nlon + 1
    faces = []
    for i in range(nlat):
        for j in range(nlon):
            a = i * cols + j
            faces.append((a, a + cols, a + 1))
            faces.append((a + 1, a + cols, a + cols + 1))
    return {"dirs": dirs, "uv": uv, "faces": np.asarray(faces, dtype=np.uint32)}


# ---------------------------------------------------------------------------
# JSON helpers
# ---------------------------------------------------------------------------

def params_to_jsonable(p: dict) -> dict:
    def conv(x):
        if isinstance(x, dict):
            return {k: conv(v) for k, v in x.items()}
        if isinstance(x, np.ndarray):
            return x.tolist()
        return x
    return {k: conv(v) for k, v in p.items()}


# ---------------------------------------------------------------------------
# Small vector utilities
# ---------------------------------------------------------------------------

def _normalize(v: np.ndarray) -> np.ndarray:
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(n == 0.0, 1.0, n)


def _random_unit_vectors(rng: np.random.Generator, n: int) -> np.ndarray:
    return _normalize(rng.standard_normal((n, 3)))
