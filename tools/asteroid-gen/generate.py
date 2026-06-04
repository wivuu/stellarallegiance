#!/usr/bin/env python3
"""Deterministic asteroid generator CLI (pure Python).

By default each asteroid emits the lean, non-redundant set:
  <name>.glb          Godot-ready mesh + UVs/normals/tangents + embedded normal/albedo/ORM
  <name>_height.png   16-bit displacement/height (NOT in the GLB; sidecar for parallax)
  <name>.json         per-asteroid manifest (seed, type, params, file hashes)

The standalone normal/albedo/ORM PNGs are byte-identical to what's embedded in the GLB, so
they are opt-in:
  --maps      also write standalone normal/albedo/ORM PNGs (for DCC/inspection)
  --no-height skip the height sidecar
  --glb-only  GLB + manifest only (implies --no-height)

Size levers (per entry in the catalog, or globally on the CLI): grid (mesh density),
map_size (normal map), tex_size (albedo/ORM/height). CLI values override catalog entries.

Types: carbonaceous (dark rounded rubble), stony (fractured rock), metallic (faceted, shiny).

Usage (via uv):
  uv run generate.py one  --seed 1234 --kind metallic [--maps] [--map-size 2048]
  uv run generate.py all  [--map-size 2048 --grid 128] [--jobs N] [--glb-only]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

# Pin BLAS to one thread per process. We parallelize at the asteroid level (catalog) or the
# row-band level (single asteroid), so multithreaded BLAS would only oversubscribe and slow
# things down. Must be set before numpy is first imported (below, via bake/shapefield).
for _v in ("VECLIB_MAXIMUM_THREADS", "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS",
           "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
    os.environ.setdefault(_v, "1")

import bake
import glb as glb_mod
import shapefield

DEF_JOBS = os.cpu_count() or 1

HERE = Path(__file__).resolve().parent

DEF_GRID = 256       # mesh longitude segments (latitude = grid/2)
DEF_MAP = 4096       # normal map width (height = width/2)
DEF_TEX = 2048       # albedo / ORM / height width (height = width/2)
DEF_RADIUS = 20.0

# entry keys forwarded to shapefield.params_from_seed
PARAM_KEYS = ("kind", "radius", "lobes", "lumpiness", "facets", "gouges",
              "boulders", "relief", "roughness", "roughness_terms", "roughness_freq",
              "rocks", "craters", "rock_amp")


# ---------------------------------------------------------------------------
# Generate one asteroid
# ---------------------------------------------------------------------------

def _sizes(entry: dict, opts: dict):
    def pick(key, default):
        if opts.get(key) is not None:
            return int(opts[key])
        return int(entry.get(key, default))
    return pick("grid", DEF_GRID), pick("map_size", DEF_MAP), pick("tex_size", DEF_TEX)


def generate(entry: dict, outdir: Path, opts: dict | None = None) -> dict:
    opts = opts or {}
    name = entry["name"]
    nlon, map_w, tex_w = _sizes(entry, opts)
    nlat = max(2, nlon // 2); map_h = max(2, map_w // 2); tex_h = max(2, tex_w // 2)

    kwargs = {k: entry[k] for k in PARAM_KEYS if k in entry}
    kwargs.setdefault("radius", DEF_RADIUS)
    params = shapefield.params_from_seed(entry["seed"], **kwargs)

    bake_jobs = opts.get("bake_jobs", 1)
    normal = bake.bake_normal(params, map_w, map_h, jobs=bake_jobs)
    albedo, orm, height = bake.bake_surface(params, tex_w, tex_h, jobs=bake_jobs)

    written: dict[str, Path] = {}

    def emit(suffix, save_fn):
        path = outdir / f"{name}{suffix}"
        save_fn(path)
        written[path.name] = path

    emit(".glb", lambda p: p.write_bytes(glb_mod.build_glb(params, normal, albedo, orm, nlat, nlon)))
    if opts.get("maps"):
        emit("_normal.png", lambda p: normal.save(p, compress_level=9))
        emit("_albedo.png", lambda p: albedo.save(p, compress_level=9))
        emit("_orm.png", lambda p: orm.save(p, compress_level=9))
    if opts.get("height", True):
        emit("_height.png", lambda p: height.save(p))

    manifest = {
        "name": name,
        "seed": int(entry["seed"]),
        "kind": params["kind"],
        "radius": params["radius"],
        "grid": {"nlat": nlat, "nlon": nlon},
        "map_size": [map_w, map_h],
        "tex_size": [tex_w, tex_h],
        "params": shapefield.params_to_jsonable(params),
        "files": {n: _file_info(p) for n, p in written.items()},
    }
    (outdir / f"{name}.json").write_text(json.dumps(manifest, indent=2))
    extras = "+maps" if opts.get("maps") else ""
    glb_kb = written[f"{name}.glb"].stat().st_size // 1024
    print(f"  {name} [{params['kind']}]: glb={glb_kb}KB{extras}")
    return manifest


def _generate_star(task):  # picklable top-level helper for the process pool
    return generate(*task)


def _file_info(path: Path) -> dict:
    data = path.read_bytes()
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _add_output_flags(p):
    p.add_argument("--maps", action="store_true",
                   help="also write standalone normal/albedo/ORM PNGs (redundant with the GLB)")
    p.add_argument("--no-height", action="store_true", help="skip the height sidecar PNG")
    p.add_argument("--glb-only", action="store_true", help="GLB + manifest only (implies --no-height)")


def _opts(args, *, map_size=None, tex_size=None, grid=None, bake_jobs=1) -> dict:
    return {
        "maps": args.maps,
        "height": not (args.no_height or args.glb_only),
        "map_size": map_size, "tex_size": tex_size, "grid": grid,
        "bake_jobs": bake_jobs,
    }


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    one = sub.add_parser("one", help="generate a single asteroid from a seed")
    one.add_argument("--seed", type=int, required=True)
    one.add_argument("--kind", default="carbonaceous", choices=shapefield.KINDS)
    one.add_argument("--name", default=None, help="output basename (default: <kind>-<seed>)")
    one.add_argument("--radius", type=float, default=DEF_RADIUS)
    one.add_argument("--lumpiness", type=float, default=None)
    one.add_argument("--rocks", type=int, default=None)
    one.add_argument("--rock-amp", type=float, default=None)
    one.add_argument("--roughness", type=float, default=None)
    one.add_argument("--grid", type=int, default=DEF_GRID)
    one.add_argument("--map-size", type=int, default=DEF_MAP)
    one.add_argument("--tex-size", type=int, default=DEF_TEX)
    one.add_argument("--jobs", type=int, default=DEF_JOBS,
                     help="threads to parallelize the bake across (default: all cores)")
    one.add_argument("--out", type=Path, default=HERE / "build")
    _add_output_flags(one)

    allp = sub.add_parser("all", help="generate every asteroid in the catalog")
    allp.add_argument("--catalog", type=Path, default=HERE / "asteroids.json")
    allp.add_argument("--out", type=Path, default=HERE / "build")
    allp.add_argument("--jobs", type=int, default=DEF_JOBS,
                      help="total cores to use (default: all)")
    allp.add_argument("--grid", type=int, default=None, help="override mesh density for all entries")
    allp.add_argument("--map-size", type=int, default=None, help="override normal-map size for all entries")
    allp.add_argument("--tex-size", type=int, default=None, help="override albedo/ORM/height size for all entries")
    _add_output_flags(allp)

    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    if args.cmd == "one":
        entry = {"name": args.name or f"{args.kind}-{args.seed}", "seed": args.seed,
                 "kind": args.kind, "radius": args.radius,
                 "grid": args.grid, "map_size": args.map_size, "tex_size": args.tex_size}
        for k in ("lumpiness", "rocks", "rock_amp", "roughness"):
            v = getattr(args, k)
            if v is not None:
                entry[k] = v
        bake_jobs = max(1, args.jobs)
        print(f"Generating 1 asteroid -> {args.out} (bake on {bake_jobs} threads)")
        generate(entry, args.out, _opts(args, bake_jobs=bake_jobs))
    else:
        catalog = json.loads(args.catalog.read_text())
        # Spread `jobs` cores: parallelize across asteroids; if there are fewer asteroids than
        # cores, give each asteroid the leftover cores as bake threads.
        jobs = max(1, args.jobs)
        procs = max(1, min(jobs, len(catalog)))
        bake_jobs = max(1, jobs // procs)
        opts = _opts(args, map_size=args.map_size, tex_size=args.tex_size,
                     grid=args.grid, bake_jobs=bake_jobs)
        print(f"Generating {len(catalog)} asteroids from {args.catalog.name} "
              f"-> {args.out} ({procs} procs x {bake_jobs} bake threads)")
        tasks = [(e, args.out, opts) for e in catalog]
        if procs == 1:
            manifests = [generate(*t) for t in tasks]
        else:
            with ProcessPoolExecutor(max_workers=procs) as ex:
                manifests = list(ex.map(_generate_star, tasks))
        (args.out / "manifest.json").write_text(json.dumps(manifests, indent=2))
        print(f"Wrote {args.out / 'manifest.json'}")


if __name__ == "__main__":
    main()
