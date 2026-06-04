"""Assemble a Godot-ready GLB from the shape field + a baked normal map.

The low-poly mesh carries:
  * POSITION   : displaced surface points (lumpy silhouette)
  * NORMAL     : the *sphere* direction (smooth carrier) so the tangent-space
                 normal map reconstructs the true detailed normal
  * TEXCOORD_0 : equirectangular UVs matching bake_normals.py
  * TANGENT    : d u / d lon (+ handedness w) so cross(N, T)*w = d u / d lat

The baked normal map is embedded as a PNG and bound to the material's normalTexture,
so importing the .glb into Godot "just works" with a StandardMaterial3D.
"""

from __future__ import annotations

import io
import struct

import numpy as np
import pygltflib
from PIL import Image

import shapefield

FLOAT = pygltflib.FLOAT
UINT = pygltflib.UNSIGNED_INT
ARRAY_BUFFER = pygltflib.ARRAY_BUFFER
ELEMENT_ARRAY_BUFFER = pygltflib.ELEMENT_ARRAY_BUFFER


def build_glb(params: dict, normal_png: Image.Image, nlat: int, nlon: int) -> bytes:
    """Return GLB bytes for the asteroid described by ``params``."""
    grid = shapefield.lonlat_grid(nlat, nlon)
    dirs = grid["dirs"]                                  # (N,3) sphere normals
    uv = grid["uv"].astype(np.float32)                   # (N,2)
    faces = grid["faces"].astype(np.uint32)              # (K,3)

    pos = shapefield.points(dirs, params).astype(np.float32)
    nrm = dirs.astype(np.float32)
    tan = _tangents(dirs).astype(np.float32)             # (N,4) xyz + w

    indices = faces.reshape(-1).astype(np.uint32)

    # --- pack one binary buffer (4-byte aligned views) ---
    blob = bytearray()
    views = []  # (byte_offset, byte_length, target_or_None)

    def add(data: bytes, target):
        _pad(blob)
        off = len(blob)
        blob.extend(data)
        views.append((off, len(data), target))
        return len(views) - 1

    v_pos = add(pos.tobytes(), ARRAY_BUFFER)
    v_nrm = add(nrm.tobytes(), ARRAY_BUFFER)
    v_uv = add(uv.tobytes(), ARRAY_BUFFER)
    v_tan = add(tan.tobytes(), ARRAY_BUFFER)
    v_idx = add(indices.tobytes(), ELEMENT_ARRAY_BUFFER)

    png_buf = io.BytesIO()
    normal_png.save(png_buf, format="PNG")
    v_img = add(png_buf.getvalue(), None)

    # --- accessors ---
    accessors = [
        pygltflib.Accessor(bufferView=v_pos, componentType=FLOAT, count=len(pos),
                           type="VEC3",
                           min=pos.min(axis=0).tolist(), max=pos.max(axis=0).tolist()),
        pygltflib.Accessor(bufferView=v_nrm, componentType=FLOAT, count=len(nrm),
                           type="VEC3"),
        pygltflib.Accessor(bufferView=v_uv, componentType=FLOAT, count=len(uv),
                           type="VEC2"),
        pygltflib.Accessor(bufferView=v_tan, componentType=FLOAT, count=len(tan),
                           type="VEC4"),
        pygltflib.Accessor(bufferView=v_idx, componentType=UINT, count=len(indices),
                           type="SCALAR"),
    ]
    A_POS, A_NRM, A_UV, A_TAN, A_IDX = range(5)

    buffer_views = [
        pygltflib.BufferView(buffer=0, byteOffset=o, byteLength=l, target=t)
        for (o, l, t) in views
    ]

    material = pygltflib.Material(
        name="asteroid",
        pbrMetallicRoughness=pygltflib.PbrMetallicRoughness(
            baseColorFactor=[0.45, 0.42, 0.38, 1.0],
            metallicFactor=0.0,
            roughnessFactor=0.95,
        ),
        normalTexture=pygltflib.NormalMaterialTexture(index=0),
    )

    gltf = pygltflib.GLTF2(
        scene=0,
        scenes=[pygltflib.Scene(nodes=[0])],
        nodes=[pygltflib.Node(mesh=0, name=f"asteroid_{params['seed']}")],
        meshes=[pygltflib.Mesh(primitives=[pygltflib.Primitive(
            attributes=pygltflib.Attributes(
                POSITION=A_POS, NORMAL=A_NRM, TEXCOORD_0=A_UV, TANGENT=A_TAN),
            indices=A_IDX,
            material=0,
        )])],
        materials=[material],
        textures=[pygltflib.Texture(source=0, sampler=0)],
        samplers=[pygltflib.Sampler(wrapS=pygltflib.REPEAT, wrapT=pygltflib.CLAMP_TO_EDGE)],
        images=[pygltflib.Image(bufferView=v_img, mimeType="image/png")],
        accessors=accessors,
        bufferViews=buffer_views,
        buffers=[pygltflib.Buffer(byteLength=len(blob))],
    )
    gltf.set_binary_blob(bytes(blob))
    return b"".join(gltf.save_to_bytes())


def _tangents(dirs: np.ndarray) -> np.ndarray:
    """Per-vertex glTF TANGENT (vec4): T = normalize(d u / d lon), w = handedness."""
    x, y, z = dirs[:, 0], dirs[:, 1], dirs[:, 2]
    lat = np.arcsin(np.clip(y, -1.0, 1.0))
    lon = np.arctan2(z, x)
    cl, sl = np.cos(lat), np.sin(lat)
    clon, slon = np.cos(lon), np.sin(lon)

    dlon = np.stack([-cl * slon, np.zeros_like(cl), cl * clon], axis=-1)
    dlat = np.stack([-sl * clon, cl, -sl * slon], axis=-1)

    T = _normalize(dlon)
    B = _normalize(dlat)
    degenerate = np.linalg.norm(dlon, axis=-1) < 1e-8
    T[degenerate] = np.array([1.0, 0.0, 0.0])
    B[degenerate] = np.cross(dirs[degenerate], T[degenerate])

    # w so that cross(N, T) * w == B   (N == sphere direction == dirs)
    w = np.sign(np.sum(np.cross(dirs, T) * B, axis=-1))
    w[w == 0] = 1.0
    return np.concatenate([T, w[:, None]], axis=-1)


def _normalize(v: np.ndarray) -> np.ndarray:
    nrm = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.where(nrm == 0.0, 1.0, nrm)


def _pad(buf: bytearray, align: int = 4):
    while len(buf) % align != 0:
        buf.append(0)
