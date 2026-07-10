#!/usr/bin/env python3
"""Interactive viewer: visual mesh surface only (no COL_ collision parts), with HP_ hardpoints
marked. Docking entry/exit hardpoints get the red-star Dock style; other kinds keep bake.py's
kind-coloured marker + forward-direction arrow. Reuses bake.py's GLB readers so this never drifts
from the merged-hull ground truth.

Usage:
    uv run mesh_only_view.py [--glb PATH] [--save PATH.png] [--kinds Dock,Light,...]

Defaults to client/assets/bases/base.glb (repo-relative), same as bake.py --kind base, and to
--kinds Dock (docking entrance/exit only). Pass --kinds all to show every hardpoint kind.
Scroll wheel zooms in/out; left-drag rotates (standard mplot3d navigation).
"""
import argparse
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import bake  # noqa: E402


def _hp_kind(name):
    p = name.split("_")
    k = p[1] if len(p) >= 2 else "HP"
    return "Dock" if k.startswith("Docking") else k


_HP_STYLE = {
    "Dock": ("#ff2d2d", "*"), "Muzzle": ("#ff7f0e", "^"),
    "Nozzle": ("#17d9c8", "v"), "Light": ("#ffd21e", "o"),
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--glb", type=Path, default=None, help="mesh GLB (default: client/assets/bases/base.glb)")
    ap.add_argument("--save", type=Path, default=None, help="also save a PNG instead of/besides showing")
    ap.add_argument("--no-show", action="store_true", help="skip the interactive window (save only)")
    ap.add_argument("--kinds", default="Dock",
                    help="comma-separated HP kinds to draw (default: Dock only); 'all' shows every kind")
    args = ap.parse_args()
    want_kinds = None if args.kinds.strip().lower() == "all" else {k.strip() for k in args.kinds.split(",") if k.strip()}

    glb_path = args.glb or (Path(__file__).parent.parent.parent / "client/assets/bases/base.glb")
    gltf, blob = bake.load_glb(glb_path) if hasattr(bake, "load_glb") else (None, None)
    if gltf is None:
        import pygltflib
        gltf = pygltflib.GLTF2().load(str(glb_path))
        blob = gltf.binary_blob()

    V, F = bake.read_visual_triangles(gltf, blob)
    _, hps = bake.read_visual_vertices(gltf, blob)
    print(f"{glb_path.name}: {len(V)} verts, {len(F)} tris, {len(hps)} hardpoints")

    import matplotlib
    if args.no_show:
        matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from mpl_toolkits.mplot3d.art3d import Poly3DCollection

    hp_kinds = []
    for n, p, f in hps:
        k = _hp_kind(n)
        if k not in hp_kinds and (want_kinds is None or k in want_kinds):
            hp_kinds.append(k)
    extra = [k for k in hp_kinds if k not in _HP_STYLE]
    pal = plt.cm.Set2(np.linspace(0, 1, max(1, len(extra))))
    marks = ["s", "D", "P", "X", "h", ">", "<", "p", "d", "8"]
    style = dict(_HP_STYLE)
    for i, k in enumerate(extra):
        style[k] = (pal[i % len(pal)], marks[i % len(marks)])

    hp_pos = {k: np.array([p for n, p, f in hps if _hp_kind(n) == k]) for k in hp_kinds}
    hp_dir = {k: np.array([f for n, p, f in hps if _hp_kind(n) == k]) for k in hp_kinds}
    hp_names = {k: [n for n, p, f in hps if _hp_kind(n) == k] for k in hp_kinds}
    arrow_len = 0.12 * float(np.ptp(V, axis=0).max()) if len(V) else 1.0

    fig = plt.figure(figsize=(12, 10))
    ax = fig.add_subplot(111, projection="3d")
    # mplot3d's automatic depth-sort (computed_zorder=True, the default) picks per-artist "closer to
    # camera" by average z, but Text artists don't participate in that sort and read as buried inside
    # the mesh from most angles. Disabling it falls back to draw-order = z-order, so as long as the
    # mesh collection is added first and labels last, labels always render on top regardless of depth.
    ax.computed_zorder = False
    tris = V[F]
    coll = Poly3DCollection(tris, facecolors="#8a9099", edgecolors="#4a4e55", linewidths=0.1, alpha=0.45)
    ax.add_collection3d(coll)

    for kd in hp_kinds:
        col, mk = style[kd]
        P, D = hp_pos[kd], hp_dir[kd]
        ax.scatter(P[:, 0], P[:, 1], P[:, 2], c=[col], marker=mk, s=90,
                   edgecolors="k", linewidths=0.5, depthshade=False, label=kd, zorder=5)
        ax.quiver(P[:, 0], P[:, 1], P[:, 2], D[:, 0], D[:, 1], D[:, 2],
                  length=arrow_len, normalize=False, color=col, linewidth=1.2)
        # Label at the arrow TIP (not the hardpoint origin) so it sits clear of the hull surface
        # instead of overlapping the mesh right at the marker.
        for name, pos in zip(hp_names[kd], P + D * arrow_len):
            ax.text(pos[0], pos[1], pos[2], name, color=col, fontsize=8, fontweight="bold",
                    zorder=10, bbox=dict(boxstyle="round,pad=0.15", fc="white", ec=col, lw=0.6, alpha=0.85))

    if hp_kinds:
        ax.legend(loc="upper left", fontsize=9)
    ax.set_xlabel("X"); ax.set_ylabel("Y"); ax.set_zlabel("Z")
    span = np.ptp(V, axis=0)
    ax.set_box_aspect((span[0], span[1], span[2]))
    lo, hi = V.min(axis=0), V.max(axis=0)
    ax.set_xlim(lo[0], hi[0]); ax.set_ylim(lo[1], hi[1]); ax.set_zlim(lo[2], hi[2])
    ax.view_init(elev=18, azim=-60)
    ax.set_title(f"{glb_path.stem}.glb visual mesh + hardpoints")

    def _on_scroll(event):
        # mplot3d has no built-in scroll-zoom; scale all three axis limits around their shared
        # midpoint so the box aspect (and hardpoint proportions) stay correct while zooming.
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

    if args.save:
        fig.savefig(args.save, dpi=150)
        print(f"saved {args.save}")
    if not args.no_show:
        plt.show()


if __name__ == "__main__":
    main()
