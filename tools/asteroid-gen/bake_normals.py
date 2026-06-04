"""Bake an equirectangular tangent-space normal map from the analytic shape field.

For every texel ``(s, t)`` of the map we recover a unit direction ``u`` (lat/long),
build the *spherical* tangent basis at that direction (the same basis the GLB mesh is
authored against), sample the analytic surface normal, and store it in that basis:

    N0 = u                 (mesh shading normal — sphere direction, the "carrier")
    T0 = d u / d lon        (tangent, points east / +U)
    B0 = d u / d lat        (bitangent, points north)

    texel = ( n·T0, n·B0, n·N0 )  ->  encoded to RGB

Because the GLB exporter authors vertex NORMAL = u and TANGENT = T0 with matching
UVs, Godot reconstructs ``n`` exactly: all surface relief comes from this map while
the low-poly mesh only provides the silhouette.

Convention: OpenGL-style tangent-space normals (green = +Y / +B0), which is what
Godot's StandardMaterial3D expects (NormalMap, *not* flipped). Image origin is
top-left with the north pole on the top row, matching the GLB's UV (v = 0 at north).
"""

from __future__ import annotations

import numpy as np
from PIL import Image

import shapefield


def equirect_dirs(width: int, height: int):
    """Return (dirs, T0, B0) arrays of shape (height, width, 3) for an equirect map.

    Texel centers are sampled. Row 0 is the north pole (v small), matching the GLB UV
    layout produced by :func:`shapefield.lonlat_grid`.
    """
    # pixel centers -> [0,1)
    su = (np.arange(width) + 0.5) / width
    sv = (np.arange(height) + 0.5) / height
    lon = su * 2.0 * np.pi                         # 0 .. 2pi  (east)
    lat = (0.5 - sv) * np.pi                        # +pi/2 (top) .. -pi/2 (bottom)
    lon, lat = np.meshgrid(lon, lat)                # (H, W)

    cl, sl = np.cos(lat), np.sin(lat)
    clon, slon = np.cos(lon), np.sin(lon)

    # direction (y-up), and its analytic derivatives w.r.t lon and lat
    u = np.stack([cl * clon, sl, cl * slon], axis=-1)
    dlon = np.stack([-cl * slon, np.zeros_like(cl), cl * clon], axis=-1)   # d u / d lon
    dlat = np.stack([-sl * clon, cl, -sl * slon], axis=-1)                 # d u / d lat

    T0 = _normalize(dlon)
    B0 = _normalize(dlat)
    # at the poles d/dlon vanishes; substitute a stable arbitrary tangent there
    degenerate = np.linalg.norm(dlon, axis=-1) < 1e-8
    T0[degenerate] = np.array([1.0, 0.0, 0.0])
    B0[degenerate] = np.cross(u[degenerate], T0[degenerate])
    return u, T0, B0


def bake(params: dict, width: int = 1024, height: int = 512) -> Image.Image:
    """Render the tangent-space normal map for the given shape params."""
    u, T0, B0 = equirect_dirs(width, height)
    n = shapefield.surface_normal(u, params)        # (H, W, 3) world-space normals

    # express the detailed normal in the spherical tangent basis
    nt = np.sum(n * T0, axis=-1)
    nb = np.sum(n * B0, axis=-1)
    nn = np.sum(n * u, axis=-1)
    tangent_space = np.stack([nt, nb, nn], axis=-1)
    tangent_space = _normalize(tangent_space)

    rgb = np.clip((tangent_space * 0.5 + 0.5) * 255.0 + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(rgb, mode="RGB")


def _normalize(v: np.ndarray) -> np.ndarray:
    nrm = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(nrm == 0.0, 1.0, nrm)
