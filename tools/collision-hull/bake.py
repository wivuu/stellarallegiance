#!/usr/bin/env python3
"""collision-hull — generate + bake convex COLLISION-PROXY parts (COL_ nodes) into any mesh GLB.

A visual GLB (e.g. `client/assets/bases/base.glb`) is ONE welded, concave mesh. Both the server sim
and the Godot client build a single QuickHull "shrink-wrap" over the whole cloud, so ships and bolts
collide with an invisible convex balloon rather than the visible surface. For BASES the runtime reads
a COMPOUND hull instead: one convex hull per COL_ part node this tool appends.

This tool GENERATES those parts straight from the mesh volume — there is no hand-authored spec. It
voxel solid-fills the visual triangles, seals the hollow interior, carves any dock corridors back
open, greedy-merges the solid into axis-aligned boxes, clamps each strictly inside the visual convex
hull, adds a surface shell pass, and APPENDS one small triangulated `COL_<name>` mesh node per box.
The visual mesh, its material, and every `HP_` empty are left untouched.

All tuning is via CLI args resolved from a per-`--kind` preset (there is no YAML config). The bake
is metric-neutral for the pre-compound-hull collision code — two hard, load-bearing validations
enforce that (the bake FAILS loudly otherwise):

  1. HULL CONTAINMENT — every COL vertex lies strictly inside the convex hull of the visual mesh
     (signed distance <= -margin to every hull face). Because the max of any linear functional
     (and of |p|) over a convex set is attained at a vertex, a strictly-interior point can never
     be a directional extreme, never enlarge the AABB, and never enlarge the bounding radius.
     Hence ConvexHull.Build's ReduceToExtremes(256) still selects only visual vertices — the
     merged hull, its LongestAxis, and its BoundingRadius are bit-unchanged.
  2. DOCK CORRIDOR — every docking-entrance disc centre, and a swept segment from it toward the
     bay-door centre and outward along its approach, lies OUTSIDE all COL parts, so no part ever
     caps a corridor a ship must fly through. (Auto-skipped when the mesh has no HP_Docking* nodes.)

A weaker AABB-containment check is also asserted (the explicit MeshAabb scale contract the client
relies on), and the output is written deterministically (fixed ordering, cleaned float32) so a
re-bake of unchanged input + identical resolved args yields a byte-identical GLB (identical SHA).

Usage (via uv — deps in pyproject.toml):
  uv run bake.py --kind base                    # bake in place: client/assets/bases/base.glb
  uv run bake.py --kind base --check            # validate only, do not write
  uv run bake.py --kind ship --glb PATH --model-length 5.5 --check   # any ship mesh (scale basis)
  uv run bake.py --kind base --preview out.png  # combined visualizer figure to an exact path
  uv run bake.py --kind base --preview-dir DIR  # ortho + 3D reviewer PNGs into DIR
  uv run bake.py --kind base --dump snap.txt    # opt-in provenance snapshot of the resolved args

All tunables (--voxel-res --box-res --margin --pad --shell/--no-shell --shell-iters --corridor-*
--ship-radius --hull-extremes --reach-guard/--no-reach-guard --corridor-check/--no-corridor-check)
default to the kind preset; pass one to override it.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

import numpy as np
import pygltflib
from scipy.spatial import ConvexHull

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DEF_GLB = REPO / "client" / "assets" / "bases" / "base.glb"

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


def part_vertices(part) -> np.ndarray:
    """Corner cloud for a generated box spec — the only primitive the auto pipeline emits."""
    if "box" in part:
        return _box_verts(part["box"])
    raise ValueError(f"part {part.get('name')!r} has no box")


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

def reduce_to_extremes(pts: np.ndarray, dir_count: int) -> np.ndarray:
    """Port of shared/Collision/ConvexHull.cs ReduceToExtremes: keep only the points that are the
    farthest along each of `dir_count` evenly-spread spherical-Fibonacci directions. A hull vertex is
    always the extreme along SOME direction, so this leaves a tiny superset of the true hull vertices
    (interior points never survive) — the containment hull is unchanged but built from far fewer
    points. `--hull-extremes 256` reproduces exactly the cloud the sim/client per-entity hull reduces
    to. Returns `pts` unchanged when it already has <= dir_count points (or dir_count <= 0)."""
    if dir_count <= 0 or len(pts) <= dir_count:
        return pts
    i = np.arange(dir_count)
    y = 1.0 - (i + 0.5) * 2.0 / dir_count
    r = np.sqrt(np.maximum(0.0, 1.0 - y * y))
    golden = np.pi * (3.0 - np.sqrt(5.0))
    theta = golden * i
    dirs = np.stack([np.cos(theta) * r, y, np.sin(theta) * r], axis=1)  # (D, 3) unit directions
    keep = np.unique(np.argmax(pts @ dirs.T, axis=0))                   # farthest point per direction
    return pts[keep]


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
#  Reviewer preview render (the tool's args-driven visualizer, for ANY mesh/kind)
# ---------------------------------------------------------------------------

def render_preview(verts, hps, parts, stem: str, kind: str,
                   preview_dir: Path | None = None, preview_path: Path | None = None):
    """Render the visual cloud (grey) + generated COL_ parts (coloured wireframes) for ANY mesh/kind.
    Title + output filenames come from `stem` + `kind`; dock-HP star markers are drawn only when the
    mesh has any (a no-op for ships/asteroids). Two output modes, either or both:
      * preview_dir → the ortho-triptych PNG + the 3D PNG as <stem>-col-ortho.png / <stem>-col-3d.png
      * preview_path → ONE combined figure (ortho triptych + 3D, 2x2 grid) written to that exact path
    Works with --check (visualize without baking). Returns the list of written paths."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Line3DCollection

    colors = plt.cm.tab10(np.linspace(0, 1, max(10, len(parts))))
    ent = np.array([p for n, p, f in hps if "Entrance" in n or "Exit" in n])
    title = f"{stem}.glb visual cloud (grey) + {kind} COL_ parts"
    planes = [(2, 1, "Z", "Y", "side"), (0, 1, "X", "Y", "front"), (2, 0, "Z", "X", "top")]

    def edges_of(hv, faces):
        segs = set()
        for a, b, c in faces:
            for u, v in ((a, b), (b, c), (c, a)):
                segs.add((min(u, v), max(u, v)))
        return [[hv[u], hv[v]] for u, v in segs]

    def draw_ortho(ax, a, b, la, lb, ptitle):
        ax.scatter(verts[:, a], verts[:, b], s=1, alpha=0.12, c="0.5", linewidths=0)
        for k, (name, hv, faces) in enumerate(parts):
            for seg in edges_of(hv, faces):
                s = np.array(seg)
                ax.plot(s[:, a], s[:, b], c=colors[k], lw=1.0, alpha=0.9)
            ax.text(hv[:, a].mean(), hv[:, b].mean(), name, color=colors[k], fontsize=7, ha="center")
        if len(ent):
            ax.plot(ent[:, a], ent[:, b], "r*", ms=13, label="dock HP")
        ax.set_xlabel(la); ax.set_ylabel(lb); ax.set_title(ptitle); ax.set_aspect("equal")

    def draw_3d(ax):
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
        ax.set_title(f"{kind} COL_ parts vs visual cloud (3D)")

    written = []

    if preview_dir is not None:  # the reviewer pair, per-stem names
        preview_dir.mkdir(parents=True, exist_ok=True)
        fig, axs = plt.subplots(1, 3, figsize=(21, 7))
        for ax, (a, b, la, lb, pt) in zip(axs, planes):
            draw_ortho(ax, a, b, la, lb, pt)
        axs[0].legend(loc="upper right", fontsize=8)
        fig.suptitle(title, fontsize=13); fig.tight_layout()
        p1 = preview_dir / f"{stem}-col-ortho.png"
        fig.savefig(p1, dpi=95); plt.close(fig)

        fig = plt.figure(figsize=(11, 10))
        ax = fig.add_subplot(111, projection="3d")
        draw_3d(ax)
        p2 = preview_dir / f"{stem}-col-3d.png"
        fig.savefig(p2, dpi=95); plt.close(fig)
        written += [p1, p2]

    if preview_path is not None:  # one combined figure to the exact path given
        preview_path = Path(preview_path)
        if preview_path.parent != Path(""):
            preview_path.parent.mkdir(parents=True, exist_ok=True)
        fig = plt.figure(figsize=(20, 14))
        for i, (a, b, la, lb, pt) in enumerate(planes):
            ax = fig.add_subplot(2, 2, i + 1)
            draw_ortho(ax, a, b, la, lb, pt)
        ax3 = fig.add_subplot(2, 2, 4, projection="3d")
        draw_3d(ax3)
        fig.suptitle(title, fontsize=15); fig.tight_layout()
        fig.savefig(preview_path, dpi=95); plt.close(fig)
        written.append(preview_path)

    return written


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


def rasterize_boxes(specs, grid: VoxelGrid) -> np.ndarray:
    """FAST solid mask for AXIS-ALIGNED box specs [(center, size)]: mark every cell whose CENTRE lies
    inside a box by direct index-range assignment — no per-cell plane dot product, so it scales to the
    thousands of thin shell boxes the shell pass generates (rasterize_parts over the full fine grid
    would be minutes at that count). Cell-centre-in-box semantics match rasterize_parts for boxes; it
    is used only for the shell pass's coverage bookkeeping — the FINAL reachability guard still runs
    through the exact plane-based rasterize_parts."""
    dims = np.array(grid.dims)
    origin = grid.origin
    res = grid.res
    B = np.zeros(grid.dims, dtype=bool)
    for c, s in specs:
        c = np.asarray(c, float)
        s = np.asarray(s, float)
        lo = np.ceil((c - s * 0.5 - origin) / res - 0.5).astype(int)
        hi = np.floor((c + s * 0.5 - origin) / res - 0.5).astype(int) + 1
        lo = np.clip(lo, 0, dims)
        hi = np.clip(hi, 0, dims)
        if (hi > lo).all():
            B[lo[0]:hi[0], lo[1]:hi[1], lo[2]:hi[2]] = True
    return B


def shell_cover(specs, Sfine, gfine, corridor_fine, eqs, guard_segs, cfg):
    """SHELL PASS — the surface-shell coverage guarantee.

    After the bulk voxel decomposition + seal-patches, a large fraction of the VISIBLE-SURFACE voxels
    still have no box at/just outside them: a ship approaching those points — especially the WALLS of a
    concavity, which sit well inside the visual convex hull — sinks inward until it reaches an interior
    bulk box before it bounces (the 'visual sink'). This pass closes that gap: it finds the surface
    voxels not yet covered by any box and greedy-merges THEM into thin boxes, each padded outward by
    `pad`, hull-clamped, and corridor-retreated exactly like every other box, so collision sits right
    at the visible skin. Because concavity walls are interior to the convex hull, the outward pad is NOT
    clamped there — that is exactly where the sink was worst and where this pass wins most. At convex
    extremities the hull clamp still wins (metric-neutral); a voxel right on the convex skin can only be
    covered to within `margin`, and a protrusion thinner than a metric-neutral box can hold is left
    uncovered (reported) — those are the only surface voxels the invariants make impossible to cover.

    Deterministic: greedy_boxes fixed scan order, the same clamp/retreat machinery as the bulk pass, and
    each candidate box is kept only if it actually covers a still-uncovered surface voxel (so the count
    stays honest and reproducible). Iterates because clamping/retreating one box can leave a neighbour
    surface voxel newly exposed. Returns (specs, added)."""
    pad = cfg["pad"]
    margin = cfg["margin"]
    voxel_res = cfg["voxel_res"]
    corridor_r = cfg["corridor_r"]
    added = 0
    for _ in range(int(cfg.get("shell_iters", 6))):
        solidF = rasterize_boxes(specs, gfine)
        uncovered = Sfine & ~solidF & ~corridor_fine
        if not uncovered.any():
            break
        boxes = greedy_boxes(uncovered)
        cand = []
        for b in boxes:
            lo, hi = box_bounds(gfine, b)
            lo = np.asarray(lo, float) - pad
            hi = np.asarray(hi, float) + pad
            clamped = clamp_box_to_hull(lo, hi, eqs, margin, gfine.res)
            if clamped is None:
                continue
            clo, chi = clamped
            cand.append(((clo + chi) * 0.5, chi - clo))
        if not cand:
            break
        cand, _drop = retreat_from_corridors(cand, guard_segs, corridor_r, voxel_res)
        if not cand:
            break
        # Keep only boxes that still cover a previously-uncovered surface voxel after clamp+retreat
        # (drop the ones the clamp pulled entirely off the skin); charge each voxel to one box only.
        remaining = uncovered.copy()
        kept = []
        for c, s in cand:
            m = rasterize_boxes([(c, s)], gfine)
            if (m & remaining).any():
                kept.append((c, s))
                remaining &= ~m
        if not kept:
            break
        specs = specs + kept
        added += len(kept)
    return specs, added


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


def retreat_from_corridors(specs, segs, corridor_r: float, res: float):
    """Keep-out DUAL of the hull clamp: pull each box's faces inward until it clears every dock
    corridor by `corridor_r`. `pad` grows boxes isotropically, and the coarse carve's cell-centre
    discretization lets a padded box's corner poke into the flyable tube; this trims exactly that
    overlap so the corridor validator + the SelfTest dock-ray + spawn clearance still pass. Only
    boxes that actually intrude move (surgical — coverage elsewhere is untouched). The freed sliver
    lies within `corridor_r` of the centreline = inside the carved corridor, so it never re-opens a
    reachability leak (the guard excludes the corridor). Deterministic small-step shrink."""
    if not segs:
        return specs, 0
    # Dense samples along every corridor centreline (the capsule axis a corridor is swept along).
    qs = []
    for a, b, _r in segs:
        a = np.asarray(a, float)
        b = np.asarray(b, float)
        n = max(2, int(np.ceil(float(np.linalg.norm(b - a)) / (res * 0.5))) + 1)
        for t in np.linspace(0.0, 1.0, n):
            qs.append(a + (b - a) * t)
    Q = np.array(qs)
    step = res * 0.1
    out, dropped = [], 0
    for c, s in specs:
        lo = c - s * 0.5
        hi = c + s * 0.5
        collapsed = False
        for _ in range(4000):
            cp = np.clip(Q, lo, hi)                       # closest box point to each centreline sample
            d = np.linalg.norm(Q - cp, axis=1)
            w = int(np.argmin(d))
            if d[w] >= corridor_r:                        # box already clears the whole corridor
                break
            q = Q[w]
            # Cheapest single face to move so this sample sits >= corridor_r outside the box.
            best_ax, best_hi, best_cost = 0, True, np.inf
            for ax in range(3):
                cost_hi = hi[ax] - (q[ax] - corridor_r)   # push hi[ax] down under q-corridor_r
                cost_lo = (q[ax] + corridor_r) - lo[ax]   # push lo[ax] up over  q+corridor_r
                if 0.0 < cost_hi < best_cost:
                    best_cost, best_ax, best_hi = cost_hi, ax, True
                if 0.0 < cost_lo < best_cost:
                    best_cost, best_ax, best_hi = cost_lo, ax, False
            if best_hi:
                hi[best_ax] -= step
            else:
                lo[best_ax] += step
            if (hi - lo).min() <= res:  # box sat (almost) entirely in the flyable tube — drop it
                collapsed = True
                break
        if collapsed:
            dropped += 1
            continue
        out.append(((lo + hi) * 0.5, hi - lo))
    return out, dropped


# Per-kind pipeline presets — the resolution baseline for every knob. `base` locks the values the
# retired base-col.yaml carried (box_res 1.5, pad 0.5, margin 0.05, ...): a no-override `--kind base`
# MUST reproduce the committed base.glb byte-for-byte, so these are a hard contract, not a default to
# tweak. `ship` shares the coverage knobs but drops the outward pad (0.0), widens the part-count
# window, and leaves the reach guard / corridor validator off (turned on by --kind auto-detection or
# an explicit flag). ship_radius / corridor_radius are filled from the world constants via `ws` below.
KIND_PRESETS = {
    "base": dict(voxel_res=0.5, box_res=1.5, margin=0.05, pad=0.5, corridor_tol=0.05,
                 corridor_clearance=0.5, corridor_approach=5.0, shell=True, shell_iters=6,
                 count_lo=2, count_hi=1024),
    "ship": dict(voxel_res=0.5, box_res=1.5, margin=0.05, pad=0.0, corridor_tol=0.05,
                 corridor_clearance=0.5, corridor_approach=5.0, shell=True, shell_iters=6,
                 count_lo=1, count_hi=100000),
}


def resolve_cfg(args, kind: str, ws: float) -> dict:
    """Resolve the pipeline knobs from the kind preset, overridden by any explicit CLI arg (every
    tunable defaults to None so 'unset' is distinguishable from a passed value). Authored-unit
    thresholds fall back to the world collision constants via world-scale `ws`, exactly as the
    sim/client derive them at load. Returns the same cfg dict shape generate_auto_parts consumes,
    plus the per-kind count window + the resolved corridor_tol / hull_extremes the validators read."""
    p = KIND_PRESETS[kind]

    def pick(name, default):
        v = getattr(args, name, None)
        return default if v is None else v

    clearance = pick("corridor_clearance", p["corridor_clearance"])
    ship_r = pick("ship_radius", WORLD_SHIP_RADIUS / ws)
    corridor_r = pick("corridor_radius", max(WORLD_DOCK_DISC_RADIUS / ws, ship_r + clearance))
    return dict(
        voxel_res=float(pick("voxel_res", p["voxel_res"])),
        box_res=float(pick("box_res", p["box_res"])),
        margin=float(pick("margin", p["margin"])),
        pad=float(pick("pad", p["pad"])),
        ship_r=float(ship_r),
        corridor_r=float(corridor_r),
        corridor_approach=float(pick("corridor_approach", p["corridor_approach"])),
        shell=bool(pick("shell", p["shell"])),
        shell_iters=int(pick("shell_iters", p["shell_iters"])),
        corridor_tol=float(pick("corridor_tol", p["corridor_tol"])),
        hull_extremes=int(pick("hull_extremes", 0)),
        count_lo=p["count_lo"],
        count_hi=p["count_hi"],
    )


def generate_auto_parts(verts, V, F, hps, eqs, cfg):
    """Run the full pipeline and return (yparts, stats). `yparts` are box specs in the SAME schema
    the authored path consumes: [{'name': 'Auto00', 'box': {'center': [...], 'size': [...]}}]."""
    ship_r = cfg["ship_r"]
    box_res = cfg["box_res"]
    voxel_res = cfg["voxel_res"]
    margin = cfg["margin"]
    pad = cfg["pad"]
    segs = corridor_segments(hps, cfg["corridor_r"], cfg["corridor_approach"])
    # Grow-outward margin (`pad`): after the greedy merge every box is inflated by `pad` on all six
    # faces BEFORE the hull-containment clamp, so collision reaches out to the visual surface (at the
    # extremity tips the clamp still wins → metric-neutral) instead of stopping strictly inside it —
    # ships bounce at/just outside the visible hull rather than sinking into the thin outer shell.
    # The flyable dock tube is protected AFTER padding by the retreat_from_corridors keep-out pass
    # (trims any box back to the true corridor wall), so the coarse carve here stays at corridor_r.

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

    def clamp_specs(boxes, grid, pad_amt=0.0):
        out, dropped = [], 0
        for b in boxes:
            lo, hi = box_bounds(grid, b)
            if pad_amt > 0.0:  # inflate outward on all faces, then let the hull clamp trim the overshoot
                lo = np.asarray(lo, float) - pad_amt
                hi = np.asarray(hi, float) + pad_amt
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
    specs, dropped = clamp_specs(boxes, gbox, pad)  # the coarse bulk grows out to the visual surface

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

    # Dock keep-out geometry — the swept carve segments PLUS each hardpoint's straight line to the door
    # centre (the bake corridor validator) and its radial approach ray (the server SelfTest dock ray /
    # spawn cone). Both the corridor keep-out and the shell pass retreat against exactly this, so all
    # three downstream guards (bake corridor validator, SelfTest dock ray, spawn clearance) stay green.
    ent_p = [np.asarray(p, float) for n, p, f in hps if "Entrance" in n]
    ext_p = [np.asarray(p, float) for n, p, f in hps if "Exit" in n]
    door = np.mean(ent_p, axis=0) if ent_p else np.zeros(3)

    def _radial(p):
        l = float(np.linalg.norm(p))
        return p / l if l > 1e-6 else np.array([0.0, 0.0, 1.0])

    guard_segs = list(segs)
    for p in ent_p + ext_p:
        guard_segs.append((p, door, cfg["corridor_r"]))                                  # hardpoint -> door
        guard_segs.append((p, p + _radial(p) * cfg["corridor_approach"], cfg["corridor_r"]))  # radial ray

    # Corridor keep-out: `pad` can grow a box into a flyable dock tube (coarse-carve discretization);
    # trim the offending boxes back to the true corridor wall so docking/launch stay clear.
    if pad > 0.0:
        specs, rdrop = retreat_from_corridors(specs, guard_segs, cfg["corridor_r"], voxel_res)
        dropped += rdrop

    # SHELL PASS: cover the visible-surface voxels the bulk decomposition left exposed (the concavity
    # walls + outer skin) with thin, padded, hull-clamped, corridor-retreated boxes so a ship bounces at
    # the visible surface instead of sinking to an interior box. This is the visual-sink fix; it may add
    # many boxes (accepted — each is 6 cheap planes). See shell_cover for the invariant handling.
    shell_added = 0
    if cfg.get("shell", True):
        specs, shell_added = shell_cover(specs, Sfine, gfine, corridor_fine, eqs, guard_segs, cfg)

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

    # SURFACE coverage + VISUAL SINK: rasterize the final boxes on the fine grid, then for every
    # visible-surface voxel measure the distance to the nearest box cell (0 if covered). `surface_cov`
    # = fraction of surface voxels with a box on them; the sink metric is that distance in WORLD units
    # (authored * ws) — how far a ship sinks past the visible skin before it bounces. Reported two ways:
    #   * sink_all_* — over ALL surface voxels (0 where covered): the true typical-case feel metric.
    #   * sink_unc_* — over only the still-UNCOVERED voxels: the residual worst cases (thin protrusions
    #     / convex skin the metric-neutrality clamp cannot cover); its mean rises as coverage improves
    #     because it conditions on the hardest voxels, so read it alongside surface_cov, not alone.
    from scipy.ndimage import distance_transform_edt as _edt2
    ws = world_scale(verts)  # authored -> world units, same derivation the sim/client use
    solid_final = rasterize_boxes(specs, gfine)
    surf_cov = float((Sfine & solid_final).sum() / Sfine.sum()) if Sfine.any() else 1.0
    sink_world = _edt2(~solid_final) * gfine.res * ws
    sink_all = sink_world[Sfine]
    sink_unc = sink_world[Sfine & ~solid_final]

    stats = dict(
        gfine=gfine, interior_hollow=interior_hollow, corridor_fine=corridor_fine,
        ship_r=ship_r, dropped=dropped, patched=patched, shell_added=shell_added,
        box_count=len(yparts),
        coverage=coverage, surface_cov=surf_cov, solid_fine=int(solid_fine.sum()),
        sealed=int(sealed_fine.sum()), hollow=int(interior_hollow.sum()), surface=int(Sfine.sum()),
        sink_all_mean=float(sink_all.mean()) if sink_all.size else 0.0,
        sink_all_p90=float(np.percentile(sink_all, 90)) if sink_all.size else 0.0,
        sink_all_max=float(sink_all.max()) if sink_all.size else 0.0,
        sink_unc_mean=float(sink_unc.mean()) if sink_unc.size else 0.0,
        sink_unc_max=float(sink_unc.max()) if sink_unc.size else 0.0,
        sink_over_1r=float((sink_all > WORLD_SHIP_RADIUS).mean()) if sink_all.size else 0.0,
        box_res=box_res, voxel_res=voxel_res, pad=pad, segs=segs,
    )
    return yparts, stats


def write_snapshot(path: Path, yparts, cfg, kind: str, primitive: str, glb: Path, ws: float):
    """Opt-in (`--dump PATH`) human-readable record of a bake: the kind, the source GLB, every
    resolved arg, and the baked box list. Replaces the retired base-col.generated.yaml — NOT consumed
    by the bake (which regenerates from the mesh every run for determinism), purely provenance so a
    reviewer can see exactly what a given SHA was baked from. Manual string formatting, no yaml lib."""
    lines = [
        f"# collision-hull snapshot — GENERATED by `bake.py --kind {kind}`; provenance only, not consumed.",
        "# A record of the deterministic voxel solid-fill + greedy box-merge output for a bake.",
        f"# kind={kind}  primitive={primitive}  glb={glb}  worldScale={ws:.6f}",
        f"# voxel_res={cfg['voxel_res']}  box_res={cfg['box_res']}  pad={cfg['pad']}  "
        f"margin={cfg['margin']}  hull_extremes={cfg['hull_extremes']}",
        f"# shell={cfg['shell']}  shell_iters={cfg['shell_iters']}  corridor_tol={cfg['corridor_tol']}",
        f"# ship_radius(authored)={cfg['ship_r']:.4f}  corridor_radius={cfg['corridor_r']:.4f}  "
        f"corridor_approach={cfg['corridor_approach']}",
        f"# {len(yparts)} parts",
        "parts:",
    ]
    for p in yparts:
        c = p["box"]["center"]
        s = p["box"]["size"]
        lines.append(f"  - name: {p['name']}")
        lines.append(f"    box: {{center: [{c[0]:.4f}, {c[1]:.4f}, {c[2]:.4f}], "
                     f"size: [{s[0]:.4f}, {s[1]:.4f}, {s[2]:.4f}]}}")
    Path(path).write_text("\n".join(lines) + "\n")


# ---------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description="Generate + bake convex COL_ collision parts into a mesh GLB")
    ap.add_argument("--kind", choices=["base", "ship"], required=True,
                    help="preset selector: base = station (default GLB + corridors + reach guard); "
                         "ship = any ship/model mesh (no pad, guard/corridors off unless present)")
    ap.add_argument("--primitive", choices=["box", "spheroid"], default="box",
                    help="collision part shape (spheroid = future round/asteroid geometry; not yet)")
    ap.add_argument("--glb", type=Path, default=None,
                    help="mesh GLB (defaults to client/assets/bases/base.glb only for --kind base)")
    ap.add_argument("--out", type=Path, default=None, help="default: rewrite --glb in place")
    ap.add_argument("--check", action="store_true", help="validate only; do not write")
    ap.add_argument("--dump", type=Path, default=None,
                    help="write a human-readable provenance snapshot (kind + resolved args + boxes) to PATH")
    ap.add_argument("--preview", type=Path, default=None,
                    help="render ONE combined figure (ortho triptych + 3D) to this exact PNG path")
    ap.add_argument("--preview-dir", type=Path, default=None,
                    help="render the ortho + 3D reviewer PNGs into DIR as <stem>-col-ortho/-3d.png")
    # --- scale basis (authored mesh units -> world units) ---
    ap.add_argument("--world-diameter", type=float, default=180.0,
                    help="base scale basis (CollisionConfig.BaseRadius*2); ws = world_diameter/LongestAxis")
    ap.add_argument("--model-length", type=float, default=None,
                    help="ship scale basis (REQUIRED for --kind ship); ws = model_length/LongestAxis")
    # --- pipeline knobs: all default None so 'unset' falls through to the kind preset ---
    ap.add_argument("--voxel-res", type=float, default=None)
    ap.add_argument("--box-res", type=float, default=None)
    ap.add_argument("--margin", type=float, default=None)
    ap.add_argument("--pad", type=float, default=None)
    ap.add_argument("--shell", action=argparse.BooleanOptionalAction, default=None)
    ap.add_argument("--shell-iters", type=int, default=None)
    ap.add_argument("--corridor-clearance", type=float, default=None)
    ap.add_argument("--corridor-approach", type=float, default=None)
    ap.add_argument("--corridor-radius", type=float, default=None)
    ap.add_argument("--corridor-tol", type=float, default=None)
    ap.add_argument("--ship-radius", type=float, default=None)
    ap.add_argument("--hull-extremes", type=int, default=None,
                    help="0 = full-cloud containment hull (default); >0 = reduce the visual cloud to N "
                         "Fibonacci directional extremes before the hull build (mirrors ConvexHull.cs 256)")
    ap.add_argument("--reach-guard", action=argparse.BooleanOptionalAction, default=None,
                    help="sealed-interior reachability guard (default on for base, off for ship)")
    ap.add_argument("--corridor-check", action=argparse.BooleanOptionalAction, default=None,
                    help="dock-corridor validator (default auto: on iff the mesh has HP_Docking* nodes)")
    # --- spheroid primitive knobs (Phase 3) ---
    ap.add_argument("--sphere-segments", type=int, default=1,
                    help="icosphere subdivisions per spheroid part (spheroid primitive; Phase 3)")
    ap.add_argument("--sphere-overlap", type=float, default=None,
                    help="greedy sphere-cover overlap factor (spheroid primitive; Phase 3)")
    args = ap.parse_args(argv)

    if args.primitive == "spheroid":
        # Phase 3 slots the greedy sphere-cover generator in here; the box path is untouched.
        sys.exit("ERROR: --primitive spheroid is not implemented yet (Phase 3). Use --primitive box.")

    # GLB + scale-basis resolution per kind.
    glb = args.glb if args.glb is not None else (DEF_GLB if args.kind == "base" else None)
    if glb is None:
        sys.exit("ERROR: --glb is required for --kind ship")
    if args.kind == "ship" and args.model_length is None:
        sys.exit("ERROR: --model-length is REQUIRED for --kind ship (ws = model_length/LongestAxis)")

    gltf = pygltflib.GLTF2().load(str(glb))
    blob = gltf.binary_blob()
    verts, hps = read_visual_vertices(gltf, blob)
    if not len(verts):
        sys.exit(f"ERROR: {glb} has no non-COL visual vertices to bake against")
    vlo, vhi = verts.min(0), verts.max(0)
    longest = float((vhi - vlo).max())
    # Same derivation the sim/client use at load — only the numerator differs per kind.
    ws = (args.world_diameter if args.kind == "base" else args.model_length) / max(1e-3, longest)

    cfg = resolve_cfg(args, args.kind, ws)
    print(f"visual mesh: {len(verts)} verts  AABB min={np.round(vlo,2)} max={np.round(vhi,2)} "
          f"longestAxis={longest:.4f}  worldScale={ws:.4f}  shipRadius(authored)={cfg['ship_r']:.4f}")

    # The containment hull the metric-neutrality clamp/validation is measured against. Default is the
    # FULL visual cloud (conservative + current behaviour); --hull-extremes>0 reduces it first.
    eqs = hull_equations(reduce_to_extremes(verts, cfg["hull_extremes"]))

    # Triangles (with indices) drive the voxelizer AND the reachability guard's sealed-interior class.
    V, F = read_visual_triangles(gltf, blob)
    if not len(F):
        sys.exit(f"ERROR: {glb} has no indexed triangles (unindexed prims are skipped) — nothing to voxelize")

    # Resolve the two auto-gated validators. reach-guard: base on / ship off unless overridden.
    # corridor-check: auto = on iff the mesh actually carries dock hardpoints (self-gates for ships).
    has_dock = any((n or "").startswith("HP_Docking") for n, p, f in hps)
    reach_guard = (args.kind == "base") if args.reach_guard is None else args.reach_guard
    corridor_check = has_dock if args.corridor_check is None else args.corridor_check

    print(f"\npipeline: voxel_res={cfg['voxel_res']}  box_res={cfg['box_res']}  pad={cfg['pad']}  "
          f"corridorRadius={cfg['corridor_r']:.4f}  reach_guard={reach_guard}  corridor_check={corridor_check}")
    yparts, autostats = generate_auto_parts(verts, V, F, hps, eqs, cfg)
    a = autostats
    print(f"pipeline: {a['surface']} surface + {a['sealed']} sealed voxels "
          f"({a['hollow']} ship-fits hollow) @res {cfg['voxel_res']} -> "
          f"{a['box_count']} boxes ({a['patched']} fine seal-patches, "
          f"{a['shell_added']} shell-cover boxes, "
          f"{a['dropped']} collapsed/dropped by hull clamp)")
    print(f"pipeline: solid-voxel coverage {a['coverage']*100:.2f}%  "
          f"surface-voxel coverage {a['surface_cov']*100:.2f}%")
    print(f"pipeline: visual sink (world units) — ALL surface: mean={a['sink_all_mean']:.2f} "
          f"p90={a['sink_all_p90']:.2f} max={a['sink_all_max']:.2f}; "
          f"uncovered only: mean={a['sink_unc_mean']:.2f} max={a['sink_unc_max']:.2f}; "
          f"{a['sink_over_1r']*100:.1f}% of surface sinks > 1 ship radius ({WORLD_SHIP_RADIUS:.0f}w)")
    if args.dump is not None:
        write_snapshot(args.dump, yparts, cfg, args.kind, args.primitive, glb, ws)
        print(f"pipeline: wrote snapshot {args.dump}")
    lo, hi = cfg["count_lo"], cfg["count_hi"]
    if not (lo <= len(yparts) <= hi):
        sys.exit(f"ERROR: produced {len(yparts)} parts (expected {lo}..{hi})")

    margin = cfg["margin"]
    corridor_tol = cfg["corridor_tol"]

    # Build each part's convex mesh (deterministic order: sort by name).
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

    # Dock-corridor clearance (auto-gated: only runs when the mesh carries dock hardpoints). The bay
    # doors face INWARD (each entrance sits on a face of the bay box and its +Z forward points toward
    # the bay centre), so the meaningful corridor is the entrance disc itself plus the swept segment
    # from it toward the door centre (the mean of the entrance positions) — and the exit toward that
    # same centre. Every such sample must sit outside all COL parts so no part ever caps a corridor.
    if corridor_check:
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
    else:
        print("\ncorridor check: skipped (no HP_Docking* nodes / disabled).")

    # REACHABILITY GUARD (the regression test for the fly-inside bug), auto-gated per kind. Rasterize
    # the FINAL parts into a fine voxel grid and flood the exterior with the free space eroded by the
    # ship radius: no ship-radius exterior path may reach a sealed-interior cell (a hollow the player
    # could fly into), except inside a carved dock corridor. The pipeline already computed the fine
    # grid + interior hollow + corridor mask; reuse them.
    if reach_guard:
        gfine = autostats["gfine"]
        interior_hollow = autostats["interior_hollow"]
        corridor_fine = autostats["corridor_fine"]
        leaks = reachability_leaks(part_eqs, gfine, interior_hollow, corridor_fine, cfg["ship_r"])
        print(f"\nreachability guard: sealed-interior voxels a ship (r={cfg['ship_r']:.3f} authored) can "
              f"reach from OUTSIDE the FINAL parts (excl. corridors) = {leaks}")
        if leaks > 0:
            print(f"  VIOLATION: {leaks} sealed-interior voxels are ship-reachable — the parts leave a "
                  f"gap a ship can fly through into the station interior.")
            ok = False
        else:
            print("  reachability OK — the interior is sealed against a ship-radius fly-through.")
    else:
        print("\nreachability guard: skipped (disabled for this kind).")

    if not ok:
        sys.exit("\nFAILED validation — not writing GLB.")
    print("\nAll validations PASSED.")

    # Visualizer: --preview writes ONE combined figure to an exact path; --preview-dir writes the
    # reviewer pair (per-stem names). When baking with neither given, default to ./preview. Both work
    # under --check (visualize without writing the GLB).
    stem = glb.stem
    pdir = args.preview_dir
    if pdir is None and args.preview is None and not args.check:
        pdir = HERE / "preview"
    if pdir is not None or args.preview is not None:
        pngs = render_preview(verts, hps, parts, stem, args.kind,
                              preview_dir=pdir, preview_path=args.preview)
        print("preview:", *[str(x) for x in pngs])

    if args.check:
        print("--check: not writing.")
        return 0

    out = args.out or glb
    data = bake_glb(gltf, blob, parts)
    Path(out).write_bytes(data)
    print(f"\nwrote {out}  ({len(data)} bytes)  sha256={hashlib.sha256(data).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
