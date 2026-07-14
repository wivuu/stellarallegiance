#!/usr/bin/env python3
"""collision-hull — generate + bake convex COLLISION-PROXY parts (COL_ nodes) into any mesh GLB.

A visual GLB (a station, a ship, a round asteroid) is ONE welded, concave mesh. Both the server sim
and the Godot client build a single QuickHull "shrink-wrap" over the whole cloud, so ships and bolts
collide with an invisible convex balloon rather than the visible surface. For BASES the runtime reads
a COMPOUND hull instead: one convex hull per COL_ part node this tool appends.

This tool GENERATES those parts straight from the mesh volume — there is no hand-authored spec. It
voxel solid-fills the visual triangles, seals the hollow interior, carves any dock corridors back
open, marching-cubes the carved solid into a watertight surface, decomposes it into convex parts
with CoACD (https://github.com/SarahWeiii/CoACD), clamps each part strictly inside the visual
convex hull, and APPENDS one small triangulated `COL_<name>` mesh node per part. The visual mesh,
its material, and every `HP_` empty are left untouched.

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

Usage (via uv — deps in pyproject.toml; --glb is always required):
  uv run bake.py --kind base --glb PATH             # bake COL_ parts into PATH in place
  uv run bake.py --kind base --glb PATH --check     # validate only, do not write
  uv run bake.py --kind ship --glb PATH --model-length 5.5 --check   # any ship mesh (scale basis)
  uv run bake.py --kind base --glb PATH --preview out.png  # combined visualizer figure
  uv run bake.py --kind base --glb PATH --preview-dir DIR  # ortho + 3D reviewer PNGs into DIR
  uv run bake.py --kind base --glb PATH --dump snap.txt    # provenance snapshot of resolved args

All tunables (--voxel-res --margin --threshold --max-hulls --max-ch-vertex --seed --mc-smooth
--corridor-* --ship-radius --hull-extremes --reach-guard/--no-reach-guard
--corridor-check/--no-corridor-check) default to the kind preset; pass one to override it.
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
WORLD_DOCK_FACE_DEPTH = 9.0   # CollisionConfig.DockFaceDepth (docking-door depth window; lateral
                              # extent is now authored per-door by the 4 boundary markers, so the
                              # corridor width is derived from the door rectangle, not this constant)


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


def read_visual_vertices(gltf, blob, min_extent: float = 0.0) -> tuple[np.ndarray, list]:
    """Return (Nx3 world-space POSITION cloud of every NON-COL mesh, HP records).

    Mirrors shared/Collision/GlbReader.Walk + client GlbLoader.MeshAabb: every non-COL mesh
    primitive's POSITION, transformed by its node world matrix. HP records = (name, pos, fwd).
    `min_extent` > 0 skips primitives whose largest AABB extent is below it (tiny marker /
    placeholder meshes some exporters leave behind) — a guard for FOREIGN preview meshes only;
    it changes the containment hull/AABB, so committed bakes must keep the default 0.
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
            pw = (ph @ w.T)[:, :3]
            if min_extent > 0.0 and len(pw) and float((pw.max(0) - pw.min(0)).max()) < min_extent:
                continue
            verts.append(pw)
    return (np.vstack(verts) if verts else np.zeros((0, 3))), hps


# ---------------------------------------------------------------------------
#  Convex-part construction (points -> triangulated hull)
# ---------------------------------------------------------------------------

def part_vertices(part) -> np.ndarray:
    """Vertex cloud for a generated part: the clamped hull verts in `verts`
    (placed by generate_coacd_parts), fed through convex_mesh downstream."""
    if "verts" in part:
        return np.asarray(part["verts"], dtype=np.float64)
    raise ValueError(f"part {part.get('name')!r} has no verts geometry")


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


def plane_count(eqs: np.ndarray, tol: float = 1e-4) -> int:
    """Distinct geometric PLANES in a hull's equations. scipy emits one row per triangulated
    simplex facet, so coplanar triangles must dedup to reflect the server's per-plane collision
    cost (SphereVsBody is O(planes) per sub-hull — a box is 6, not 12)."""
    return len(np.unique(np.round(eqs / tol).astype(np.int64), axis=0))


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
                   preview_dir: Path | None = None, preview_path: Path | None = None,
                   show: bool = False):
    """Render the visual cloud (grey) + generated COL_ parts (coloured wireframes) for ANY mesh/kind.
    Title + output filenames come from `stem` + `kind`. Every HP_<Kind> hardpoint is drawn with a
    kind-coloured marker + a forward quiver and a legend of the kinds present, but only when the mesh
    has any (a no-op for HP-less meshes like asteroids). Any combination of three sinks:
      * preview_dir → the ortho-triptych PNG + the 3D PNG as <stem>-col-ortho.png / <stem>-col-3d.png
      * preview_path → ONE combined figure (ortho triptych + 3D, 2x2 grid) written to that exact path
      * show=True → the SAME combined figure opened interactively (plt.show(); blocks until closed)
    The combined figure is built by ONE shared builder (`build_combined`) for both file + window.
    Works with --check (visualize without baking). Returns the list of written paths (files only).
    Never perturbs the bake path; a headless/non-interactive backend soft-fails `show` with a hint."""
    import matplotlib
    # Only pin the non-interactive Agg backend when we are NOT opening a window. Agg can render to a
    # file but cannot show one; when `show` is requested we keep matplotlib's default GUI backend
    # (macosx under `uv run` on darwin). savefig() works on either backend, so file sinks compose.
    if not show:
        matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Line3DCollection

    colors = plt.cm.tab10(np.linspace(0, 1, max(10, len(parts))))
    title = f"{stem}.glb visual cloud (grey) + {kind} COL_ parts"
    planes = [(2, 1, "Z", "Y", "side"), (0, 1, "X", "Y", "front"), (2, 0, "Z", "X", "top")]

    # Hardpoints: one kind-coloured marker + a short local-+Z forward quiver per HP, drawn in EVERY
    # sink but only when the mesh defines any (a silent no-op for asteroids / HP-less meshes). Name is
    # HP_<Kind>_<Index>; all Docking* fold into one "Dock" style so the bay keeps its legacy red star.
    # Arrow length tracks the model extent so it reads at any authored scale. hps = (name, pos, fwd),
    # fwd already unit-normalised by read_visual_vertices.
    def _hp_kind(name):
        p = name.split("_")
        k = p[1] if len(p) >= 2 else "HP"
        return "Dock" if k.startswith("Docking") else k

    _HP_KNOWN = {  # kind -> (colour, marker); Dock keeps the legacy red star
        "Dock": ("#ff2d2d", "*"), "Muzzle": ("#ff7f0e", "^"),
        "Nozzle": ("#17d9c8", "v"), "Light": ("#ffd21e", "o"),
    }
    hp_kinds = []
    for n, p, f in hps:
        k = _hp_kind(n)
        if k not in hp_kinds:
            hp_kinds.append(k)
    _hp_extra = [k for k in hp_kinds if k not in _HP_KNOWN]
    _hp_pal = plt.cm.Set2(np.linspace(0, 1, max(1, len(_hp_extra))))
    _hp_mk = ["s", "D", "P", "X", "h", ">", "<", "p", "d", "8"]
    hp_style = dict(_HP_KNOWN)
    for i, k in enumerate(_hp_extra):
        hp_style[k] = (_hp_pal[i % len(_hp_pal)], _hp_mk[i % len(_hp_mk)])
    hp_arrow = 0.12 * float(np.ptp(verts, axis=0).max()) if len(verts) else 1.0
    hp_pos = {k: np.array([p for n, p, f in hps if _hp_kind(n) == k]) for k in hp_kinds}
    hp_dir = {k: np.array([f for n, p, f in hps if _hp_kind(n) == k]) for k in hp_kinds}
    hp_names = {k: [n for n, p, f in hps if _hp_kind(n) == k] for k in hp_kinds}
    hp_label = len(hps) <= 12  # annotate names only when few enough to stay legible in 3D

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
        for kd in hp_kinds:
            col, mk = hp_style[kd]
            P, D = hp_pos[kd], hp_dir[kd]
            ax.scatter(P[:, a], P[:, b], c=[col], marker=mk, s=70, edgecolors="k",
                       linewidths=0.4, zorder=5, label=kd)
            ax.quiver(P[:, a], P[:, b], D[:, a] * hp_arrow, D[:, b] * hp_arrow,
                      angles="xy", scale_units="xy", scale=1, color=col, width=0.004, zorder=4)
        ax.set_xlabel(la); ax.set_ylabel(lb); ax.set_title(ptitle); ax.set_aspect("equal")

    def draw_3d(ax):
        samp = verts[::7]
        ax.scatter(samp[:, 0], samp[:, 1], samp[:, 2], s=1, alpha=0.08, c="0.5", linewidths=0)
        for k, (name, hv, faces) in enumerate(parts):
            ax.add_collection3d(Line3DCollection(edges_of(hv, faces), colors=[colors[k]], lw=0.9))
        for kd in hp_kinds:
            col, mk = hp_style[kd]
            P, D = hp_pos[kd], hp_dir[kd]
            ax.scatter(P[:, 0], P[:, 1], P[:, 2], c=[col], marker=mk, s=60,
                       edgecolors="k", linewidths=0.4, depthshade=False, label=kd)
            ax.quiver(P[:, 0], P[:, 1], P[:, 2], D[:, 0], D[:, 1], D[:, 2],
                      length=hp_arrow, normalize=False, color=col, linewidth=1.0)
            if hp_label:
                for name, pos in zip(hp_names[kd], P):
                    ax.text(pos[0], pos[1], pos[2], name, color=col, fontsize=6)
        if hp_kinds:
            ax.legend(loc="upper left", fontsize=8)
        ax.set_xlabel("X"); ax.set_ylabel("Y"); ax.set_zlabel("Z")
        span = np.ptp(verts, axis=0)
        ax.set_box_aspect((span[0], span[1], span[2]))
        ax.view_init(elev=18, azim=-60)
        ax.set_title(f"{kind} COL_ parts vs visual cloud (3D)")

    def build_combined():
        """The ONE combined 2x2 figure (ortho triptych + 3D) shared by the --preview file sink and the
        interactive --show window. Returns the Figure; caller saves and/or shows it."""
        fig = plt.figure(figsize=(20, 14))
        for i, (a, b, la, lb, pt) in enumerate(planes):
            ax = fig.add_subplot(2, 2, i + 1)
            draw_ortho(ax, a, b, la, lb, pt)
        ax3 = fig.add_subplot(2, 2, 4, projection="3d")
        draw_3d(ax3)
        fig.suptitle(title, fontsize=15); fig.tight_layout()
        return fig

    written = []

    if preview_dir is not None:  # the reviewer pair, per-stem names
        preview_dir.mkdir(parents=True, exist_ok=True)
        fig, axs = plt.subplots(1, 3, figsize=(21, 7))
        for ax, (a, b, la, lb, pt) in zip(axs, planes):
            draw_ortho(ax, a, b, la, lb, pt)
        if hp_kinds:
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
        fig = build_combined()
        fig.savefig(preview_path, dpi=95); plt.close(fig)
        written.append(preview_path)

    if show:  # open the SAME combined figure in an interactive window (blocks until closed)
        backend = matplotlib.get_backend()
        try:
            from matplotlib.rcsetup import interactive_bk
            interactive = backend.lower() in {b.lower() for b in interactive_bk}
        except Exception:
            interactive = backend.lower() not in {
                "agg", "pdf", "svg", "ps", "template", "cairo", "pgf"}
        if not interactive:
            print(f"--show: matplotlib backend '{backend}' is non-interactive (headless / no display "
                  f"available) — cannot open a window. Use --preview PATH.png to write a file you can "
                  f"open instead.", file=sys.stderr)
        else:
            fig = build_combined()
            try:
                plt.show()  # blocks until the human closes the window
            except Exception as e:  # GUI backend present but display unusable — soft-fail, never crash
                print(f"--show: interactive display failed ({e}); use --preview PATH.png instead.",
                      file=sys.stderr)
            finally:
                plt.close(fig)

    return written


# ---------------------------------------------------------------------------
#  Deterministic voxel solid-fill: the CoACD decomposition's input volume
# ---------------------------------------------------------------------------
#
# Hand-eye part placement leaves gaps a ship (radius WORLD_SHIP_RADIUS in authored units) can fly
# THROUGH into the hollow station interior. Coverage is driven off the actual mesh volume instead:
#   1. Voxelize the visual TRIANGLES (robust for the concave, non-watertight shell) at `voxel_res`.
#   2. Flood-fill the EXTERIOR from the grid boundary through free space; every free cell it cannot
#      reach is sealed interior → mark solid. This fills the hollow the player flies around inside.
#      (No outward inflation: a radius-0 boundary flood reaches every exterior cell up to the
#      surface, so only genuinely-enclosed cells are filled.)
#   3. Carve swept-cylinder DOCK CORRIDORS back open so docking/launch approaches stay clear.
#   4. Marching-cubes the carved solid and decompose it into convex parts with CoACD (see the
#      coacd section below).
#   5. Clamp every part strictly inside the visual convex hull (metric-neutrality contract).
#   6. Reachability ASSERT: no ship-radius exterior path may reach a sealed-interior cell through
#      the FINAL part set (except inside a corridor) — the regression guard for this very bug.


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


def read_visual_triangles(gltf, blob, min_extent: float = 0.0) -> tuple[np.ndarray, np.ndarray]:
    """(V Nx3 world float64, F Mx3 int) — every NON-COL indexed mesh primitive's triangles in the
    same world space as read_visual_vertices (same `min_extent` tiny-marker skip). Keeps the index
    buffer so the voxelizer rasterizes real triangle SURFACES (robust for a concave,
    non-watertight shell), not just the vert cloud."""
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
            if min_extent > 0.0 and len(pw) and float((pw.max(0) - pw.min(0)).max()) < min_extent:
                continue
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


def _hp_index(name: str) -> int:
    """Trailing integer index of an 'HP_..._<Index>' node name; 0 if absent (stable sort key)."""
    tail = name.rsplit("_", 1)[-1]
    try:
        return int(tail)
    except ValueError:
        return 0


def _face_marker(group):
    """Index (within `group` of 5 (idx, pos, fwd)) of the FACE marker, detected by ORIENTATION:
    rim markers' forwards point outward along the face plane (straight at/away from a sibling),
    the face marker's forward is perpendicular to the whole spread. Score = worst |cos| against
    any sibling direction; strictly-lowest wins, ties fall to the lowest index (legacy art where
    all five share the face normal → old first-is-face rule). Mirrors DockFaceParser.FaceMarker."""
    best, best_score = 0, float("inf")
    for i, (_, pi, fi) in enumerate(group):
        ln = float(np.linalg.norm(fi))
        if ln < 1e-6:
            continue  # degenerate forward can't be scored — never the face marker
        f = fi / ln
        score = 0.0
        for j, (_, pj, _) in enumerate(group):
            if j == i:
                continue
            d = pj - pi
            dl = float(np.linalg.norm(d))
            if dl < 1e-6:
                continue  # coincident markers carry no direction
            score = max(score, abs(float(f @ d) / dl))
        if score < best_score:
            best, best_score = i, score
    return best


def dock_doors(hps):
    """Group HP_DockingEntrance_* markers (sorted by trailing index) into docking DOORS of FIVE:
    ONE marker of each group is the face (found by _face_marker, NOT assumed first) — its position
    is the face centre and its forward (local +Z) is the INWARD normal (the direction a ship
    travels entering); the OTHER FOUR mark the rectangle boundary. Returns
    [(face_pos, inward_normal, half_diagonal)] per full group (authored mesh units). Mirrors
    shared/Collision/DockFace.cs DockFaceParser — KEEP IN SYNC: the bake carves corridors from the
    same door geometry the sim/client dock against. Leftover (<5) markers are ignored here (the
    sim treats them as legacy discs; a corridor for them isn't worth the risk)."""
    ent = [(_hp_index(n), np.asarray(p, float), np.asarray(f, float))
           for n, p, f in hps if "Entrance" in n]
    ent.sort(key=lambda t: t[0])
    doors = []
    for g in range(len(ent) // 5):
        group = ent[g * 5 : g * 5 + 5]
        face = _face_marker(group)
        _, pos, fwd = group[face]
        n = fwd / (np.linalg.norm(fwd) or 1.0)
        proj = []
        for k in range(5):
            if k == face:
                continue
            rel = group[k][1] - pos
            proj.append(rel - n * float(rel @ n))  # onto the face plane
        u = None
        for pr in proj:
            if float(pr @ pr) > 1e-8:
                u = pr / np.linalg.norm(pr)
                break
        if u is None:
            u = np.array([1.0, 0.0, 0.0])
        v = np.cross(n, u)
        v = v / (np.linalg.norm(v) or 1.0)
        eu = max(abs(float(pr @ u)) for pr in proj) if proj else 0.0
        ev = max(abs(float(pr @ v)) for pr in proj) if proj else 0.0
        doors.append((pos, n, float(np.hypot(eu, ev))))  # half-diagonal covers the corners
    return doors


def corridor_segments(hps, corridor_r: float, approach: float, ship_r: float = 0.0):
    """Swept-cylinder DOCK CORRIDORS that must stay open (never solid). Under the GROUPED-door
    convention each door is a rectangle (dock_doors): we carve one cylinder per door from
    `approach` units OUTSIDE the face (opposite the inward normal) to the face centre, widened
    PER DOOR to that door's half-diagonal + a ship radius (floored at `corridor_r`, the
    ship-clearance minimum) so no COL part can cap a corner a ship may legally dock through —
    but a big door never inflates the carve at a small one (a single global max radius once ate
    60% of a two-door station's collision). Each HP_DockingExit catapults ships outward along
    normalize(exitPos): sweep from the exit point out along that axis only — never from the door
    reference across the body (with far-apart doors that carved straight through the hub). Falls
    back to the legacy mean-of-entrances sweep for a non-grouped asset. Returns [(a, b, radius)]."""
    ext = [np.asarray(p, float) for n, p, f in hps if "Exit" in n]

    def radial(p):
        l = float(np.linalg.norm(p))
        return p / l if l > 1e-6 else np.array([0.0, 0.0, 1.0])

    doors = dock_doors(hps)
    if not doors:
        ent = [np.asarray(p, float) for n, p, f in hps if "Entrance" in n]
        if not ent:
            return []
        door = np.mean(ent, axis=0)
        segs = [(e + radial(e) * approach, door, corridor_r) for e in ent]
        for x in ext:
            segs.append((door, x + radial(x) * approach, corridor_r))
        return segs

    segs = []
    for pos, n, hd in doors:
        segs.append((pos - n * approach, pos, max(corridor_r, hd + ship_r)))  # approach → face
    for x in ext:
        segs.append((x, x + radial(x) * approach, corridor_r))  # exit launch ray, outward only
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


# Per-kind pipeline presets — the resolution baseline for every knob. A no-override `--kind base`
# bake of an unchanged GLB MUST reproduce the committed bytes (the determinism contract), so these
# are a hard contract, not a default to tweak. `ship` shares the coverage knobs but tightens the
# CoACD threshold (finer parts on small meshes), widens the part-count window, and leaves the reach
# guard / corridor validator off (turned on by --kind auto-detection or an explicit flag).
# ship_radius / corridor_radius are filled from the world constants via `ws` below.
KIND_PRESETS = {
    "base": dict(voxel_res=0.5, margin=0.05, corridor_tol=0.05,
                 corridor_clearance=0.5, corridor_approach=8.0,
                 count_lo=2, count_hi=1024, threshold=0.1),
    "ship": dict(voxel_res=0.5, margin=0.05, corridor_tol=0.05,
                 corridor_clearance=0.5, corridor_approach=8.0,
                 count_lo=1, count_hi=100000, threshold=0.05),
}


def resolve_cfg(args, kind: str, ws: float) -> dict:
    """Resolve the pipeline knobs from the kind preset, overridden by any explicit CLI arg (every
    tunable defaults to None so 'unset' is distinguishable from a passed value). Authored-unit
    thresholds fall back to the world collision constants via world-scale `ws`, exactly as the
    sim/client derive them at load. Returns the cfg dict shape generate_coacd_parts consumes,
    plus the per-kind count window + the resolved corridor_tol / hull_extremes the validators read."""
    p = KIND_PRESETS[kind]

    def pick(name, default):
        v = getattr(args, name, None)
        return default if v is None else v

    clearance = pick("corridor_clearance", p["corridor_clearance"])
    ship_r = pick("ship_radius", WORLD_SHIP_RADIUS / ws)
    # The corridor-width FLOOR (ship radius + clearance). Each door's carve segment is widened to
    # that door's own rectangle half-diagonal + ship radius in corridor_segments — the lateral
    # extent is authored by the 4 boundary markers, not a global disc-radius constant.
    corridor_r = pick("corridor_radius", ship_r + clearance)
    return dict(
        voxel_res=float(pick("voxel_res", p["voxel_res"])),
        margin=float(pick("margin", p["margin"])),
        ship_r=float(ship_r),
        corridor_r=float(corridor_r),
        corridor_approach=float(pick("corridor_approach", p["corridor_approach"])),
        corridor_tol=float(pick("corridor_tol", p["corridor_tol"])),
        hull_extremes=int(pick("hull_extremes", 0)),
        count_lo=p["count_lo"],
        count_hi=p["count_hi"],
        threshold=float(pick("threshold", p["threshold"])),
        max_hulls=int(pick("max_hulls", -1)),
        max_ch_vertex=int(pick("max_ch_vertex", 64)),
        seed=int(pick("seed", 0)),
        mc_smooth=float(pick("mc_smooth", 1.0)),
    )


# ---------------------------------------------------------------------------
#  CoACD convex decomposition of the carved voxel solid
# ---------------------------------------------------------------------------
#
# CoACD (https://github.com/SarahWeiii/CoACD) splits a watertight mesh into convex parts that hug
# concave detail far more tightly than fitted boxes. It is NOT run on the raw visual mesh: that
# would hug the hangar walls and leave the sealed interior HOLLOW (the fly-inside bug the
# reachability guard exists to catch). Instead it decomposes the SAME carved voxel solid the box
# path merges — interior sealed by classify_solid, dock corridors carved back open by
# corridor_mask — turned into a watertight surface by marching cubes. Each resulting hull is then
# clamped strictly inside the visual hull (the metric-neutrality contract), so all downstream
# validation/bake machinery is shared unchanged.

def _chebyshev_center(halfspaces: np.ndarray):
    """Deepest interior point (Chebyshev centre) of {x | n_i.x + d_i <= 0}, or None when the
    intersection is empty/degenerate. Rows use the scipy hull.equations [n | d] convention."""
    from scipy.optimize import linprog

    A, b = halfspaces[:, :3], halfspaces[:, 3]
    norms = np.linalg.norm(A, axis=1, keepdims=True)
    res = linprog(np.array([0.0, 0.0, 0.0, -1.0]),
                  A_ub=np.hstack([A, norms]), b_ub=-b,
                  bounds=[(None, None)] * 3 + [(0.0, None)], method="highs")
    if not res.success or res.x[3] <= 1e-9:
        return None
    return res.x[:3]


def clamp_part_to_hull(pv: np.ndarray, eqs: np.ndarray, margin: float, max_verts: int):
    """Intersect the part's halfspaces with the visual-hull planes offset INWARD by margin and
    return the intersection's hull vertices (or None if the part collapses). Every returned
    vertex sits >= margin inside the visual hull —
    the metric-neutrality contract — with a small epsilon of headroom so the float32 quantization
    in the baked GLB cannot tip a vertex back over the validator's exact -margin threshold."""
    from scipy.spatial import HalfspaceIntersection, QhullError

    pv = np.asarray(pv, dtype=np.float64)
    eps = 1e-3
    try:
        if signed_dist_to_hull(pv, eqs).max() <= -(margin + eps):
            pts = pv  # already strictly inside — keep CoACD's own hull verts
        else:
            inner = eqs.copy()
            inner[:, 3] += margin + eps
            hs = np.vstack([hull_equations(pv), inner])
            c = _chebyshev_center(hs)
            if c is None:
                return None
            pts = HalfspaceIntersection(hs, c).intersections
        if max_verts > 0 and len(pts) > max_verts:
            pts = reduce_to_extremes(pts, max_verts)
        hull = ConvexHull(pts)
        return pts[np.unique(hull.simplices)]
    except (QhullError, ValueError):
        return None


def generate_coacd_parts(verts, V, F, hps, eqs, cfg):
    """CoACD pipeline; returns (yparts, stats) with parts in the raw-verts schema
    [{'name': 'CoACD000', 'verts': hull_verts}]. Reuses the box path's fine voxel stage (solid
    fill + seal + corridor carve) and reports the same coverage/sink stats so the generators
    compare like-for-like."""
    import coacd
    from scipy.ndimage import distance_transform_edt as _edt
    from skimage.measure import marching_cubes

    ship_r = cfg["ship_r"]
    voxel_res = cfg["voxel_res"]
    margin = cfg["margin"]
    segs = corridor_segments(hps, cfg["corridor_r"], cfg["corridor_approach"], ship_r)

    gfine = grid_for(V, voxel_res)
    Sfine = voxelize_surface(V, F, gfine)
    solid_fine, sealed_fine, _ = classify_solid(Sfine)
    corridor_fine = corridor_mask(gfine, segs)
    interior_hollow = sealed_fine & ((_edt(~Sfine) * gfine.res) > ship_r)

    carved = solid_fine & ~corridor_fine
    if not carved.any():
        sys.exit("ERROR: carved voxel solid is empty — nothing to decompose")

    # Watertight input surface: marching-cubes the carved solid. Sample (i,j,k) is the CELL CENTRE
    # at origin + (ijk + 0.5)*res, and grid_for pads 3 empty cells on every side so the isosurface
    # always closes inside the grid. The binary volume is gaussian-smoothed first (sigma in CELLS,
    # --mc-smooth): the raw voxel STAIRCASE reads as concavity to CoACD, which then shatters curved
    # or diagonal geometry into hundreds of thin crust plates that the hull clamp can only drop
    # (measured: 367 parts for a plain sphere). Smoothing restores the true surface shape; walls or
    # corridors thinner than ~2*sigma cells can blur away, which the reachability/corridor
    # validators catch — lower --mc-smooth if that happens.
    from scipy.ndimage import gaussian_filter
    vol = carved.astype(np.float32)
    if cfg["mc_smooth"] > 0.0:
        vol = gaussian_filter(vol, sigma=cfg["mc_smooth"])
        if vol.max() <= 0.5:
            sys.exit("ERROR: --mc-smooth blurred the entire solid below the isosurface level — "
                     "the mesh is too thin for this sigma; lower --mc-smooth")
    mv, mf, _, _ = marching_cubes(vol, level=0.5, spacing=(gfine.res,) * 3)
    mv = mv + (gfine.origin + 0.5 * gfine.res)
    # skimage's marching_cubes winds faces with normals pointing INTO the solid for this
    # inside=1 volume — inside-out for CoACD, whose concavity metric then explodes (measured:
    # a plain sphere shatters into 267 crust plates instead of 1 hull). Flip the winding.
    mf = mf[:, ::-1]

    coacd.set_log_level("error")
    cmesh = coacd.Mesh(mv.astype(np.float64), mf.astype(np.int64))
    kwargs = dict(threshold=cfg["threshold"], max_convex_hull=cfg["max_hulls"],
                  max_ch_vertex=cfg["max_ch_vertex"], seed=cfg["seed"])
    try:
        raw = coacd.run_coacd(cmesh, preprocess_mode="off", **kwargs)
    except Exception as e:  # marching-cubes output CoACD won't take as-is — let it remesh itself
        print(f"pipeline: CoACD preprocess=off failed ({e}); retrying with preprocess=auto")
        raw = coacd.run_coacd(cmesh, preprocess_mode="auto", **kwargs)

    # Clamp every part strictly inside the visual hull (collapsed parts drop), then order
    # deterministically by rounded centroid — CoACD's output order is seed-stable but opaque.
    clamped, dropped = [], 0
    for pv, pf in raw:
        hv = clamp_part_to_hull(pv, eqs, margin, cfg["max_ch_vertex"])
        if hv is None or len(hv) < 4:
            dropped += 1
            continue
        clamped.append(hv)
    clamped.sort(key=lambda h: tuple(np.round(h.mean(0), 4)))
    yparts = [{"name": f"CoACD{i:03d}", "verts": h} for i, h in enumerate(clamped)]

    # Coverage + visual-sink metrics: rasterize the FINAL clamped parts on the fine grid, measure
    # carved-solid and surface-voxel coverage plus the world-unit distance a ship sinks past the
    # visible skin before it bounces (sink_all = over ALL surface voxels, the typical-case feel;
    # sink_unc = over only the still-uncovered voxels, the residual worst case — it conditions on
    # the hardest voxels, so read it alongside surface_cov, not alone).
    part_eqs_list = [hull_equations(h) for h in clamped]
    solid_final = rasterize_parts(part_eqs_list, gfine)
    coverage = float((carved & solid_final).sum() / carved.sum()) if carved.any() else 1.0
    ws = world_scale(verts)  # authored -> world units, same derivation the sim/client use
    surf_cov = float((Sfine & solid_final).sum() / Sfine.sum()) if Sfine.any() else 1.0
    sink_world = _edt(~solid_final) * gfine.res * ws
    sink_all = sink_world[Sfine]
    sink_unc = sink_world[Sfine & ~solid_final]

    stats = dict(
        gfine=gfine, interior_hollow=interior_hollow, corridor_fine=corridor_fine,
        ship_r=ship_r, dropped=dropped, part_count=len(yparts),
        mc_verts=len(mv), mc_faces=len(mf), raw_parts=len(raw),
        coverage=coverage, surface_cov=surf_cov, solid_fine=int(solid_fine.sum()),
        sealed=int(sealed_fine.sum()), hollow=int(interior_hollow.sum()), surface=int(Sfine.sum()),
        sink_all_mean=float(sink_all.mean()) if sink_all.size else 0.0,
        sink_all_p90=float(np.percentile(sink_all, 90)) if sink_all.size else 0.0,
        sink_all_max=float(sink_all.max()) if sink_all.size else 0.0,
        sink_unc_mean=float(sink_unc.mean()) if sink_unc.size else 0.0,
        sink_unc_max=float(sink_unc.max()) if sink_unc.size else 0.0,
        sink_over_1r=float((sink_all > WORLD_SHIP_RADIUS).mean()) if sink_all.size else 0.0,
        voxel_res=voxel_res, segs=segs,
    )
    return yparts, stats


def write_snapshot(path: Path, yparts, cfg, kind: str, glb: Path, ws: float):
    """Opt-in (`--dump PATH`) human-readable record of a bake: the kind, the source GLB, every
    resolved arg, and the baked part list. Replaces the retired base-col.generated.yaml — NOT consumed
    by the bake (which regenerates from the mesh every run for determinism), purely provenance so a
    reviewer can see exactly what a given SHA was baked from. Manual string formatting, no yaml lib."""
    lines = [
        f"# collision-hull snapshot — GENERATED by `bake.py --kind {kind}`; provenance only, not consumed.",
        "# A record of the deterministic voxel solid-fill + CoACD decomposition output for a bake.",
        f"# kind={kind}  glb={glb}  worldScale={ws:.6f}",
        f"# voxel_res={cfg['voxel_res']}  margin={cfg['margin']}  hull_extremes={cfg['hull_extremes']}  "
        f"corridor_tol={cfg['corridor_tol']}",
        f"# ship_radius(authored)={cfg['ship_r']:.4f}  corridor_radius={cfg['corridor_r']:.4f}  "
        f"corridor_approach={cfg['corridor_approach']}",
        f"# coacd: threshold={cfg['threshold']}  max_hulls={cfg['max_hulls']}  "
        f"max_ch_vertex={cfg['max_ch_vertex']}  seed={cfg['seed']}  mc_smooth={cfg['mc_smooth']}",
        f"# {len(yparts)} parts",
        "parts:",
    ]
    for p in yparts:
        hv = np.asarray(p["verts"], dtype=np.float64)
        c = hv.mean(0)
        lo, hi = hv.min(0), hv.max(0)
        lines.append(f"  - name: {p['name']}")
        lines.append(f"    hull: {{verts: {len(hv)}, centroid: [{c[0]:.4f}, {c[1]:.4f}, {c[2]:.4f}], "
                     f"aabb: [[{lo[0]:.4f}, {lo[1]:.4f}, {lo[2]:.4f}], "
                     f"[{hi[0]:.4f}, {hi[1]:.4f}, {hi[2]:.4f}]]}}")
    Path(path).write_text("\n".join(lines) + "\n")


# ---------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description="Generate + bake convex COL_ collision parts into a mesh GLB")
    ap.add_argument("--kind", choices=["base", "ship"], required=True,
                    help="preset selector: base = station (corridors + reach guard); "
                         "ship = any ship/model mesh (guard/corridors off unless present)")
    ap.add_argument("--glb", type=Path, required=True, help="source mesh GLB to validate/bake")
    ap.add_argument("--out", type=Path, default=None, help="default: rewrite --glb in place")
    ap.add_argument("--check", action="store_true", help="validate only; do not write")
    ap.add_argument("--dump", type=Path, default=None,
                    help="write a human-readable provenance snapshot (kind + resolved args + parts) to PATH")
    ap.add_argument("--preview", type=Path, default=None,
                    help="render ONE combined figure (ortho triptych + 3D) to this exact PNG path")
    ap.add_argument("--preview-dir", type=Path, default=None,
                    help="render the ortho + 3D reviewer PNGs into DIR as <stem>-col-ortho/-3d.png")
    ap.add_argument("--show", action="store_true",
                    help="open the combined figure in an interactive window (rotate/zoom the 3D view); "
                         "blocks until closed. Composes with --check/--preview; soft-fails if headless")
    # --- scale basis (authored mesh units -> world units) ---
    ap.add_argument("--world-diameter", type=float, default=180.0,
                    help="base scale basis (CollisionConfig.BaseRadius*2); ws = world_diameter/LongestAxis")
    ap.add_argument("--model-length", type=float, default=None,
                    help="ship scale basis (REQUIRED for --kind ship); ws = model_length/LongestAxis")
    # --- pipeline knobs: all default None so 'unset' falls through to the kind preset ---
    ap.add_argument("--voxel-res", type=float, default=None)
    ap.add_argument("--margin", type=float, default=None)
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
    # --- CoACD knobs ---
    ap.add_argument("--threshold", type=float, default=None,
                    help="CoACD concavity tolerance (preset base 0.1 / ship 0.05; "
                         "lower = more, tighter-fitting parts)")
    ap.add_argument("--max-hulls", type=int, default=None,
                    help="CoACD max_convex_hull cap (default -1 = unlimited)")
    ap.add_argument("--max-ch-vertex", type=int, default=None,
                    help="max verts per CoACD hull (default 64 — server SphereVsBody is O(planes) "
                         "per sub-hull, so CoACD's native 256 is far too fat)")
    ap.add_argument("--seed", type=int, default=None,
                    help="CoACD RNG seed (default 0, fixed for determinism)")
    ap.add_argument("--mc-smooth", type=float, default=None,
                    help="gaussian sigma (in voxel CELLS, default 1.0; 0 = off) applied to the "
                         "carved solid before marching cubes — kills the voxel staircase that "
                         "CoACD reads as concavity; walls thinner than ~2*sigma cells may blur "
                         "away (the validators catch it)")
    ap.add_argument("--min-extent", type=float, default=0.0,
                    help="skip visual mesh primitives whose largest AABB extent is below this "
                         "(authored units) — guards FOREIGN preview meshes with tiny marker/"
                         "placeholder prims; changes the containment hull, so committed bakes "
                         "must keep the default 0")
    args = ap.parse_args(argv)

    # Scale-basis resolution per kind.
    glb = args.glb
    if args.kind == "ship" and args.model_length is None:
        sys.exit("ERROR: --model-length is REQUIRED for --kind ship (ws = model_length/LongestAxis)")

    gltf = pygltflib.GLTF2().load(str(glb))
    blob = gltf.binary_blob()
    verts, hps = read_visual_vertices(gltf, blob, args.min_extent)
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
    V, F = read_visual_triangles(gltf, blob, args.min_extent)
    if not len(F):
        sys.exit(f"ERROR: {glb} has no indexed triangles (unindexed prims are skipped) — nothing to voxelize")

    # Resolve the two auto-gated validators. reach-guard: base on / ship off unless overridden.
    # corridor-check: auto = on iff the mesh actually carries dock hardpoints (self-gates for ships).
    has_dock = any((n or "").startswith("HP_Docking") for n, p, f in hps)
    reach_guard = (args.kind == "base") if args.reach_guard is None else args.reach_guard
    corridor_check = has_dock if args.corridor_check is None else args.corridor_check

    print(f"\npipeline: voxel_res={cfg['voxel_res']}  corridorRadius={cfg['corridor_r']:.4f}  "
          f"reach_guard={reach_guard}  corridor_check={corridor_check}")
    yparts, autostats = generate_coacd_parts(verts, V, F, hps, eqs, cfg)
    a = autostats
    print(f"pipeline: {a['surface']} surface + {a['sealed']} sealed voxels "
          f"({a['hollow']} ship-fits hollow) @res {cfg['voxel_res']} -> "
          f"marching-cubes {a['mc_verts']}v/{a['mc_faces']}f -> "
          f"{a['raw_parts']} CoACD parts (threshold={cfg['threshold']}, "
          f"max_ch_vertex={cfg['max_ch_vertex']}, seed={cfg['seed']}, "
          f"mc_smooth={cfg['mc_smooth']}) -> "
          f"{a['part_count']} clamped parts ({a['dropped']} collapsed/dropped by hull clamp)")
    print(f"pipeline: solid-voxel coverage {a['coverage']*100:.2f}%  "
          f"surface-voxel coverage {a['surface_cov']*100:.2f}%")
    print(f"pipeline: visual sink (world units) — ALL surface: mean={a['sink_all_mean']:.2f} "
          f"p90={a['sink_all_p90']:.2f} max={a['sink_all_max']:.2f}; "
          f"uncovered only: mean={a['sink_unc_mean']:.2f} max={a['sink_unc_max']:.2f}; "
          f"{a['sink_over_1r']*100:.1f}% of surface sinks > 1 ship radius ({WORLD_SHIP_RADIUS:.0f}w)")
    if args.dump is not None:
        write_snapshot(args.dump, yparts, cfg, args.kind, glb, ws)
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
    total_planes = 0
    print(f"\n{'part':14} {'verts':>5} {'planes':>6} {'AABB (min..max)':>34}  {'hull-margin':>11}")
    ok = True
    for p in yparts:
        name = p["name"]
        raw = part_vertices(p)
        hv, faces, _ = convex_mesh(raw)
        parts.append((name, hv, faces))
        all_col.append(hv)
        peq = hull_equations(hv)
        part_eqs.append(peq)
        nplanes = plane_count(peq)
        total_planes += nplanes
        d = signed_dist_to_hull(hv.astype(np.float64), eqs)
        worst = d.max()  # want <= -margin
        plo, phi = hv.min(0), hv.max(0)
        flag = "" if worst <= -margin else "  <-- VIOLATION (pokes out of visual hull)"
        if worst > -margin:
            ok = False
        print(f"{name:14} {len(hv):5d} {nplanes:6d}  [{plo[0]:5.1f},{plo[1]:5.1f},{plo[2]:5.1f}]"
              f"..[{phi[0]:5.1f},{phi[1]:5.1f},{phi[2]:5.1f}]  {(-worst):8.4f}{flag}")
    print(f"{'TOTAL':14} {sum(len(hv) for _, hv, _ in parts):5d} {total_planes:6d}  "
          f"({len(parts)} parts; server SphereVsBody cost scales with total planes)")

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
        doors = dock_doors(hps)
        ext = [(n, p, f) for n, p, f in hps if "Exit" in n]
        approach = cfg["corridor_approach"]
        print(f"\ndock doors parsed: {len(doors)}; corridor samples must stay OUTSIDE all parts")
        corridor_fail = 0
        # Per DOOR (a base may author N): walk the inward-normal approach axis straight to the face
        # centre — the exact ray the server SelfTest fires. SelfTest probes from BaseRadius*2 world
        # (= longestAxis authored) outside the face, so walk that FULL lane, not just the carved
        # `approach` stretch: structure crossing the lane beyond the carve blocks docking just the
        # same (a two-door hub failed exactly this way). We also probe a small in-plane cross at
        # half the door's half-diagonal (well inside the rectangle) to catch a part that laterally
        # pinches the doorway without blocking the centre axis.
        lane = max(approach, longest)  # authored units == the server's BaseRadius*2-world probe
        for di, (pos, n, hd) in enumerate(doors):
            seed = np.array([0.0, 1.0, 0.0]) if abs(n[1]) < 0.9 else np.array([1.0, 0.0, 0.0])
            u = np.cross(n, seed); u /= (np.linalg.norm(u) or 1.0)
            v = np.cross(n, u); v /= (np.linalg.norm(v) or 1.0)
            samples = []
            for t in np.arange(0.0, lane, cfg["voxel_res"] * 0.5):
                samples.append(pos - n * t)  # face centre -> outside, sampled at half-voxel steps
            lat = hd * 0.5  # inside the rectangle (hd is the corner radius) for a lateral pinch probe
            for s in (u, -u, v, -v):
                samples.append(pos + s * lat)
            for s in samples:
                for name, peq in zip([x[0] for x in parts], part_eqs):
                    if point_inside_part(np.asarray(s, float), peq, corridor_tol):
                        print(f"  VIOLATION: door {di} corridor point {np.round(s,2)} is inside part {name}")
                        corridor_fail += 1
        # Exit radial rays (launch mouth): from the exit hardpoint out along normalize(pos).
        for n_, p_, f_ in ext:
            l = float(np.linalg.norm(p_)); rad = p_ / l if l > 1e-6 else np.array([0.0, 0.0, 1.0])
            for t in np.linspace(0.0, 1.0, 9):
                s = p_ + rad * approach * t
                for name, peq in zip([x[0] for x in parts], part_eqs):
                    if point_inside_part(np.asarray(s, float), peq, corridor_tol):
                        print(f"  VIOLATION: {n_} exit point {np.round(s,2)} is inside part {name}")
                        corridor_fail += 1
        if corridor_fail == 0:
            print(f"  corridor clearance OK ({len(doors)} doors + {len(ext)} exit swept)")
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
    if pdir is not None or args.preview is not None or args.show:
        pngs = render_preview(verts, hps, parts, stem, args.kind,
                              preview_dir=pdir, preview_path=args.preview, show=args.show)
        if pngs:
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
