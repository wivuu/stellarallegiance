#!/usr/bin/env python3
"""Experiment: run CoACD (https://github.com/SarahWeiii/CoACD) convex decomposition on a
Godot-ready ship/base GLB and preview the resulting convex parts against the source mesh.

Usage:
    uv run run_coacd.py [GLB_PATH] [--save PATH.png] [--no-show] [--threshold 0.05]

GLB_PATH defaults to ../../pick-assets/ss27.glb (repo-relative) when omitted; pass any other GLB
positionally (or via --glb) to decompose it instead. Merges every glTF primitive whose extent
clears --min-extent (skips tiny marker/placeholder primitives that ship-gen sometimes leaves
behind, e.g. HP_ node meshes) into one watertight-ish mesh, decomposes it with CoACD, then
renders the source mesh in translucent grey with each convex hull part overlaid in its own
colour. --save/the plot title default to the input GLB's filename stem.
"""
import argparse
from pathlib import Path

import coacd
import numpy as np
import trimesh


def load_merged_hull_mesh(glb_path, min_extent=1.0):
    scene = trimesh.load(glb_path)
    if not isinstance(scene, trimesh.Scene):
        return scene

    parts = []
    for name, geom in scene.geometry.items():
        if geom.extents.max() < min_extent:
            print(f"  skip {name}: extent {geom.extents.max():.4f} < {min_extent} (marker/placeholder mesh)")
            continue
        node_name = next(n for n in scene.graph.nodes_geometry if scene.graph[n][1] == name)
        transform = scene.graph[node_name][0]
        g = geom.copy()
        g.apply_transform(transform)
        print(f"  include {name}: {len(g.vertices)} verts, {len(g.faces)} faces")
        parts.append(g)

    return trimesh.util.concatenate(parts)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("glb_pos", nargs="?", type=Path, default=None, metavar="GLB_PATH",
                    help="source GLB, positional (default: pick-assets/ss27.glb)")
    ap.add_argument("--glb", type=Path, default=None, help="source GLB (alternative to positional GLB_PATH)")
    ap.add_argument("--save", type=Path, default=None, help="preview PNG path (default: preview/<glb-stem>-coacd.png)")
    ap.add_argument("--no-show", action="store_true", help="skip the interactive window (save only)")
    ap.add_argument("--threshold", type=float, default=0.05, help="CoACD concavity threshold (lower = more parts)")
    ap.add_argument("--max-hulls", type=int, default=-1, help="CoACD max_convex_hull (-1 = unlimited)")
    ap.add_argument("--min-extent", type=float, default=1.0,
                    help="skip glTF primitives whose largest extent is below this (marker/placeholder meshes)")
    args = ap.parse_args()

    glb_path = args.glb_pos or args.glb or (Path(__file__).parent.parent.parent / "pick-assets/ss27.glb")
    save_path = args.save or (Path(__file__).parent / "preview" / f"{glb_path.stem}-coacd.png")
    print(f"loading {glb_path}")
    mesh = load_merged_hull_mesh(glb_path, min_extent=args.min_extent)
    print(f"source mesh: {len(mesh.vertices)} vertices, {len(mesh.faces)} polygons (triangles), "
          f"extents={mesh.extents}")

    coacd.set_log_level("info")
    cd_mesh = coacd.Mesh(mesh.vertices, mesh.faces)
    parts = coacd.run_coacd(cd_mesh, threshold=args.threshold, max_convex_hull=args.max_hulls)
    print(f"CoACD produced {len(parts)} convex parts")
    for i, (pv, pf) in enumerate(parts):
        part_mesh = trimesh.Trimesh(vertices=pv, faces=pf, process=False)
        print(f"  part {i}: {len(pv)} verts, {len(pf)} faces, volume={part_mesh.volume:.2f}")

    preview(mesh, parts, glb_path.stem, save_path, args.no_show)
    report(mesh, parts)


def report(mesh, parts):
    src_verts, src_faces = len(mesh.vertices), len(mesh.faces)
    col_verts = sum(len(pv) for pv, pf in parts)
    col_faces = sum(len(pf) for pv, pf in parts)

    def _compare(label, src, col):
        if col <= src:
            print(f"  {label}: {col} vs source {src}  ->  {src / col:.2f}x SIMPLER")
        else:
            print(f"  {label}: {col} vs source {src}  ->  {col / src:.2f}x MORE than source")

    print()
    print("=== Collision mesh vs. source mesh ===")
    print(f"  convex parts: {len(parts)} (source is 1 concave mesh)")
    _compare("vertices", src_verts, col_verts)
    _compare("faces", src_faces, col_faces)
    print("  Note: CoACD parts are re-triangulated per-hull, so totals often exceed the source "
          "mesh's polycount even though each individual part is convex. The simplification that "
          "matters for physics is per-part (a physics engine tests against N convex primitives, "
          "never the full concave source mesh), not raw triangle count.")


def preview(mesh, parts, name, save_path, no_show):
    import matplotlib
    if no_show:
        matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Poly3DCollection

    fig = plt.figure(figsize=(14, 11))
    ax = fig.add_subplot(111, projection="3d")
    # Keep the default computed_zorder=True (per-collection centroid depth sort). Splitting the
    # base mesh + each convex part into separate Poly3DCollections and disabling computed_zorder
    # (as mesh_only_view.py does to keep text labels on top) falls back to draw-order, which is
    # wrong here — many parts interpenetrate in depth. A single combined Poly3DCollection lets
    # mplot3d depth-sort every triangle together instead of per-collection, avoiding z-fighting
    # between overlapping convex hulls.
    all_tris = [mesh.vertices[mesh.faces]]
    all_face = [np.tile(np.array([0.54, 0.565, 0.6, 0.25]), (len(mesh.faces), 1))]
    all_edge = [np.tile(np.array([0.0, 0.0, 0.0, 0.0]), (len(mesh.faces), 1))]

    cmap = plt.cm.tab20(np.linspace(0, 1, max(1, len(parts))))
    for i, (pv, pf) in enumerate(parts):
        ptris = np.asarray(pv)[np.asarray(pf)]
        color = cmap[i % len(cmap)]
        all_tris.append(ptris)
        all_face.append(np.tile(np.array([*color[:3], 0.55]), (len(pf), 1)))
        all_edge.append(np.tile(np.array([0.1, 0.1, 0.1, 0.6]), (len(pf), 1)))

    coll = Poly3DCollection(np.concatenate(all_tris), linewidths=0.3)
    coll.set_facecolor(np.concatenate(all_face))
    coll.set_edgecolor(np.concatenate(all_edge))
    ax.add_collection3d(coll)

    V = mesh.vertices
    span = np.ptp(V, axis=0)
    ax.set_box_aspect((span[0], span[1], span[2]))
    lo, hi = V.min(axis=0), V.max(axis=0)
    ax.set_xlim(lo[0], hi[0]); ax.set_ylim(lo[1], hi[1]); ax.set_zlim(lo[2], hi[2])
    ax.set_xlabel("X"); ax.set_ylabel("Y"); ax.set_zlabel("Z")
    ax.view_init(elev=18, azim=-60)
    ax.set_title(f"{name}: CoACD convex decomposition ({len(parts)} parts)")

    def _on_scroll(event):
        factor = 0.9 if event.button == "up" else 1.1
        for get_lim, set_lim in ((ax.get_xlim3d, ax.set_xlim3d),
                                  (ax.get_ylim3d, ax.set_ylim3d),
                                  (ax.get_zlim3d, ax.set_zlim3d)):
            lo, hi = get_lim()
            mid = (lo + hi) / 2
            half = (hi - lo) / 2 * factor
            set_lim(mid - half, mid + half)
        fig.canvas.draw_idle()

    fig.canvas.mpl_connect("scroll_event", _on_scroll)

    if save_path:
        save_path.parent.mkdir(parents=True, exist_ok=True)
        fig.savefig(save_path, dpi=150)
        print(f"saved {save_path}")
    if not no_show:
        plt.show()


if __name__ == "__main__":
    main()
