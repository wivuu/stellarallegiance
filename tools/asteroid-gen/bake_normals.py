"""Bake an equirectangular tangent-space normal map from the analytic shape field.

For every texel we recover a unit direction ``u`` (lat/long), build the *spherical*
tangent basis at that direction (the same basis the GLB mesh is authored against),
sample the analytic surface normal of the full (base + detail) surface, and store it
in that basis:

    N0 = u                 (mesh shading normal — sphere direction, the "carrier")
    T0 = d u / d lon        (tangent, points east / +U)
    B0 = d u / d lat        (bitangent, points north)

    texel = ( n·T0, n·B0, n·N0 )  ->  encoded to RGB

The GLB exporter authors vertex NORMAL = u and TANGENT = T0 with matching UVs, so Godot
reconstructs ``n`` exactly: all surface relief (lobes/facets AND the high-frequency
rocks/roughness) comes from this map while the low-poly mesh only provides the silhouette.

Convention: OpenGL-style tangent-space normals (green = +Y / +B0), Godot's default
NormalMap expectation. Image origin is top-left with the north pole on the top row.

Rows are processed in chunks so the many-term detail layer stays within bounded memory.
"""

from __future__ import annotations

import numpy as np
from PIL import Image

import shapefield


def _row_dirs(width: int, height: int, y0: int, y1: int):
    """Directions + spherical tangent basis for image rows [y0, y1)."""
    su = (np.arange(width) + 0.5) / width
    sv = (np.arange(y0, y1) + 0.5) / height
    lon = su * 2.0 * np.pi
    lat = (0.5 - sv) * np.pi
    lon, lat = np.meshgrid(lon, lat)                 # (rows, W)

    cl, sl = np.cos(lat), np.sin(lat)
    clon, slon = np.cos(lon), np.sin(lon)
    u = np.stack([cl * clon, sl, cl * slon], axis=-1)
    dlon = np.stack([-cl * slon, np.zeros_like(cl), cl * clon], axis=-1)
    dlat = np.stack([-sl * clon, cl, -sl * slon], axis=-1)

    T0 = _normalize(dlon)
    B0 = _normalize(dlat)
    degenerate = np.linalg.norm(dlon, axis=-1) < 1e-8     # poles
    T0[degenerate] = np.array([1.0, 0.0, 0.0])
    B0[degenerate] = np.cross(u[degenerate], T0[degenerate])
    return u, T0, B0


def bake(params: dict, width: int = 2048, height: int = 1024, block: int = 64) -> Image.Image:
    """Render the tangent-space normal map for the given shape params."""
    out = np.empty((height, width, 3), dtype=np.uint8)
    for y0 in range(0, height, block):
        y1 = min(height, y0 + block)
        u, T0, B0 = _row_dirs(width, height, y0, y1)
        n = shapefield.surface_normal(u, params)
        ts = np.stack([np.sum(n * T0, -1), np.sum(n * B0, -1), np.sum(n * u, -1)], axis=-1)
        ts = _normalize(ts)
        out[y0:y1] = np.clip((ts * 0.5 + 0.5) * 255.0 + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(out, mode="RGB")


def _normalize(v: np.ndarray) -> np.ndarray:
    nrm = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(nrm == 0.0, 1.0, nrm)
