#!/usr/bin/env python3
"""base-col — bake authored convex COLLISION-PROXY parts into base.glb.

The station art (`client/assets/bases/base.glb`) is ONE welded, concave visual mesh. Both the
server sim and the Godot client currently build a single QuickHull "shrink-wrap" over that whole
cloud, so ships and bolts collide with an invisible convex balloon rather than the visible
superstructure. Phase B replaces that with a COMPOUND hull: one convex hull per authored part.

This tool authors those parts. It reads `base-col.yaml` (the hand-authored spec — a handful of
`box` / `cylinder` / `points` primitives that hug the visible masses while leaving the docking
corridors open) and APPENDS one small triangulated convex mesh node per part, named `COL_<Name>`,
into the GLB. The visual mesh, its material, and every `HP_` empty are left untouched.

Until the shared/server compound-hull code (packages B2/B3) lands, the OLD single-hull collision
code reads this NEW glb. So the bake is only safe if it is metric-neutral. Two hard, load-bearing
validations enforce that (the bake FAILS loudly otherwise):

  1. HULL CONTAINMENT — every COL vertex lies strictly inside the convex hull of the visual mesh
     (signed distance <= -margin to every hull face). Because the max of any linear functional
     (and of |p|) over a convex set is attained at a vertex, a strictly-interior point can never
     be a directional extreme, never enlarge the AABB, and never enlarge the bounding radius.
     Hence ConvexHull.Build's ReduceToExtremes(256) still selects only visual vertices — the
     merged hull, its LongestAxis, and its BoundingRadius are bit-unchanged. This is what makes
     the bake invisible to the pre-B2 collision code.
  2. DOCK CORRIDOR — every docking-entrance disc centre, and a swept segment from it toward the
     bay-door centre and outward along its approach, lies OUTSIDE all COL parts, so no part ever
     caps a corridor a ship must fly through.

A weaker AABB-containment check is also asserted (the explicit MeshAabb scale contract the client
relies on), and the output is written deterministically (fixed ordering, cleaned float32) so a
re-bake of unchanged input yields a byte-identical GLB (identical SHA).

Usage (via uv — deps in pyproject.toml):
  uv run bake.py                       # bake in place: client/assets/bases/base.glb
  uv run bake.py --check               # validate only, do not write
  uv run bake.py --suggest             # print candidate boxes clustered from the mesh (seed only)
  uv run bake.py --preview-dir DIR     # also render reviewer PNGs (default: ./preview)
  uv run bake.py --glb PATH --yaml PATH --out PATH
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

import numpy as np
import pygltflib
import yaml
from scipy.spatial import ConvexHull

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DEF_GLB = REPO / "client" / "assets" / "bases" / "base.glb"
DEF_YAML = HERE / "base-col.yaml"

FLOAT = pygltflib.FLOAT
UINT = pygltflib.UNSIGNED_INT
ARRAY_BUFFER = pygltflib.ARRAY_BUFFER
ELEMENT_ARRAY_BUFFER = pygltflib.ELEMENT_ARRAY_BUFFER

COL_PREFIX = "COL_"

# World-space collision constants — MIRROR shared/Collision/CollisionConfig.cs. The auto generator
# reasons in AUTHORED mesh units, then converts these via the SAME world-scale the server/client
# derive at load: ws = WORLD_BASE_RADIUS*2 / mesh.LongestAxis (server/Sim/World.cs LoadBase). A ship
# is WORLD_SHIP_RADIUS in world units, so only WORLD_SHIP_RADIUS/ws authored units — size the
# voxel/gap/corridor thresholds off that, never the raw world number. Keep these in sync with
# CollisionConfig or the generated coverage will be sized wrong.
WORLD_BASE_RADIUS = 90.0      # CollisionConfig.BaseRadius
WORLD_SHIP_RADIUS = 3.0       # CollisionConfig.ShipRadius
WORLD_DOCK_DISC_RADIUS = 9.0  # CollisionConfig.DockDiscRadius


# ---------------------------------------------------------------------------
#  glTF reading helpers (node transforms + POSITION vertices)
# ---------------------------------------------------------------------------

def _local_matrix(node) -> np.ndarray:
    """4x4 world-from-local for a glTF node (matrix, or TRS)."""
    if node.matrix:
        # glTF matrix is column-major, 16 floats.
        return np.array(node.matrix, dtype=np.float64).reshape(4, 4, order="F")
    t = np.array(node.translation or [0, 0, 0], dtype=np.float64)
    s = np.array(node.scale or [1, 1, 1], dtype=np.float64)
    x, y, z, w = node.rotation or [0, 0, 0, 1]
    r = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
        [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
        [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)],
    ], dtype=np.float64)
    m = np.eye(4)
    m[:3, :3] = r * s  # scale columns
    m[:3, 3] = t
    return m


def _parent_map(gltf) -> dict[int, int]:
    parent = {}
    for i, n in enumerate(gltf.nodes):
        for c in (n.children or []):
            parent[c] = i
    return parent


def _world_matrix(gltf, parent, i) -> np.ndarray:
    chain = []
    cur = i
    while cur is not None:
        chain.append(cur)
        cur = parent.get(cur)
    m = np.eye(4)
    for c in reversed(chain):
        m = m @ _local_matrix(gltf.nodes[c])
    return m


def _accessor_array(gltf, blob, acc_idx) -> np.ndarray:
    acc = gltf.accessors[acc_idx]
    bv = gltf.bufferViews[acc.bufferView]
    off = (bv.byteOffset or 0) + (acc.byteOffset or 0)
    ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[acc.type]
    dt = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}[acc.componentType]
    arr = np.frombuffer(blob, dtype=dt, count=acc.count * ncomp, offset=off)
    return arr.reshape(acc.count, ncomp) if ncomp > 1 else arr


def read_visual_vertices(gltf, blob) -> tuple[np.ndarray, list]:
    """Return (Nx3 world-space POSITION cloud of every NON-COL mesh, HP records).

    Mirrors shared/Collision/GlbReader.Walk + client GlbLoader.MeshAabb: every non-COL mesh
    primitive's POSITION, transformed by its node world matrix. HP records = (name, pos, fwd).
    """
    parent = _parent_map(gltf)
    verts = []
    hps = []
    for i, n in enumerate(gltf.nodes):
        name = n.name or ""
        if name.startswith(COL_PREFIX):
            continue
        w = _world_matrix(gltf, parent, i)
        if name.startswith("HP_"):
            pos = (w @ np.array([0, 0, 0, 1.0]))[:3]
            fwd = w[:3, :3] @ np.array([0, 0, 1.0])
            fwd = fwd / (np.linalg.norm(fwd) or 1.0)
            hps.append((name, pos, fwd))
        if n.mesh is None:
            continue
        for prim in gltf.meshes[n.mesh].primitives:
            if prim.attributes.POSITION is None:
                continue
            p = _accessor_array(gltf, blob, prim.attributes.POSITION).astype(np.float64)
            ph = np.c_[p, np.ones(len(p))]
            verts.append((ph @ w.T)[:, :3])
    return (np.vstack(verts) if verts else np.zeros((0, 3))), hps


# ---------------------------------------------------------------------------
#  Convex-part construction (box / cylinder / points -> triangulated hull)
# ---------------------------------------------------------------------------

def _euler_deg_to_matrix(rot) -> np.ndarray:
    rx, ry, rz = np.radians(rot or [0, 0, 0])
    cx, sx = np.cos(rx), np.sin(rx)
    cy, sy = np.cos(ry), np.sin(ry)
    cz, sz = np.cos(rz), np.sin(rz)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    Rz = np.array([[cz, -sz, cz * 0 + 0], [sz, cz, 0], [0, 0, 1]])
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return Rz @ Ry @ Rx


def _box_verts(spec) -> np.ndarray:
    c = np.array(spec["center"], dtype=np.float64)
    h = np.array(spec["size"], dtype=np.float64) * 0.5
    R = _euler_deg_to_matrix(spec.get("rot"))
    corners = np.array([[sx, sy, sz] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)],
                       dtype=np.float64) * h
    return (corners @ R.T) + c


def _cylinder_verts(spec) -> np.ndarray:
    c = np.array(spec["center"], dtype=np.float64)
    axis = np.array(spec["axis"], dtype=np.float64)
    axis = axis / (np.linalg.norm(axis) or 1.0)
    r = float(spec["radius"])
    half = float(spec["height"]) * 0.5
    seg = int(spec.get("segments", 12))
    # Two vectors spanning the plane perpendicular to axis.
    ref = np.array([1.0, 0, 0]) if abs(axis[0]) < 0.9 else np.array([0, 1.0, 0])
    u = np.cross(axis, ref); u /= (np.linalg.norm(u) or 1.0)
    v = np.cross(axis, u)
    ang = np.linspace(0, 2 * np.pi, seg, endpoint=False)
    ring = np.outer(np.cos(ang), u) * r + np.outer(np.sin(ang), v) * r
    top = c + axis * half + ring
    bot = c - axis * half + ring
    return np.vstack([top, bot])


def part_vertices(part) -> np.ndarray:
    if "box" in part:
        return _box_verts(part["box"])
    if "cylinder" in part:
        return _cylinder_verts(part["cylinder"])
    if "points" in part:
        return np.array(part["points"], dtype=np.float64)
    raise ValueError(f"part {part.get('name')!r} has no box/cylinder/points")


def convex_mesh(verts: np.ndarray) -> tuple[np.ndarray, np.ndarray, ConvexHull]:
    """Return (hull_verts float32, triangle_faces uint32, scipy hull) with OUTWARD winding."""
    hull = ConvexHull(verts)
    used = np.unique(hull.simplices)
    remap = {old: new for new, old in enumerate(used)}
    hv = verts[used]
    centroid = hv.mean(axis=0)
    faces = []
    for simp in hull.simplices:
        a, b, c = (remap[i] for i in simp)
        n = np.cross(hv[b] - hv[a], hv[c] - hv[a])
        # Flip so the normal points away from the hull centroid (outward).
        if np.dot(n, hv[a] - centroid) < 0:
            b, c = c, b
        faces.append([a, b, c])
    return hv.astype(np.float32), np.array(faces, dtype=np.uint32), hull


# ---------------------------------------------------------------------------
#  Validations
# ---------------------------------------------------------------------------

def hull_equations(verts: np.ndarray) -> np.ndarray:
    """scipy hull face inequalities [n | d]: point x is inside iff n.x + d <= 0 for all rows."""
    return ConvexHull(verts).equations


def signed_dist_to_hull(pts: np.ndarray, eqs: np.ndarray) -> np.ndarray:
    """Per-point max_i (n_i.x + d_i). <=0 means inside; the value is signed distance to surface."""
    return (pts @ eqs[:, :3].T + eqs[:, 3]).max(axis=1)


def point_inside_part(pt: np.ndarray, eqs: np.ndarray, tol: float) -> bool:
    return bool((pt @ eqs[:, :3].T + eqs[:, 3]).max() < -tol)


# ---------------------------------------------------------------------------
#  Deterministic GLB assembly (strip prior COL, append fresh)
# ---------------------------------------------------------------------------

def _pad4(buf: bytearray):
    while len(buf) % 4 != 0:
        buf.append(0)


def strip_col(gltf, blob: bytes) -> bytes:
    """Remove any previously-baked COL_ nodes/meshes/accessors/bufferViews (always appended at
    the tail, so removal is a truncation) and return the truncated blob. Idempotency guard so a
    re-bake starts from the pristine visual asset and stays byte-stable."""
    col_nodes = [i for i, n in enumerate(gltf.nodes) if (n.name or "").startswith(COL_PREFIX)]
    if not col_nodes:
        return blob
    col_meshes = sorted({gltf.nodes[i].mesh for i in col_nodes if gltf.nodes[i].mesh is not None})
    col_acc = sorted({p.attributes.POSITION for m in col_meshes for p in gltf.meshes[m].primitives} |
                     {p.indices for m in col_meshes for p in gltf.meshes[m].primitives})
    col_bv = sorted({gltf.accessors[a].bufferView for a in col_acc})
    # COL data is contiguous at the tail of the buffer; truncate there.
    cut = min(gltf.bufferViews[b].byteOffset for b in col_bv)
    # Drop trailing COL entries (no surviving item references them, so no re-indexing needed).
    keep_nodes = set(range(len(gltf.nodes))) - set(col_nodes)
    for sc in gltf.scenes:
        sc.nodes = [n for n in sc.nodes if n in keep_nodes]
    gltf.nodes = [n for i, n in enumerate(gltf.nodes) if i in keep_nodes]
    gltf.meshes = [m for i, m in enumerate(gltf.meshes) if i not in set(col_meshes)]
    gltf.accessors = [a for i, a in enumerate(gltf.accessors) if i not in set(col_acc)]
    gltf.bufferViews = [b for i, b in enumerate(gltf.bufferViews) if i not in set(col_bv)]
    # Drop the shared COL material if present.
    gltf.materials = [m for m in gltf.materials if (m.name or "") != "COL_proxy"]
    for msh in gltf.meshes:
        for p in msh.primitives:
            if p.material is not None and p.material >= len(gltf.materials):
                p.material = None
    return blob[:cut]


def bake_glb(gltf, blob: bytes, parts: list[tuple[str, np.ndarray, np.ndarray]]) -> bytes:
    """Append one COL_<name> node+mesh per part. `parts` = [(name, hullverts, faces)] sorted."""
    buf = bytearray(strip_col(gltf, blob))

    # One minimal shared material so importers that require it are happy; never rendered.
    mat_idx = len(gltf.materials)
    gltf.materials.append(pygltflib.Material(
        name="COL_proxy",
        pbrMetallicRoughness=pygltflib.PbrMetallicRoughness(
            baseColorFactor=[1.0, 0.0, 1.0, 1.0], metallicFactor=0.0, roughnessFactor=1.0),
    ))

    for name, hv, faces in parts:
        idx = faces.reshape(-1).astype(np.uint32)
        _pad4(buf)
        pos_off = len(buf)
        buf.extend(hv.astype("<f4").tobytes())
        pos_len = len(buf) - pos_off
        _pad4(buf)
        idx_off = len(buf)
        buf.extend(idx.astype("<u4").tobytes())
        idx_len = len(buf) - idx_off

        bv_pos = len(gltf.bufferViews)
        gltf.bufferViews.append(pygltflib.BufferView(
            buffer=0, byteOffset=pos_off, byteLength=pos_len, target=ARRAY_BUFFER))
        bv_idx = bv_pos + 1
        gltf.bufferViews.append(pygltflib.BufferView(
            buffer=0, byteOffset=idx_off, byteLength=idx_len, target=ELEMENT_ARRAY_BUFFER))

        acc_pos = len(gltf.accessors)
        gltf.accessors.append(pygltflib.Accessor(
            bufferView=bv_pos, componentType=FLOAT, count=len(hv), type="VEC3",
            min=[float(x) for x in hv.min(axis=0)], max=[float(x) for x in hv.max(axis=0)]))
        acc_idx = acc_pos + 1
        gltf.accessors.append(pygltflib.Accessor(
            bufferView=bv_idx, componentType=UINT, count=len(idx), type="SCALAR"))

        mesh_idx = len(gltf.meshes)
        gltf.meshes.append(pygltflib.Mesh(
            name=COL_PREFIX + name,
            primitives=[pygltflib.Primitive(
                attributes=pygltflib.Attributes(POSITION=acc_pos), indices=acc_idx,
                material=mat_idx, mode=4)]))
        node_idx = len(gltf.nodes)
        gltf.nodes.append(pygltflib.Node(name=COL_PREFIX + name, mesh=mesh_idx))
        gltf.scenes[gltf.scene or 0].nodes.append(node_idx)

    gltf.buffers[0].byteLength = len(buf)
    gltf.set_binary_blob(bytes(buf))
    return b"".join(gltf.save_to_bytes())


# ---------------------------------------------------------------------------
#  --suggest: rough region boxes to seed the YAML (never baked directly)
# ---------------------------------------------------------------------------

def suggest(verts: np.ndarray, eqs: np.ndarray, k: int = 7):
    """Lightweight k-means over the cloud; per cluster print a hull-safe AABB. Seed only."""
    rng = np.random.default_rng(0)
    cen = verts[rng.choice(len(verts), k, replace=False)]
    for _ in range(40):
        lab = np.argmin(((verts[:, None, :] - cen[None]) ** 2).sum(-1), axis=1)
        new = np.array([verts[lab == j].mean(0) if (lab == j).any() else cen[j] for j in range(k)])
        if np.allclose(new, cen):
            break
        cen = new
    print("# --suggest candidate boxes (refine by hand; shrunk to stay inside the visual hull):")
    print("parts:")
    for j in range(k):
        cl = verts[lab == j]
        if len(cl) < 8:
            continue
        lo, hi = cl.min(0), cl.max(0)
        c = (lo + hi) / 2
        size = (hi - lo)
        # Shrink toward centre until all 8 corners clear the hull by 0.05.
        for _ in range(60):
            box = np.array([[sx, sy, sz] for sx in (-1, 1) for sy in (-1, 1)
                            for sz in (-1, 1)]) * (size / 2) + c
            if signed_dist_to_hull(box, eqs).max() <= -0.05:
                break
            size *= 0.94
        print(f"  - name: Region{j}")
        print(f"    box: {{center: [{c[0]:.2f}, {c[1]:.2f}, {c[2]:.2f}], "
              f"size: [{size[0]:.2f}, {size[1]:.2f}, {size[2]:.2f}]}}")


# ---------------------------------------------------------------------------
#  Reviewer preview render
# ---------------------------------------------------------------------------

def render_preview(verts, hps, parts, out_dir: Path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Line3DCollection

    out_dir.mkdir(parents=True, exist_ok=True)
    colors = plt.cm.tab10(np.linspace(0, 1, max(10, len(parts))))
    ent = np.array([p for n, p, f in hps if "Entrance" in n or "Exit" in n])

    def edges_of(hv, faces):
        segs = set()
        for a, b, c in faces:
            for u, v in ((a, b), (b, c), (c, a)):
                segs.add((min(u, v), max(u, v)))
        return [[hv[u], hv[v]] for u, v in segs]

    # Orthographic triptych.
    fig, axs = plt.subplots(1, 3, figsize=(21, 7))
    planes = [(2, 1, "Z", "Y", "side"), (0, 1, "X", "Y", "front"), (2, 0, "Z", "X", "top")]
    for ax, (a, b, la, lb, title) in zip(axs, planes):
        ax.scatter(verts[:, a], verts[:, b], s=1, alpha=0.12, c="0.5", linewidths=0)
        for k, (name, hv, faces) in enumerate(parts):
            for seg in edges_of(hv, faces):
                s = np.array(seg)
                ax.plot(s[:, a], s[:, b], c=colors[k], lw=1.0, alpha=0.9)
            ax.text(hv[:, a].mean(), hv[:, b].mean(), name, color=colors[k], fontsize=7, ha="center")
        if len(ent):
            ax.plot(ent[:, a], ent[:, b], "r*", ms=13, label="dock HP")
        ax.set_xlabel(la); ax.set_ylabel(lb); ax.set_title(title); ax.set_aspect("equal")
    axs[0].legend(loc="upper right", fontsize=8)
    fig.suptitle("base.glb visual cloud (grey) + authored COL_ parts", fontsize=13)
    fig.tight_layout()
    p1 = out_dir / "base-col-ortho.png"
    fig.savefig(p1, dpi=95); plt.close(fig)

    # 3D view.
    fig = plt.figure(figsize=(11, 10))
    ax = fig.add_subplot(111, projection="3d")
    samp = verts[::7]
    ax.scatter(samp[:, 0], samp[:, 1], samp[:, 2], s=1, alpha=0.08, c="0.5", linewidths=0)
    for k, (name, hv, faces) in enumerate(parts):
        ax.add_collection3d(Line3DCollection(edges_of(hv, faces), colors=[colors[k]], lw=0.9))
    if len(ent):
        ax.scatter(ent[:, 0], ent[:, 1], ent[:, 2], c="r", marker="*", s=90)
    ax.set_xlabel("X"); ax.set_ylabel("Y"); ax.set_zlabel("Z")
    span = np.ptp(verts, axis=0)
    ax.set_box_aspect((span[0], span[1], span[2]))
    ax.view_init(elev=18, azim=-60)
    ax.set_title("COL_ parts vs visual cloud (3D)")
    p2 = out_dir / "base-col-3d.png"
    fig.savefig(p2, dpi=95); plt.close(fig)
    return [p1, p2]


# ---------------------------------------------------------------------------
#  --auto: deterministic voxel solid-fill + greedy box-merge collision generation
# ---------------------------------------------------------------------------
#
# The hand-authored star-of-boxes leaves gaps a ship (radius WORLD_SHIP_RADIUS ≈ 0.54 AUTHORED
# units) can fly THROUGH into the hollow station interior. `--auto` drives coverage off the actual
# mesh volume instead of hand-eye box placement:
#   1. Voxelize the visual TRIANGLES (robust for the concave, non-watertight shell) at `voxel_res`.
#   2. Flood-fill the EXTERIOR from the grid boundary through free space; every free cell it cannot
#      reach is sealed interior → mark solid. This fills the hollow the player flies around inside.
#      (No outward inflation: a radius-0 boundary flood reaches every exterior cell up to the
#      surface, so only genuinely-enclosed cells are filled.)
#   3. Carve swept-cylinder DOCK CORRIDORS back open so docking/launch approaches stay clear.
#   4. Greedy-merge the solid voxels into maximal axis-aligned boxes (deterministic scan order).
#   5. Clamp every box strictly inside the visual convex hull (metric-neutrality contract).
#   6. Reachability ASSERT: no ship-radius exterior path may reach a sealed-interior cell through
#      the FINAL box set (except inside a corridor) — the regression guard for this very bug.

_STRUCT_ONE = None  # lazily-built 6-connectivity structuring element (label default)


class VoxelGrid:
    """An axis-aligned voxel grid: world-authored `origin` (min corner of cell [0,0,0]), cubic
    `res`, integer `dims`. Cell (i,j,k) centre = origin + (ijk + 0.5)*res."""

    __slots__ = ("origin", "res", "dims")

    def __init__(self, origin, res, dims):
        self.origin = np.asarray(origin, dtype=np.float64)
        self.res = float(res)
        self.dims = tuple(int(d) for d in dims)

    def centers_flat(self) -> np.ndarray:
        ii = np.arange(self.dims[0])
        jj = np.arange(self.dims[1])
        kk = np.arange(self.dims[2])
        gx, gy, gz = np.meshgrid(ii, jj, kk, indexing="ij")
        idx = np.stack([gx, gy, gz], axis=-1).reshape(-1, 3)
        return self.origin + (idx + 0.5) * self.res


def grid_for(V: np.ndarray, res: float, pad_cells: int = 3) -> VoxelGrid:
    """A grid covering the mesh AABB plus `pad_cells` of empty margin on every side, so the grid
    boundary is guaranteed exterior (the exterior flood always has a seed)."""
    lo = V.min(0) - res * pad_cells
    hi = V.max(0) + res * pad_cells
    dims = np.ceil((hi - lo) / res).astype(int) + 1
    return VoxelGrid(lo, res, dims)


def read_visual_triangles(gltf, blob) -> tuple[np.ndarray, np.ndarray]:
    """(V Nx3 world float64, F Mx3 int) — every NON-COL indexed mesh primitive's triangles in the
    same world space as read_visual_vertices. Keeps the index buffer so the voxelizer rasterizes
    real triangle SURFACES (robust for a concave, non-watertight shell), not just the vert cloud."""
    parent = _parent_map(gltf)
    Vs, Fs, off = [], [], 0
    for i, n in enumerate(gltf.nodes):
        name = n.name or ""
        if name.startswith(COL_PREFIX) or n.mesh is None:
            continue
        w = _world_matrix(gltf, parent, i)
        for prim in gltf.meshes[n.mesh].primitives:
            if prim.attributes.POSITION is None or prim.indices is None:
                continue
            p = _accessor_array(gltf, blob, prim.attributes.POSITION).astype(np.float64)
            pw = (np.c_[p, np.ones(len(p))] @ w.T)[:, :3]
            idx = _accessor_array(gltf, blob, prim.indices).astype(np.int64).reshape(-1, 3)
            Vs.append(pw)
            Fs.append(idx + off)
            off += len(pw)
    if not Vs:
        return np.zeros((0, 3)), np.zeros((0, 3), dtype=int)
    return np.vstack(Vs), np.vstack(Fs)


def world_scale(verts: np.ndarray) -> float:
    """ws = WORLD_BASE_RADIUS*2 / LongestAxis — identical to World.LoadBase's derivation."""
    longest = float((verts.max(0) - verts.min(0)).max())
    return WORLD_BASE_RADIUS * 2.0 / max(1e-3, longest)


def voxelize_surface(V: np.ndarray, F: np.ndarray, grid: VoxelGrid) -> np.ndarray:
    """Mark every grid cell any triangle passes through. Each triangle is barycentric-sampled at a
    spacing <= res/2 so no crossed cell is skipped — deterministic and robust to the mesh being
    non-watertight (we never rely on a solid fill from the mesh, only on the flood in classify)."""
    S = np.zeros(grid.dims, dtype=bool)
    inv = 1.0 / grid.res
    dmax = np.array(grid.dims) - 1
    for tri in F:
        p0, p1, p2 = V[tri]
        emax = max(np.linalg.norm(p1 - p0), np.linalg.norm(p2 - p1), np.linalg.norm(p0 - p2))
        nn = max(1, int(np.ceil(emax * 2.0 * inv)))  # spacing emax/nn <= res/2
        ii = np.arange(nn + 1)
        us, vs = np.meshgrid(ii, ii)
        m = (us + vs) <= nn
        u = us[m].astype(np.float64) / nn
        v = vs[m].astype(np.float64) / nn
        pts = p0 + np.outer(u, (p1 - p0)) + np.outer(v, (p2 - p0))
        gi = np.floor((pts - grid.origin) * inv).astype(int)
        np.clip(gi, 0, dmax, out=gi)
        S[gi[:, 0], gi[:, 1], gi[:, 2]] = True
    return S


def _boundary_labels(lab: np.ndarray) -> set:
    faces = [lab[0], lab[-1], lab[:, 0], lab[:, -1], lab[:, :, 0], lab[:, :, -1]]
    s = set(int(x) for x in np.unique(np.concatenate([f.ravel() for f in faces])))
    s.discard(0)
    return s


def classify_solid(S: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """solid = surface ∪ sealed-interior. sealed-interior = free cells the EXTERIOR (flood from the
    grid boundary through free space) cannot reach — the hollow the player currently flies inside.
    Returns (solid, sealed, exterior)."""
    from scipy.ndimage import label

    free = ~S
    lab, _ = label(free)
    ext = np.isin(lab, list(_boundary_labels(lab)))
    sealed = free & ~ext
    return (S | sealed), sealed, ext


def downsample_solid(fine: VoxelGrid, fine_solid: np.ndarray, box: VoxelGrid) -> np.ndarray:
    """A box-grid cell is solid if ANY fine solid cell centre falls in it. The box solid therefore
    ENGULFS the fine solid (≈100% coverage of the true volume); the small outward slop is trimmed
    back by the strict hull-containment clamp. This makes the coarse decomposition robust even if
    the coarse shell alone would not be voxel-watertight."""
    xs, ys, zs = np.where(fine_solid)
    centers = fine.origin + (np.stack([xs, ys, zs], axis=1) + 0.5) * fine.res
    bi = np.floor((centers - box.origin) / box.res).astype(int)
    np.clip(bi, 0, np.array(box.dims) - 1, out=bi)
    B = np.zeros(box.dims, dtype=bool)
    B[bi[:, 0], bi[:, 1], bi[:, 2]] = True
    return B


def corridor_segments(hps, corridor_r: float, approach: float):
    """Swept-cylinder DOCK CORRIDORS that must stay open (never solid). Reuses World.LoadBase's
    geometry: the bay-door centre is the mean of the HP_DockingEntrance positions; each entrance's
    approach axis is radial-outward (normalize(pos)) — matching the disc normals the sim carves —
    and the HP_DockingExit catapults ships radially outward along normalize(exitPos). We sweep from
    each entrance (extended `approach` units outside) to the door centre, and from the door out
    along the exit axis. Returns [(a, b, radius)]."""
    ent = [np.asarray(p, float) for n, p, f in hps if "Entrance" in n]
    ext = [np.asarray(p, float) for n, p, f in hps if "Exit" in n]
    if not ent:
        return []
    door = np.mean(ent, axis=0)

    def radial(p):
        l = float(np.linalg.norm(p))
        return p / l if l > 1e-6 else np.array([0.0, 0.0, 1.0])

    segs = []
    for e in ent:
        segs.append((e + radial(e) * approach, door, corridor_r))
    for x in ext:
        segs.append((door, x + radial(x) * approach, corridor_r))
    return segs


def corridor_mask(grid: VoxelGrid, segs) -> np.ndarray:
    """Cells within `radius` of any corridor segment (a swept cylinder / capsule)."""
    if not segs:
        return np.zeros(grid.dims, dtype=bool)
    centers = grid.centers_flat()
    inside = np.zeros(len(centers), dtype=bool)
    for a, b, r in segs:
        a = np.asarray(a)
        b = np.asarray(b)
        ab = b - a
        L2 = float(ab @ ab) or 1.0
        t = np.clip((centers - a) @ ab / L2, 0.0, 1.0)
        proj = a + t[:, None] * ab
        d = np.linalg.norm(centers - proj, axis=1)
        inside |= d < r
    return inside.reshape(grid.dims)


def greedy_boxes(solid: np.ndarray):
    """Deterministic voxel→cuboid decomposition: scan solid cells in (x,y,z) order; from the first
    still-unclaimed cell grow a maximal box x→y→z, claim it, repeat. Standard greedy merge; the
    fixed scan + growth order makes the box set reproducible. Returns [(i0,j0,k0,i1,j1,k1)]."""
    g = solid.copy()
    nx, ny, nz = g.shape
    boxes = []
    xs, ys, zs = np.where(g)
    order = np.lexsort((zs, ys, xs))
    xs, ys, zs = xs[order], ys[order], zs[order]
    for a in range(len(xs)):
        x, y, z = int(xs[a]), int(ys[a]), int(zs[a])
        if not g[x, y, z]:
            continue
        x1 = x
        while x1 + 1 < nx and g[x1 + 1, y, z]:
            x1 += 1
        y1 = y
        while y1 + 1 < ny and g[x:x1 + 1, y1 + 1, z].all():
            y1 += 1
        z1 = z
        while z1 + 1 < nz and g[x:x1 + 1, y:y1 + 1, z1 + 1].all():
            z1 += 1
        g[x:x1 + 1, y:y1 + 1, z:z1 + 1] = False
        boxes.append((x, y, z, x1, y1, z1))
    return boxes


def box_bounds(grid: VoxelGrid, box) -> tuple[np.ndarray, np.ndarray]:
    x0, y0, z0, x1, y1, z1 = box
    lo = grid.origin + np.array([x0, y0, z0]) * grid.res
    hi = grid.origin + (np.array([x1, y1, z1]) + 1) * grid.res
    return lo, hi


_CORNER_SIGNS = np.array([[sx, sy, sz] for sx in (0, 1) for sy in (0, 1) for sz in (0, 1)])


def clamp_box_to_hull(lo, hi, eqs, margin, res):
    """Shrink an AABB's faces inward (minimally, along the dominant violated-plane axis) until all 8
    corners are >= `margin` strictly inside the visual convex hull — the same interior test the
    per-part containment validation asserts, so the merged hull / LongestAxis / BoundingRadius stay
    bit-unchanged. Returns (lo, hi) or None if the box collapses (report + drop)."""
    lo = np.asarray(lo, float).copy()
    hi = np.asarray(hi, float).copy()
    step = res * 0.1
    for _ in range(2000):
        pts = np.where(_CORNER_SIGNS.astype(bool), hi, hi * 0 + lo)
        vals = pts @ eqs[:, :3].T + eqs[:, 3]  # (8, nfaces)
        corner_max = vals.max(axis=1)
        w = int(np.argmax(corner_max))
        if corner_max[w] <= -margin:
            return lo, hi
        plane = int(np.argmax(vals[w]))
        ax = int(np.argmax(np.abs(eqs[plane, :3])))
        if _CORNER_SIGNS[w, ax] == 1:
            hi[ax] -= step
        else:
            lo[ax] += step
        if (hi - lo).min() <= 1e-3:
            return None
    return None


def box_eqs(center, size) -> np.ndarray:
    """The 6 face inequalities [n|d] of an axis-aligned box (point inside iff n.x+d <= 0 for all),
    same convention as scipy's hull equations — but exact and cheap (no QuickHull rebuild)."""
    c = np.asarray(center, float)
    h = np.asarray(size, float) * 0.5
    return np.array([
        [1, 0, 0, -(c[0] + h[0])], [-1, 0, 0, (c[0] - h[0])],
        [0, 1, 0, -(c[1] + h[1])], [0, -1, 0, (c[1] - h[1])],
        [0, 0, 1, -(c[2] + h[2])], [0, 0, -1, (c[2] - h[2])],
    ], dtype=np.float64)


def rasterize_parts(part_eqs_list, grid: VoxelGrid) -> np.ndarray:
    """Solid grid = cell centre inside ANY part's convex hull (works for boxes and general parts)."""
    centers = grid.centers_flat()
    inside = np.zeros(len(centers), dtype=bool)
    for eqs in part_eqs_list:
        inside |= (centers @ eqs[:, :3].T + eqs[:, 3]).max(axis=1) <= 1e-9
    return inside.reshape(grid.dims)


def reachability_leaks(part_eqs_list, gfine, interior_hollow, corridor_fine, ship_r) -> int:
    """THE REGRESSION GUARD. Rasterize the FINAL parts into the fine grid, then flood the exterior
    with the free space eroded by the ship radius (a ship CENTRE can sit only >= ship_r from solid).
    Any INTERIOR-HOLLOW cell (a sealed cell where a ship actually fits — the fly-around-inside space)
    that this exterior ship-flood reaches, and that is NOT inside a carved dock corridor, is a hole a
    ship can fly through into the station interior. Returns that count; >0 must FAIL the bake. The
    sparse hand-authored layout leaks badly here (by design — it's the bug this guard catches)."""
    from scipy.ndimage import label, distance_transform_edt

    solidF = rasterize_parts(part_eqs_list, gfine)
    dist = distance_transform_edt(~solidF) * gfine.res
    ship_free = dist > ship_r
    lab, _ = label(ship_free)
    ext = np.isin(lab, list(_boundary_labels(lab))) & ship_free
    leaked = interior_hollow & ext & ~corridor_fine
    return int(leaked.sum())


def auto_config(spec: dict, ws: float) -> dict:
    """Resolve the --auto knobs from `auto_config:` in the YAML, filling authored-unit defaults from
    the world collision constants via the world-scale `ws`."""
    ac = spec.get("auto_config") or {}
    ship_r = float(ac.get("ship_radius", WORLD_SHIP_RADIUS / ws))
    clearance = float(ac.get("corridor_clearance", 0.5))
    corridor_r = float(ac.get("corridor_radius", max(WORLD_DOCK_DISC_RADIUS / ws, ship_r + clearance)))
    return dict(
        voxel_res=float(ac.get("voxel_res", 0.5)),
        box_res=float(ac.get("box_res", 1.75)),
        margin=float(spec.get("margin", 0.05)),
        ship_r=ship_r,
        corridor_r=corridor_r,
        corridor_approach=float(ac.get("corridor_approach", 5.0)),
    )


def generate_auto_parts(verts, V, F, hps, eqs, cfg):
    """Run the full pipeline and return (yparts, stats). `yparts` are box specs in the SAME schema
    the authored path consumes: [{'name': 'Auto00', 'box': {'center': [...], 'size': [...]}}]."""
    ship_r = cfg["ship_r"]
    box_res = cfg["box_res"]
    voxel_res = cfg["voxel_res"]
    margin = cfg["margin"]
    segs = corridor_segments(hps, cfg["corridor_r"], cfg["corridor_approach"])

    from scipy.ndimage import label as _label, distance_transform_edt as _edt, binary_dilation as _dil

    # Fine grid: accurate solid + sealed-interior classification (drives coverage + reachability).
    gfine = grid_for(V, voxel_res)
    Sfine = voxelize_surface(V, F, gfine)
    solid_fine, sealed_fine, _ = classify_solid(Sfine)
    corridor_fine = corridor_mask(gfine, segs)
    # The "fly-around-inside" hollow = sealed cells where a ship of radius ship_r actually FITS (the
    # space the player currently flies through into). The reachability guard protects exactly this —
    # not the paper-thin near-surface sealed band any finite-box approximation slightly penetrates.
    interior_hollow = sealed_fine & ((_edt(~Sfine) * gfine.res) > ship_r)

    def clamp_specs(boxes, grid):
        out, dropped = [], 0
        for b in boxes:
            lo, hi = box_bounds(grid, b)
            clamped = clamp_box_to_hull(lo, hi, eqs, margin, grid.res)
            if clamped is None:
                dropped += 1
                continue
            clo, chi = clamped
            out.append(((clo + chi) * 0.5, chi - clo))
        return out, dropped

    # COARSE pass: engulf the fine solid at the box resolution, carve corridors, greedy-decompose.
    # A few large boxes cover the bulk cheaply.
    gbox = grid_for(V, box_res)
    solid_box = downsample_solid(gfine, solid_fine, gbox)
    corridor_box = corridor_mask(gbox, segs)
    boxes = greedy_boxes(solid_box & ~corridor_box)
    specs, dropped = clamp_specs(boxes, gbox)

    # FINE PATCH passes: the hull-containment clamp shrinks coarse boxes at the extremities (tower
    # top, arm/spindle tips) where the visual hull is tight, which can re-open a narrow gap into a
    # ship-sized interior pocket the coarse box had covered. Detect exactly those leaks with the
    # fine-res reachability flood and plug the reachable SOLID cells around each leak with small FINE
    # boxes (which barely clamp), iterating until the hollow is provably sealed. Only a handful of
    # cells leak, so this adds few boxes while guaranteeing the guard.
    reach = max(1, int(np.ceil(ship_r / gfine.res)) + 1)
    patched = 0
    for _ in range(8):
        eqs_list = [box_eqs(c, s) for c, s in specs]
        solidF = rasterize_parts(eqs_list, gfine)
        ship_free = (_edt(~solidF) * gfine.res) > ship_r
        lab, _ = _label(ship_free)
        ext = np.isin(lab, list(_boundary_labels(lab))) & ship_free
        leaked_hollow = interior_hollow & ext & ~corridor_fine
        if not leaked_hollow.any():
            break
        # Seal the channel: box the still-uncovered solid cells within ship reach of the leak.
        plug = solid_fine & ext & ~corridor_fine & _dil(leaked_hollow, iterations=reach)
        pboxes = greedy_boxes(plug)
        pspecs, pdrop = clamp_specs(pboxes, gfine)
        specs.extend(pspecs)
        patched += len(pspecs)
        dropped += pdrop

    # Deterministic ordering (rounded center) + stable names.
    specs.sort(key=lambda cs: (round(float(cs[0][0]), 4), round(float(cs[0][1]), 4), round(float(cs[0][2]), 4)))
    yparts = [
        {"name": f"Auto{i:02d}",
         "box": {"center": [float(c[0]), float(c[1]), float(c[2])],
                 "size": [float(s[0]), float(s[1]), float(s[2])]}}
        for i, (c, s) in enumerate(specs)
    ]

    # Coverage: fraction of carved fine-solid voxel centres inside some FINAL clamped box.
    fine_carved = solid_fine & ~corridor_fine
    sxs, sys_, szs = np.where(fine_carved)
    scen = gfine.origin + (np.stack([sxs, sys_, szs], axis=1) + 0.5) * gfine.res
    covered = np.zeros(len(scen), dtype=bool)
    for c, s in specs:
        covered |= np.all((scen >= c - s * 0.5 - 1e-9) & (scen <= c + s * 0.5 + 1e-9), axis=1)
    coverage = float(covered.mean()) if len(scen) else 1.0

    stats = dict(
        gfine=gfine, interior_hollow=interior_hollow, corridor_fine=corridor_fine,
        ship_r=ship_r, dropped=dropped, patched=patched, box_count=len(yparts),
        coverage=coverage, solid_fine=int(solid_fine.sum()), sealed=int(sealed_fine.sum()),
        hollow=int(interior_hollow.sum()), surface=int(Sfine.sum()),
        box_res=box_res, voxel_res=voxel_res, segs=segs,
    )
    return yparts, stats


def write_generated_yaml(path: Path, yparts, cfg):
    """Persist the generated box list for inspection (and as the concrete record of what was baked).
    Not consumed by the bake — the bake regenerates from the mesh each run for determinism."""
    lines = [
        "# base-col.generated.yaml — GENERATED by `bake.py --auto`; DO NOT hand-edit.",
        "# A snapshot of the deterministic voxel solid-fill + greedy box-merge output for review.",
        "# The bake regenerates these from base.glb every run (auto: true in base-col.yaml); this",
        "# file is only a human-readable record. Regenerate: `uv run bake.py --auto`.",
        f"# voxel_res={cfg['voxel_res']}  box_res={cfg['box_res']}  ship_radius(authored)={cfg['ship_r']:.4f}"
        f"  corridor_radius={cfg['corridor_r']:.4f}",
        f"# {len(yparts)} parts",
        "margin: 0.05",
        "parts:",
    ]
    for p in yparts:
        c = p["box"]["center"]
        s = p["box"]["size"]
        lines.append(f"  - name: {p['name']}")
        lines.append(f"    box: {{center: [{c[0]:.4f}, {c[1]:.4f}, {c[2]:.4f}], "
                     f"size: [{s[0]:.4f}, {s[1]:.4f}, {s[2]:.4f}]}}")
    path.write_text("\n".join(lines) + "\n")


# ---------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description="Bake authored COL_ collision parts into base.glb")
    ap.add_argument("--glb", type=Path, default=DEF_GLB)
    ap.add_argument("--yaml", type=Path, default=DEF_YAML)
    ap.add_argument("--out", type=Path, default=None, help="default: rewrite --glb in place")
    ap.add_argument("--check", action="store_true", help="validate only; do not write")
    ap.add_argument("--suggest", action="store_true", help="print candidate boxes and exit")
    ap.add_argument("--preview-dir", type=Path, default=None,
                    help="render reviewer PNGs to DIR (default ./preview unless --check/--suggest)")
    ap.add_argument("--auto", action="store_true",
                    help="generate parts from the mesh volume (voxel solid-fill + greedy box merge), "
                         "overriding the YAML parts list; also writes base-col.generated.yaml")
    args = ap.parse_args(argv)

    gltf = pygltflib.GLTF2().load(str(args.glb))
    blob = gltf.binary_blob()
    verts, hps = read_visual_vertices(gltf, blob)
    vlo, vhi = verts.min(0), verts.max(0)
    eqs = hull_equations(verts)
    ws = world_scale(verts)
    print(f"visual mesh: {len(verts)} verts  AABB min={np.round(vlo,2)} max={np.round(vhi,2)} "
          f"longestAxis={(vhi-vlo).max():.4f}  worldScale={ws:.4f}")

    if args.suggest:
        suggest(verts, eqs, k=7)
        return 0

    spec = yaml.safe_load(args.yaml.read_text())
    margin = float(spec.get("margin", 0.05))
    corridor_tol = float(spec.get("corridor_tol", 0.05))

    # Triangles (with indices) drive the auto voxelizer AND the reachability guard's sealed-interior
    # classification (run in BOTH modes, so a hand-authored spec is guarded too).
    V, F = read_visual_triangles(gltf, blob)
    cfg = auto_config(spec, ws)

    auto = args.auto or bool(spec.get("auto", False))
    autostats = None
    if auto:
        print(f"\n--auto: voxel_res={cfg['voxel_res']}  box_res={cfg['box_res']}  "
              f"shipRadius(authored)={cfg['ship_r']:.4f}  corridorRadius={cfg['corridor_r']:.4f}")
        yparts, autostats = generate_auto_parts(verts, V, F, hps, eqs, cfg)
        print(f"--auto: {autostats['surface']} surface + {autostats['sealed']} sealed voxels "
              f"({autostats['hollow']} ship-fits hollow) @res {cfg['voxel_res']} -> "
              f"{autostats['box_count']} boxes ({autostats['patched']} fine seal-patches, "
              f"{autostats['dropped']} collapsed/dropped by hull clamp), "
              f"coverage {autostats['coverage']*100:.2f}%")
        write_generated_yaml(HERE / "base-col.generated.yaml", yparts, cfg)
        print(f"--auto: wrote {HERE / 'base-col.generated.yaml'}")
        if not (2 <= len(yparts) <= 200):
            sys.exit(f"ERROR: auto produced {len(yparts)} parts (expected 2..200)")
    else:
        yparts = spec["parts"]
        if not (4 <= len(yparts) <= 10):
            sys.exit(f"ERROR: expected 4..10 parts, got {len(yparts)}")

    # Build each part's convex mesh (deterministic order = YAML order, then by name for safety).
    yparts = sorted(yparts, key=lambda p: p["name"])
    parts = []
    all_col = []
    part_eqs = []
    print(f"\n{'part':14} {'verts':>5} {'AABB (min..max)':>34}  {'hull-margin':>11}")
    ok = True
    for p in yparts:
        name = p["name"]
        raw = part_vertices(p)
        hv, faces, _ = convex_mesh(raw)
        parts.append((name, hv, faces))
        all_col.append(hv)
        peq = hull_equations(hv)
        part_eqs.append(peq)
        d = signed_dist_to_hull(hv.astype(np.float64), eqs)
        worst = d.max()  # want <= -margin
        plo, phi = hv.min(0), hv.max(0)
        flag = "" if worst <= -margin else "  <-- VIOLATION (pokes out of visual hull)"
        if worst > -margin:
            ok = False
        print(f"{name:14} {len(hv):5d}  [{plo[0]:5.1f},{plo[1]:5.1f},{plo[2]:5.1f}]"
              f"..[{phi[0]:5.1f},{phi[1]:5.1f},{phi[2]:5.1f}]  {(-worst):8.4f}{flag}")

    # AABB containment (MeshAabb scale contract).
    call = np.vstack(all_col)
    clo, chi = call.min(0), call.max(0)
    aabb_tol = 1e-4
    if (clo < vlo - aabb_tol).any() or (chi > vhi + aabb_tol).any():
        print(f"ERROR: COL union AABB [{clo}..{chi}] exceeds visual AABB [{vlo}..{vhi}]")
        ok = False
    else:
        print(f"\nAABB containment OK: COL [{np.round(clo,2)}..{np.round(chi,2)}] "
              f"within visual [{np.round(vlo,2)}..{np.round(vhi,2)}]")

    # Dock-corridor clearance. The bay doors face INWARD (each entrance sits on a face of the bay
    # box and its +Z forward points toward the bay centre), so the meaningful corridor is the
    # entrance disc itself plus the swept segment from it toward the door centre (the mean of the
    # entrance positions) — and the exit toward that same centre. Every such sample must sit
    # outside all COL parts so no part ever caps a corridor.
    ent = [(n, p, f) for n, p, f in hps if "Entrance" in n]
    ext = [(n, p, f) for n, p, f in hps if "Exit" in n]
    door = np.mean([p for n, p, f in ent], axis=0) if ent else np.zeros(3)
    print(f"\ndock-door centre ~ {np.round(door,2)}; corridor samples must stay OUTSIDE all parts")
    corridor_fail = 0
    for n, p, f in ent + ext:
        samples = [p]
        for t in np.linspace(0.0, 1.0, 9):          # door disc -> door centre
            samples.append(p + (door - p) * t)
        for s in samples:
            for name, peq in zip([x[0] for x in parts], part_eqs):
                if point_inside_part(np.asarray(s, float), peq, corridor_tol):
                    print(f"  VIOLATION: {n} corridor point {np.round(s,2)} is inside part {name}")
                    corridor_fail += 1
    if corridor_fail == 0:
        print(f"  corridor clearance OK ({len(ent)} entrances + {len(ext)} exit swept)")
    else:
        ok = False

    # REACHABILITY GUARD (the regression test for the fly-inside bug). Rasterize the FINAL parts into
    # a fine voxel grid and flood the exterior with the free space eroded by the ship radius: no
    # ship-radius exterior path may reach a sealed-interior cell (a hollow the player could fly into),
    # except inside a carved dock corridor. Runs in BOTH modes so sparse hand-authored specs are
    # guarded too — the old star-of-boxes layout leaks badly here, which is the point.
    if autostats is not None:
        gfine = autostats["gfine"]
        interior_hollow = autostats["interior_hollow"]
        corridor_fine = autostats["corridor_fine"]
    else:
        from scipy.ndimage import distance_transform_edt as _edt
        segs = corridor_segments(hps, cfg["corridor_r"], cfg["corridor_approach"])
        gfine = grid_for(V, cfg["voxel_res"])
        Sfine = voxelize_surface(V, F, gfine)
        _, sealed_fine, _ = classify_solid(Sfine)
        interior_hollow = sealed_fine & ((_edt(~Sfine) * gfine.res) > cfg["ship_r"])
        corridor_fine = corridor_mask(gfine, segs)
    leaks = reachability_leaks(part_eqs, gfine, interior_hollow, corridor_fine, cfg["ship_r"])
    print(f"\nreachability guard: sealed-interior voxels a ship (r={cfg['ship_r']:.3f} authored) can "
          f"reach from OUTSIDE the FINAL parts (excl. corridors) = {leaks}")
    if leaks > 0:
        print(f"  VIOLATION: {leaks} sealed-interior voxels are ship-reachable — the parts leave a "
              f"gap a ship can fly through into the station interior.")
        ok = False
    else:
        print("  reachability OK — the interior is sealed against a ship-radius fly-through.")

    if not ok:
        sys.exit("\nFAILED validation — not writing GLB.")
    print("\nAll validations PASSED.")

    if args.preview_dir is not None or not args.check:
        pdir = args.preview_dir or (HERE / "preview")
        pngs = render_preview(verts, hps, parts, pdir)
        print("preview:", *[str(x) for x in pngs])

    if args.check:
        print("--check: not writing.")
        return 0

    out = args.out or args.glb
    data = bake_glb(gltf, blob, parts)
    Path(out).write_bytes(data)
    print(f"\nwrote {out}  ({len(data)} bytes)  sha256={hashlib.sha256(data).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
