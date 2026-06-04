#!/usr/bin/env python3
"""Deterministic asteroid generator CLI.

From a seed (+ optional shape params) it produces, in the output dir:
  <name>.stl         low-poly geometry (rendered by OpenSCAD from asteroid.scad)
  <name>_normal.png  equirectangular tangent-space normal map (baked analytically)
  <name>.glb         Godot-ready mesh with UVs/normals/tangents + embedded normal map
  <name>.json        per-asteroid manifest (seed, params, file hashes)

Usage (via uv):
  uv run generate.py one  --seed 1234 [--radius 20 --grid 96 --map-size 1024] [--out build]
  uv run generate.py all  [--catalog asteroids.json] [--out build]

The whole thing is deterministic: identical inputs yield byte-identical outputs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import bake_normals
import glb as glb_mod
import shapefield

HERE = Path(__file__).resolve().parent
SCAD_LIB = HERE / "asteroid.scad"

DEFAULTS = dict(radius=20.0, lobes=7, lumpiness=0.35, detail=0.12, grid=96, map_size=1024)


# ---------------------------------------------------------------------------
# OpenSCAD STL rendering
# ---------------------------------------------------------------------------

def _fmt(x: float) -> str:
    return f"{x:.12g}"


def _scad_array(rows) -> str:
    return "[" + ", ".join("[" + ", ".join(_fmt(v) for v in row) + "]" for row in rows) + "]"


def write_stl(params: dict, nlat: int, nlon: int, out_stl: Path) -> None:
    """Render the asteroid mesh to STL by driving OpenSCAD with literal params."""
    lob = params["lobes"]
    det = params["detail"]
    lobes = [[*L, a, s] for L, a, s in zip(lob["L"], lob["amp"], lob["sharp"])]
    details = [[*F, p, a] for F, p, a in zip(det["F"], det["phase"], det["amp"])]

    wrapper = (
        f'use <{SCAD_LIB.as_posix()}>\n'
        f'asteroid(radius={_fmt(params["radius"])}, R0={_fmt(params["R0"])},\n'
        f'  lobes={_scad_array(lobes)},\n'
        f'  details={_scad_array(details)},\n'
        f'  nlat={nlat}, nlon={nlon});\n'
    )

    openscad = os.environ.get("OPENSCAD", "openscad")

    with tempfile.NamedTemporaryFile("w", suffix=".scad", delete=False) as f:
        f.write(wrapper)
        scad_path = f.name
    try:
        # STL export uses CGAL and needs no display, so OpenSCAD runs headless.
        cmd = [openscad, "-o", str(out_stl), scad_path]
        proc = subprocess.run(cmd, capture_output=True, text=True)
        if proc.returncode != 0 or not out_stl.exists():
            sys.stderr.write(proc.stdout + proc.stderr)
            raise RuntimeError(f"OpenSCAD failed for {out_stl.name}")
    finally:
        os.unlink(scad_path)


# ---------------------------------------------------------------------------
# Generate one asteroid
# ---------------------------------------------------------------------------

def generate(entry: dict, outdir: Path) -> dict:
    e = {**DEFAULTS, **entry}
    name = e["name"]
    nlon = int(e["grid"])
    nlat = max(2, nlon // 2)
    map_w = int(e["map_size"])
    map_h = max(2, map_w // 2)

    params = shapefield.params_from_seed(
        e["seed"], radius=float(e["radius"]), lobes=int(e["lobes"]),
        lumpiness=float(e["lumpiness"]), detail=float(e["detail"]),
    )

    stl = outdir / f"{name}.stl"
    png = outdir / f"{name}_normal.png"
    glb = outdir / f"{name}.glb"

    write_stl(params, nlat, nlon, stl)
    img = bake_normals.bake(params, map_w, map_h)
    img.save(png)
    glb.write_bytes(glb_mod.build_glb(params, img, nlat, nlon))

    manifest = {
        "name": name,
        "seed": int(e["seed"]),
        "radius": float(e["radius"]),
        "grid": {"nlat": nlat, "nlon": nlon},
        "map_size": [map_w, map_h],
        "params": shapefield.params_to_jsonable(params),
        "files": {f.name: _file_info(f) for f in (stl, png, glb)},
    }
    (outdir / f"{name}.json").write_text(json.dumps(manifest, indent=2))
    print(f"  {name}: stl={stl.stat().st_size}B png={png.stat().st_size}B glb={glb.stat().st_size}B")
    return manifest


def _file_info(path: Path) -> dict:
    data = path.read_bytes()
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    one = sub.add_parser("one", help="generate a single asteroid from a seed")
    one.add_argument("--seed", type=int, required=True)
    one.add_argument("--name", default=None, help="output basename (default: seed-<seed>)")
    one.add_argument("--radius", type=float, default=DEFAULTS["radius"])
    one.add_argument("--lobes", type=int, default=DEFAULTS["lobes"])
    one.add_argument("--lumpiness", type=float, default=DEFAULTS["lumpiness"])
    one.add_argument("--detail", type=float, default=DEFAULTS["detail"])
    one.add_argument("--grid", type=int, default=DEFAULTS["grid"])
    one.add_argument("--map-size", type=int, default=DEFAULTS["map_size"])
    one.add_argument("--out", type=Path, default=HERE / "build")

    allp = sub.add_parser("all", help="generate every asteroid in the catalog")
    allp.add_argument("--catalog", type=Path, default=HERE / "asteroids.json")
    allp.add_argument("--out", type=Path, default=HERE / "build")

    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    if args.cmd == "one":
        entry = {
            "name": args.name or f"seed-{args.seed}",
            "seed": args.seed, "radius": args.radius, "lobes": args.lobes,
            "lumpiness": args.lumpiness, "detail": args.detail,
            "grid": args.grid, "map_size": args.map_size,
        }
        print(f"Generating 1 asteroid -> {args.out}")
        generate(entry, args.out)
    else:
        catalog = json.loads(args.catalog.read_text())
        print(f"Generating {len(catalog)} asteroids from {args.catalog.name} -> {args.out}")
        manifests = [generate(e, args.out) for e in catalog]
        (args.out / "manifest.json").write_text(json.dumps(manifests, indent=2))
        print(f"Wrote {args.out / 'manifest.json'}")


if __name__ == "__main__":
    main()
