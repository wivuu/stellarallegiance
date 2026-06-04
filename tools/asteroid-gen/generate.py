#!/usr/bin/env python3
"""Deterministic asteroid generator CLI.

From a seed (+ a kind and optional shape params) it produces, in the output dir:
  <name>.stl         low-poly geometry (rendered by OpenSCAD from asteroid.scad)
  <name>_normal.png  equirectangular tangent-space normal map (baked analytically)
  <name>.glb         Godot-ready mesh with UVs/normals/tangents + embedded normal map
  <name>.json        per-asteroid manifest (seed, kind, params, file hashes)

Kinds: bulbous (rounded/lumpy), crystalline (sharp facets), angular (faceted + gouged).

Usage (via uv):
  uv run generate.py one  --seed 1234 --kind crystalline [--radius 20 --grid 128 --map-size 2048]
  uv run generate.py all  [--catalog asteroids.json] [--out build]

Deterministic within a fixed build environment (the Docker image): identical inputs
yield byte-identical outputs.
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

DEF_GRID = 128
DEF_MAP = 2048
DEF_RADIUS = 20.0

# entry keys forwarded to shapefield.params_from_seed
PARAM_KEYS = ("kind", "radius", "lobes", "lumpiness", "facets", "gouges",
              "roughness", "roughness_terms", "roughness_freq",
              "rocks", "rock_amp", "rock_sharp")


# ---------------------------------------------------------------------------
# OpenSCAD STL rendering
# ---------------------------------------------------------------------------

def _fmt(x: float) -> str:
    return f"{x:.12g}"


def _scad_array(rows) -> str:
    return "[" + ", ".join("[" + ", ".join(_fmt(v) for v in row) + "]" for row in rows) + "]"


def write_stl(params: dict, nlat: int, nlon: int, out_stl: Path) -> None:
    """Render the asteroid base mesh to STL by driving OpenSCAD with literal params."""
    kind = params["kind"]
    b = params["base"]
    lobes, planes, gouges = [], [], []
    if kind == "bulbous":
        lobes = [[*L, a, s] for L, a, s in zip(b["L"], b["amp"], b["sharp"])]
    else:
        planes = [[*N, d] for N, d in zip(b["N"], b["d"])]
        if kind == "angular":
            g = b["gouge"]
            gouges = [[*C, a, s] for C, a, s in zip(g["C"], g["amp"], g["sharp"])]

    wrapper = (
        f'use <{SCAD_LIB.as_posix()}>\n'
        f'asteroid(kind="{kind}", radius={_fmt(params["radius"])}, R0={_fmt(params["R0"])},\n'
        f'  lobes={_scad_array(lobes)},\n'
        f'  planes={_scad_array(planes)},\n'
        f'  gouges={_scad_array(gouges)},\n'
        f'  nlat={nlat}, nlon={nlon});\n'
    )

    openscad = os.environ.get("OPENSCAD", "openscad")
    with tempfile.NamedTemporaryFile("w", suffix=".scad", delete=False) as f:
        f.write(wrapper)
        scad_path = f.name
    try:
        # STL export uses CGAL and needs no display, so OpenSCAD runs headless.
        proc = subprocess.run([openscad, "-o", str(out_stl), scad_path],
                              capture_output=True, text=True)
        if proc.returncode != 0 or not out_stl.exists():
            sys.stderr.write(proc.stdout + proc.stderr)
            raise RuntimeError(f"OpenSCAD failed for {out_stl.name}")
    finally:
        os.unlink(scad_path)


# ---------------------------------------------------------------------------
# Generate one asteroid
# ---------------------------------------------------------------------------

def generate(entry: dict, outdir: Path) -> dict:
    name = entry["name"]
    nlon = int(entry.get("grid", DEF_GRID))
    nlat = max(2, nlon // 2)
    map_w = int(entry.get("map_size", DEF_MAP))
    map_h = max(2, map_w // 2)

    kwargs = {k: entry[k] for k in PARAM_KEYS if k in entry}
    kwargs.setdefault("radius", DEF_RADIUS)
    params = shapefield.params_from_seed(entry["seed"], **kwargs)

    stl = outdir / f"{name}.stl"
    png = outdir / f"{name}_normal.png"
    glb = outdir / f"{name}.glb"

    write_stl(params, nlat, nlon, stl)
    img = bake_normals.bake(params, map_w, map_h)
    img.save(png)
    glb.write_bytes(glb_mod.build_glb(params, img, nlat, nlon))

    manifest = {
        "name": name,
        "seed": int(entry["seed"]),
        "kind": params["kind"],
        "radius": params["radius"],
        "grid": {"nlat": nlat, "nlon": nlon},
        "map_size": [map_w, map_h],
        "params": shapefield.params_to_jsonable(params),
        "files": {f.name: _file_info(f) for f in (stl, png, glb)},
    }
    (outdir / f"{name}.json").write_text(json.dumps(manifest, indent=2))
    print(f"  {name} [{params['kind']}]: "
          f"stl={stl.stat().st_size}B png={png.stat().st_size}B glb={glb.stat().st_size}B")
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
    one.add_argument("--kind", default="bulbous", choices=shapefield.KINDS)
    one.add_argument("--name", default=None, help="output basename (default: <kind>-<seed>)")
    one.add_argument("--radius", type=float, default=DEF_RADIUS)
    one.add_argument("--lumpiness", type=float, default=None)
    one.add_argument("--rocks", type=int, default=None)
    one.add_argument("--rock-amp", type=float, default=None)
    one.add_argument("--roughness", type=float, default=None)
    one.add_argument("--grid", type=int, default=DEF_GRID)
    one.add_argument("--map-size", type=int, default=DEF_MAP)
    one.add_argument("--out", type=Path, default=HERE / "build")

    allp = sub.add_parser("all", help="generate every asteroid in the catalog")
    allp.add_argument("--catalog", type=Path, default=HERE / "asteroids.json")
    allp.add_argument("--out", type=Path, default=HERE / "build")

    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    if args.cmd == "one":
        entry = {"name": args.name or f"{args.kind}-{args.seed}", "seed": args.seed,
                 "kind": args.kind, "radius": args.radius,
                 "grid": args.grid, "map_size": args.map_size}
        for k in ("lumpiness", "rocks", "rock_amp", "roughness"):
            v = getattr(args, k)
            if v is not None:
                entry[k] = v
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
