"""Assemble a Godot-ready GLB from placed parts + hardpoints.

One self-contained ``.glb`` per ship bundles everything the client loads:
  * one glTF primitive per part (POSITION/NORMAL/TEXCOORD_0/TANGENT + indices),
  * one PBR material per material *kind*, with its albedo / normal / ORM PNGs embedded as
    buffer-views (no external files), and
  * empty ``HP_<Kind>_<Index>`` nodes parented under the ship root, each oriented so its
    local +Z is the hardpoint forward (the game's convention, mirroring
    ShipModelLoader.BasisFacingZ).
"""

from __future__ import annotations

import io

import numpy as np
import pygltflib
from PIL import Image

import bake

FLOAT = pygltflib.FLOAT
UINT = pygltflib.UNSIGNED_INT
ARRAY_BUFFER = pygltflib.ARRAY_BUFFER
ELEMENT_ARRAY_BUFFER = pygltflib.ELEMENT_ARRAY_BUFFER


def build_glb(name: str, placed: list[tuple[dict, str]], hardpoints: list[dict],
              *, seed: int = 0, tex_size: int = 512) -> bytes:
    """``placed`` is a list of (mesh, kind); ``hardpoints`` a list of
    {kind, index, offset:[x,y,z], forward:[x,y,z]}."""
    blob = bytearray()
    views: list[tuple[int, int, object]] = []

    def add(data: bytes, target) -> int:
        _pad(blob)
        off = len(blob)
        blob.extend(data)
        views.append((off, len(data), target))
        return len(views) - 1

    def add_png(img: Image.Image) -> int:
        buf = io.BytesIO()
        img.save(buf, format="PNG", compress_level=9)
        return add(buf.getvalue(), None)

    # --- materials: one per distinct kind, textures baked once and shared ---
    kinds = list(dict.fromkeys(kind for _, kind in placed))
    materials: list[pygltflib.Material] = []
    textures: list[pygltflib.Texture] = []
    images: list[pygltflib.Image] = []
    kind_to_mat: dict[str, int] = {}
    for kind in kinds:
        albedo, normal, orm = bake.bake_kind(kind, seed, tex_size)
        i_alb, i_nrm, i_orm = (len(images) + k for k in range(3))
        images.extend([
            pygltflib.Image(bufferView=add_png(albedo), mimeType="image/png"),
            pygltflib.Image(bufferView=add_png(normal), mimeType="image/png"),
            pygltflib.Image(bufferView=add_png(orm), mimeType="image/png"),
        ])
        t_alb, t_nrm, t_orm = (len(textures) + k for k in range(3))
        textures.extend([pygltflib.Texture(source=i_alb, sampler=0),
                         pygltflib.Texture(source=i_nrm, sampler=0),
                         pygltflib.Texture(source=i_orm, sampler=0)])
        kind_to_mat[kind] = len(materials)
        materials.append(pygltflib.Material(
            name=f"{name}_{kind}",
            pbrMetallicRoughness=pygltflib.PbrMetallicRoughness(
                baseColorFactor=[1.0, 1.0, 1.0, 1.0],
                baseColorTexture=pygltflib.TextureInfo(index=t_alb),
                metallicFactor=1.0, roughnessFactor=1.0,
                metallicRoughnessTexture=pygltflib.TextureInfo(index=t_orm),
            ),
            normalTexture=pygltflib.NormalMaterialTexture(index=t_nrm),
            occlusionTexture=pygltflib.OcclusionTextureInfo(index=t_orm),
        ))

    # --- one primitive per part ---
    accessors: list[pygltflib.Accessor] = []
    primitives: list[pygltflib.Primitive] = []
    for mesh, kind in placed:
        pos = mesh["pos"].astype(np.float32)
        nrm = mesh["nrm"].astype(np.float32)
        uv = mesh["uv"].astype(np.float32)
        tan = _tangents(pos, nrm, uv, mesh["faces"]).astype(np.float32)
        idx = mesh["faces"].reshape(-1).astype(np.uint32)

        a_pos = _acc(accessors, add(pos.tobytes(), ARRAY_BUFFER), FLOAT, len(pos), "VEC3",
                     mn=pos.min(0).tolist(), mx=pos.max(0).tolist())
        a_nrm = _acc(accessors, add(nrm.tobytes(), ARRAY_BUFFER), FLOAT, len(nrm), "VEC3")
        a_uv = _acc(accessors, add(uv.tobytes(), ARRAY_BUFFER), FLOAT, len(uv), "VEC2")
        a_tan = _acc(accessors, add(tan.tobytes(), ARRAY_BUFFER), FLOAT, len(tan), "VEC4")
        a_idx = _acc(accessors, add(idx.tobytes(), ELEMENT_ARRAY_BUFFER), UINT, len(idx), "SCALAR")
        primitives.append(pygltflib.Primitive(
            attributes=pygltflib.Attributes(POSITION=a_pos, NORMAL=a_nrm, TEXCOORD_0=a_uv, TANGENT=a_tan),
            indices=a_idx, material=kind_to_mat[kind]))

    # --- nodes: ship root (mesh) + a child empty per hardpoint ---
    nodes = [pygltflib.Node(name=name, mesh=0)]
    child_indices: list[int] = []
    for hp in hardpoints:
        kind = hp["kind"]; index = int(hp.get("index", 0))
        offset = [float(v) for v in hp.get("offset", (0, 0, 0))]
        forward = [float(v) for v in hp.get("forward", (0, 0, 1))]
        nodes.append(pygltflib.Node(
            name=f"HP_{kind}_{index}",
            translation=offset,
            rotation=_quat_facing_z(forward)))
        child_indices.append(len(nodes) - 1)
    nodes[0].children = child_indices

    gltf = pygltflib.GLTF2(
        scene=0,
        scenes=[pygltflib.Scene(nodes=[0])],
        nodes=nodes,
        meshes=[pygltflib.Mesh(name=name, primitives=primitives)],
        materials=materials,
        textures=textures,
        images=images,
        samplers=[pygltflib.Sampler(wrapS=pygltflib.REPEAT, wrapT=pygltflib.REPEAT)],
        accessors=accessors,
        bufferViews=[pygltflib.BufferView(buffer=0, byteOffset=o, byteLength=l, target=t)
                     for (o, l, t) in views],
        buffers=[pygltflib.Buffer(byteLength=len(blob))],
    )
    gltf.set_binary_blob(bytes(blob))
    return b"".join(gltf.save_to_bytes())


# ---------------------------------------------------------------------------

def _acc(accessors, view, ctype, count, atype, mn=None, mx=None) -> int:
    accessors.append(pygltflib.Accessor(
        bufferView=view, componentType=ctype, count=count, type=atype, min=mn, max=mx))
    return len(accessors) - 1


def _tangents(pos, nrm, uv, faces) -> np.ndarray:
    """Per-vertex glTF TANGENT (vec4 xyz + handedness w) derived from the UV gradient."""
    n = len(pos)
    tan_acc = np.zeros((n, 3), np.float64)
    bit_acc = np.zeros((n, 3), np.float64)
    i0, i1, i2 = faces[:, 0], faces[:, 1], faces[:, 2]
    e1 = pos[i1] - pos[i0]; e2 = pos[i2] - pos[i0]
    d1 = uv[i1] - uv[i0]; d2 = uv[i2] - uv[i0]
    denom = d1[:, 0] * d2[:, 1] - d2[:, 0] * d1[:, 1]
    r = np.where(np.abs(denom) > 1e-12, 1.0 / np.where(denom == 0, 1, denom), 0.0)[:, None]
    t = (e1 * d2[:, 1:2] - e2 * d1[:, 1:2]) * r
    b = (e2 * d1[:, 0:1] - e1 * d2[:, 0:1]) * r
    for i in (i0, i1, i2):
        np.add.at(tan_acc, i, t)
        np.add.at(bit_acc, i, b)

    N = nrm.astype(np.float64)
    # Gram-Schmidt orthogonalize T against N; fall back to an arbitrary perpendicular.
    T = tan_acc - N * np.sum(N * tan_acc, axis=1, keepdims=True)
    tl = np.linalg.norm(T, axis=1)
    bad = tl < 1e-8
    if bad.any():
        alt = np.tile(np.array([1.0, 0.0, 0.0]), (n, 1))
        alt[np.abs(N[:, 0]) > 0.9] = np.array([0.0, 1.0, 0.0])
        T[bad] = (alt - N * np.sum(N * alt, axis=1, keepdims=True))[bad]
        tl = np.linalg.norm(T, axis=1)
    T /= tl[:, None].clip(1e-9)
    w = np.sign(np.sum(np.cross(N, T) * bit_acc, axis=1))
    w[w == 0] = 1.0
    return np.concatenate([T, w[:, None]], axis=1)


def _quat_facing_z(forward) -> list[float]:
    """Quaternion [x,y,z,w] for a basis whose local +Z aligns with ``forward`` (mirrors
    ShipModelLoader.BasisFacingZ)."""
    f = np.asarray(forward, np.float64)
    fl = np.linalg.norm(f)
    if fl < 1e-8:
        return [0.0, 0.0, 0.0, 1.0]
    z = f / fl
    up = np.array([1.0, 0.0, 0.0]) if abs(z[1]) > 0.999 else np.array([0.0, 1.0, 0.0])
    x = np.cross(up, z); x /= np.linalg.norm(x).clip(1e-9)
    y = np.cross(z, x)
    return _mat_to_quat(np.column_stack([x, y, z]))


def _mat_to_quat(R) -> list[float]:
    tr = R[0, 0] + R[1, 1] + R[2, 2]
    if tr > 0:
        s = np.sqrt(tr + 1.0) * 2
        w = 0.25 * s
        x = (R[2, 1] - R[1, 2]) / s
        y = (R[0, 2] - R[2, 0]) / s
        z = (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = np.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2
        w = (R[2, 1] - R[1, 2]) / s; x = 0.25 * s
        y = (R[0, 1] + R[1, 0]) / s; z = (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = np.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2
        w = (R[0, 2] - R[2, 0]) / s; x = (R[0, 1] + R[1, 0]) / s
        y = 0.25 * s; z = (R[1, 2] + R[2, 1]) / s
    else:
        s = np.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2
        w = (R[1, 0] - R[0, 1]) / s; x = (R[0, 2] + R[2, 0]) / s
        y = (R[1, 2] + R[2, 1]) / s; z = 0.25 * s
    q = np.array([x, y, z, w], np.float64)
    q /= np.linalg.norm(q).clip(1e-9)
    return q.tolist()


def _pad(buf: bytearray, align: int = 4) -> None:
    while len(buf) % align != 0:
        buf.append(0)
