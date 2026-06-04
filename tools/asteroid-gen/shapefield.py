"""Deterministic asteroid shape field — the single source of randomness.

Asteroids come in three spectral *types*, each with a characteristic silhouette and PBR
material:

  * "carbonaceous" (C) — rounded rubble pile (lobed shape); very dark charcoal, rough, non-metallic.
  * "stony"        (S) — fractured rock (faceted + gouges); tan/grey, semi-rough, trace metal.
  * "metallic"     (M) — faceted nickel-iron gem (crystalline); steely, shiny (low roughness, metallic).

A star-shaped asteroid is a radial field ``r(u)`` over unit directions ``u`` (radius as a
function of direction, so no overhangs), in layers:

  * BASE   — silhouette by type. Drives the low-poly mesh and the normal map's low frequencies.
  * DETAIL — multi-scale relief (boulders -> pebbles -> grit, plus craters/pits). Feeds ONLY
             the textures, so the mesh silhouette stays clean while the surface reads rocky.
  * COLOUR — per-type tones + low-frequency mottling, used to synthesise albedo / ORM.

ALL randomness lives in :func:`params_from_seed`; the mesh (``points``) and every texture are
derived from the *same* field, so they cannot drift. Everything is closed-form, so surface
normals are analytic and capture full-resolution detail at any mesh density.
"""

from __future__ import annotations

import numpy as np

EPS = 1e-3
KINDS = ("carbonaceous", "stony", "metallic")

# type -> base silhouette generator
_SHAPE = {"carbonaceous": "lobed", "stony": "faceted_gouged", "metallic": "faceted"}

# type -> (albedo lit tone, albedo shadow tone, base roughness, metalness)
_MATERIAL = {
    "carbonaceous": ((0.075, 0.070, 0.064), (0.034, 0.032, 0.030), 0.95, 0.0),
    "stony":        ((0.36, 0.31, 0.26),    (0.17, 0.145, 0.12),    0.85, 0.06),
    "metallic":     ((0.56, 0.56, 0.59),    (0.30, 0.30, 0.33),     0.34, 0.92),
}

# bump amplitude cutoff used to estimate each bump's angular footprint (for culling)
_BUMP_CUTOFF = 0.02


# ---------------------------------------------------------------------------
# Seed -> shape parameters
# ---------------------------------------------------------------------------

def params_from_seed(
    seed: int,
    *,
    kind: str = "carbonaceous",
    radius: float = 20.0,
    # base silhouette
    lobes: int = 7,
    lumpiness: float = 0.35,
    facets: int = 24,
    gouges: int = 5,
    # medium geometric relief baked into the MESH (boulders + erosion)
    boulders: int = 48,
    relief: float = 0.17,
    # detail layer (textures only)
    roughness: float = 0.05,
    roughness_terms: int = 96,
    roughness_freq: tuple[float, float] = (10.0, 200.0),
    rocks: int = 240,
    craters: int = 16,
    rock_amp: float = 0.05,
    # optional per-entry colour nudges (on top of the per-seed variation)
    tint: tuple[float, float, float] | None = None,
    value: float | None = None,
) -> dict:
    """Derive a fully-explicit shape description from an integer seed."""
    if kind not in KINDS:
        raise ValueError(f"unknown kind {kind!r}; expected one of {KINDS}")
    rng = np.random.default_rng(int(seed) & 0xFFFFFFFF)

    p = {
        "seed": int(seed),
        "kind": kind,
        "shape": _SHAPE[kind],
        "radius": float(radius),
        "R0": 1.0,
        "base": _base_params(rng, _SHAPE[kind], lobes, lumpiness, facets, gouges, boulders, relief),
        "detail": _detail_params(rng, roughness, roughness_terms, roughness_freq,
                                 rocks, craters, rock_amp),
        "colour": _colour_params(rng, kind, tint=tint, value=value),
    }
    return _to_f32(p)


def _base_params(rng, shape, lobes, lumpiness, facets, gouges, boulders, relief) -> dict:
    if shape == "lobed":
        # primary lobes (big lumps) + secondary lobes (smaller, sharper) for interest
        L = np.concatenate([_random_unit_vectors(rng, lobes), _random_unit_vectors(rng, lobes)])
        amp = np.concatenate([lumpiness * rng.uniform(0.4, 1.0, lobes),
                              0.45 * lumpiness * rng.uniform(0.3, 0.8, lobes)])
        sharp = np.concatenate([rng.uniform(1.5, 6.0, lobes), rng.uniform(7.0, 16.0, lobes)])
        out = {"L": L, "amp": amp, "sharp": sharp}
    else:
        # faceted: random planes + 6 axis planes (guarantee bounded convex support)
        axes = np.array([[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -1, 0], [0, 0, 1], [0, 0, -1]], float)
        N = np.concatenate([axes, _random_unit_vectors(rng, facets)])
        d = rng.uniform(0.78, 1.12, len(N))
        out = {"N": N, "d": d}
        if shape == "faceted_gouged":
            out["gouge"] = {
                "C": _random_unit_vectors(rng, gouges),
                "amp": rng.uniform(0.10, 0.22, gouges),
                "sharp": rng.uniform(8.0, 22.0, gouges),
            }
    out["medium"] = _medium_params(rng, boulders, relief)
    return out


def _medium_params(rng, boulders, relief) -> dict:
    """Multi-scale Gaussian bumps baked into the MESH geometry (broad erosion + boulders +
    cobbles), so the silhouette and faces break up into rock instead of staying flat."""
    broad, cobbles = 16, boulders
    C = np.concatenate([_random_unit_vectors(rng, broad),
                        _random_unit_vectors(rng, boulders),
                        _random_unit_vectors(rng, cobbles)])
    amp = np.concatenate([
        relief * rng.uniform(-1.0, 1.0, broad),                                   # broad +/- erosion
        0.85 * relief * rng.uniform(0.5, 1.0, boulders)
        * np.where(rng.uniform(size=boulders) < 0.72, 1.0, -0.7),                 # boulders / pits
        0.5 * relief * rng.uniform(0.4, 1.0, cobbles)
        * np.where(rng.uniform(size=cobbles) < 0.7, 1.0, -0.6),                   # cobbles
    ])
    sharp = np.concatenate([rng.uniform(5.0, 14.0, broad),
                            rng.uniform(20.0, 60.0, boulders),
                            rng.uniform(70.0, 150.0, cobbles)])
    rho = np.arccos(np.clip(1.0 + np.log(_BUMP_CUTOFF) / sharp, -1.0, 1.0))
    lat = np.arcsin(np.clip(C[:, 1], -1.0, 1.0))
    return {"C": C, "amp": amp, "sharp": sharp, "rho": rho, "lat": lat}


def _detail_params(rng, roughness, rough_terms, rough_freq, rocks, craters, rock_amp) -> dict:
    fmin, fmax = rough_freq
    fdir = _random_unit_vectors(rng, rough_terms)
    fmag = rng.uniform(fmin, fmax, rough_terms)
    rough = {
        "F": fdir * fmag[:, None],
        "phase": rng.uniform(0.0, 2.0 * np.pi, rough_terms),
        "amp": roughness * rng.uniform(0.3, 1.0, rough_terms) * (fmin / fmag),  # pink-ish
    }
    # fine Gaussian bumps: pebbles (tight) + craters (broad pits)
    C, amp, sharp = [], [], []
    C.append(_random_unit_vectors(rng, rocks))
    psign = np.where(rng.uniform(size=rocks) < 0.8, 1.0, -0.6)
    amp.append(rock_amp * rng.uniform(0.4, 1.0, rocks) * psign)
    sharp.append(rng.uniform(150.0, 700.0, rocks))
    C.append(_random_unit_vectors(rng, craters))
    amp.append(-rock_amp * rng.uniform(0.8, 2.0, craters))
    sharp.append(rng.uniform(8.0, 28.0, craters))
    C = np.concatenate(C); amp = np.concatenate(amp); sharp = np.concatenate(sharp)
    # precompute angular footprint (radius) + centre latitude for latitude-band culling
    rho = np.arccos(np.clip(1.0 + np.log(_BUMP_CUTOFF) / sharp, -1.0, 1.0))
    lat = np.arcsin(np.clip(C[:, 1], -1.0, 1.0))
    return {"roughness": rough, "bumps": {"C": C, "amp": amp, "sharp": sharp, "rho": rho, "lat": lat}}


def _colour_params(rng, kind, *, tint=None, value=None) -> dict:
    tone_a, tone_b, rough_base, metal = _MATERIAL[kind]
    tone_a = np.array(tone_a, float)
    tone_b = np.array(tone_b, float)

    # per-seed variation so two asteroids of the same type don't read identically. Brightness
    # alone just reads as "different lighting", so the variety comes from a coherent per-seed
    # *hue* lean: a warm<->cool axis (red up / blue down) plus a green<->magenta axis. The warm
    # axis is anti-correlated across R/B so luminance stays put and the rock genuinely changes
    # colour rather than just value. Amplitudes are proportional (multiplicative), tuned to be
    # clearly visible but still mineral (brass / steel / cool-grey / olive), not neon.
    bright = rng.uniform(0.78, 1.22)
    warm = rng.uniform(-0.14, 0.18)        # + warm (brassy), - cool (steely blue)
    grn = rng.uniform(-0.09, 0.09)         # + green/olive, - magenta
    hue = 1.0 + np.array([warm, grn, -warm])
    # deliberate per-entry nudges from the catalog (default: none)
    if value is not None:
        bright *= float(value)
    add = np.zeros(3) if tint is None else np.asarray(tint, float)

    tone_a = tone_a * bright * hue + add
    tone_b = tone_b * bright * hue + add

    def lowfreq(terms, flo, fhi):
        d = _random_unit_vectors(rng, terms)
        m = rng.uniform(flo, fhi, terms)
        return {"F": d * m[:, None], "phase": rng.uniform(0, 2 * np.pi, terms),
                "amp": rng.uniform(0.5, 1.0, terms) / np.sqrt(terms)}

    return {
        "tone_a": tone_a.clip(0.02, 0.95),
        "tone_b": tone_b.clip(0.01, 0.9),
        "rough_base": float(rough_base),
        "metal": float(metal),
        "mottle": lowfreq(10, 1.2, 4.5),
        "rough_var": lowfreq(8, 1.5, 5.5),
    }


# ---------------------------------------------------------------------------
# Field evaluation
# ---------------------------------------------------------------------------

def _bumps(u, C, amp, sharp):
    """Sum of Gaussian bumps and its gradient: a*exp(s*(u·C - 1))."""
    if len(amp) == 0:
        z = np.zeros(u.shape[:-1], u.dtype)
        return z, np.zeros_like(u)
    e = amp * np.exp(sharp * (u @ C.T - 1.0))
    return e.sum(-1), ((e * sharp)[..., None] * C).sum(-2)


def _crystal(u, N, d):
    """Convex faceted support r = min_i d_i/max(u·N_i, eps) and its gradient."""
    denom = np.maximum(u @ N.T, EPS)
    t = d / denom
    idx = np.argmin(t, axis=-1)
    r = np.take_along_axis(t, idx[..., None], -1)[..., 0]
    denom_sel = np.take_along_axis(denom, idx[..., None], -1)[..., 0]
    return r, -(r / denom_sel)[..., None] * N[idx]


def _roughness(u, rough):
    arg = u @ rough["F"].T + rough["phase"]
    r = (rough["amp"] * np.sin(arg)).sum(-1)
    grad = ((rough["amp"] * np.cos(arg))[..., None] * rough["F"]).sum(-2)
    return r, grad


def planewave(u, pw):
    """Scalar band-limited field sum_j amp_j*sin(F_j·u + phase_j), roughly in [-1, 1]."""
    return (pw["amp"] * np.sin(u @ pw["F"].T + pw["phase"])).sum(-1)


def _cull(bm, lat_lo, lat_hi):
    """Select only the bumps whose angular footprint reaches the [lat_lo, lat_hi] band."""
    if lat_lo is None:
        return bm["C"], bm["amp"], bm["sharp"]
    keep = (bm["lat"] >= lat_lo - bm["rho"]) & (bm["lat"] <= lat_hi + bm["rho"])
    return bm["C"][keep], bm["amp"][keep], bm["sharp"][keep]


def _eval_shape(u, p):
    """The type silhouette only (lobes / facets), without the medium relief."""
    b, R0, shape = p["base"], p["R0"], p["shape"]
    if shape == "lobed":
        rb, gb = _bumps(u, b["L"], b["amp"], b["sharp"])
        return R0 * (1.0 + rb), R0 * gb
    r, grad = _crystal(u, b["N"], b["d"])
    if shape == "faceted_gouged":
        rg, gg = _bumps(u, b["gouge"]["C"], b["gouge"]["amp"], b["gouge"]["sharp"])
        r, grad = r - rg, grad - gg
    return r, grad


def eval_base(u: np.ndarray, p: dict, lat_lo=None, lat_hi=None) -> tuple[np.ndarray, np.ndarray]:
    """Base radial field value + gradient (silhouette + medium relief; drives the mesh)."""
    u = np.asarray(u)
    r, grad = _eval_shape(u, p)
    rm, gm = _bumps(u, *_cull(p["base"]["medium"], lat_lo, lat_hi))
    return r + rm, grad + gm


def radius(u: np.ndarray, p: dict) -> np.ndarray:
    """Unit-field BASE radius (mesh silhouette); excludes detail. No ``p['radius']``."""
    return eval_base(_normalize(np.asarray(u, np.float32)), p)[0]


def points(u: np.ndarray, p: dict) -> np.ndarray:
    """Surface points ``radius * r_base(u) * u`` in world units (mesh geometry)."""
    u = _normalize(np.asarray(u, np.float32))
    return (p["radius"] * radius(u, p))[..., None] * u


def surface(u: np.ndarray, p: dict, lat_lo: float | None = None, lat_hi: float | None = None):
    """Return (normal, detail_height) for the full base+detail surface at ``u``.

    ``normal`` is the analytic outward unit normal; ``detail_height`` is the radial detail
    displacement (>0 raised, <0 recessed) used as a cavity signal for AO / albedo.

    If ``lat_lo``/``lat_hi`` (radians) are given, only bumps whose angular footprint reaches
    that latitude band are evaluated — a big speedup for the many tight high-frequency bumps.
    """
    u = _normalize(np.asarray(u, np.float32))
    rb, gb = eval_base(u, p, lat_lo, lat_hi)              # silhouette + (culled) medium relief
    rv, gv = _roughness(u, p["detail"]["roughness"])      # fine grit
    rk, gk = _bumps(u, *_cull(p["detail"]["bumps"], lat_lo, lat_hi))  # pebbles + craters

    fine = rv + rk                                        # fine relief -> cavity signal for AO
    r = rb + fine
    grad = gb + gv + gk
    grad_tan = grad - np.sum(grad * u, -1, keepdims=True) * u
    return _normalize(u - grad_tan / r[..., None]), fine


def surface_normal(u: np.ndarray, p: dict) -> np.ndarray:
    return surface(u, p)[0]


# ---------------------------------------------------------------------------
# Lat/long tessellation (builds the GLB mesh)
# ---------------------------------------------------------------------------

def lonlat_grid(nlat: int, nlon: int) -> dict:
    """UV-sphere direction grid with a duplicated seam column for clean UVs (y-up)."""
    lats = np.linspace(-np.pi / 2, np.pi / 2, nlat + 1)
    lons = np.linspace(0.0, 2.0 * np.pi, nlon + 1)
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
# JSON helpers / utilities
# ---------------------------------------------------------------------------

def params_to_jsonable(p: dict) -> dict:
    def conv(x):
        if isinstance(x, dict):
            return {k: conv(v) for k, v in x.items()}
        if isinstance(x, np.ndarray):
            return x.tolist()
        if isinstance(x, np.floating):
            return float(x)
        return x
    return {k: conv(v) for k, v in p.items()}


def _to_f32(p):
    if isinstance(p, dict):
        return {k: _to_f32(v) for k, v in p.items()}
    if isinstance(p, np.ndarray):
        return p.astype(np.float32)
    return p


def _normalize(v: np.ndarray) -> np.ndarray:
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(n == 0.0, 1.0, n)


def _random_unit_vectors(rng: np.random.Generator, n: int) -> np.ndarray:
    return _normalize(rng.standard_normal((n, 3)))
