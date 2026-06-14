#!/usr/bin/env python3
"""Procedural spaceship generator CLI.

Two paths, both producing one self-contained ``<name>.glb`` per ship (mesh + embedded PBR
textures + HP_<Kind>_<Index> hardpoint nodes):

  build <catalog.yaml>         compose each authored ship -> GLB
  generate --seed N --count K  emit K random ship YAMLs (seed-deterministic) then build them

Usage (via uv):
  uv run generate.py build ships.yaml --out build
  uv run generate.py generate --seed 1 --count 5 --out build
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
import yaml

import glb as glb_mod
import parts

HERE = Path(__file__).resolve().parent
DEF_TEX = 512


# ---------------------------------------------------------------------------
# Compose one ship (YAML dict) -> GLB bytes
# ---------------------------------------------------------------------------

def compose(ship: dict, *, seed: int | None = None, tex_size: int = DEF_TEX) -> bytes:
    name = ship["name"]
    seed = ship.get("seed", seed if seed is not None else 0)

    placed: list[tuple[dict, str]] = []
    part_hps: dict[tuple[str, int], dict] = {}
    for spec in ship.get("parts", []):
        mesh = parts.build_part(spec)
        kind = spec.get("material", "hull")
        pos = spec.get("pos", (0, 0, 0))
        rot = spec.get("rot", (0, 0, 0))
        scale = spec.get("scale", (1, 1, 1))
        instances = [False] + ([True] if spec.get("mirror") == "x" else [])
        for mx in instances:
            placed.append((parts.place(mesh, pos, rot, scale, mirror_x=mx), kind))
            for hp in spec.get("hardpoints", []):
                ph = _place_hp(hp, pos, rot, scale, mx)
                part_hps[(ph["kind"], ph["index"])] = ph

    # Explicit ship-level hardpoints are authoritative and win on (kind,index) collision.
    final = dict(part_hps)
    for hp in ship.get("hardpoints", []):
        entry = {"kind": hp["kind"], "index": int(hp.get("index", 0)),
                 "offset": list(hp.get("offset", (0, 0, 0))),
                 "forward": list(hp.get("forward", (0, 0, 1)))}
        final[(entry["kind"], entry["index"])] = entry

    return glb_mod.build_glb(name, placed, list(final.values()), seed=seed, tex_size=tex_size)


def _place_hp(hp: dict, pos, rot, scale, mirror_x: bool) -> dict:
    R = parts._euler_matrix(rot)
    s = np.asarray(scale if hasattr(scale, "__len__") else (scale, scale, scale), np.float64)
    off = np.asarray(hp.get("offset", (0, 0, 0)), np.float64) * s
    fwd = np.asarray(hp.get("forward", (0, 0, 1)), np.float64)
    if mirror_x:
        off[0] *= -1; fwd[0] *= -1
    off = R @ off + np.asarray(pos, np.float64)
    fwd = R @ fwd
    return {"kind": hp["kind"], "index": int(hp.get("index", 0)),
            "offset": off.tolist(), "forward": fwd.tolist()}


# ---------------------------------------------------------------------------
# Random ship generation (seed -> YAML)
# ---------------------------------------------------------------------------

def random_ship(rng: np.random.Generator, name: str) -> dict:
    """A varied but plausible hull: fuselage + canopy + symmetric engines, optional wings,
    a nose weapon. Returns a plain YAML-serializable dict with explicit hardpoints."""
    length = round(float(rng.uniform(4.0, 8.0)), 2)
    width = round(float(rng.uniform(1.6, 4.2)), 2)
    height = round(float(rng.uniform(1.0, 2.4)), 2)
    hz = length / 2

    parts_list: list[dict] = []
    hardpoints: list[dict] = []

    # Fuselage: either a tapered box or a tapered cylinder.
    if rng.random() < 0.5:
        nose = round(float(rng.uniform(0.1, 0.6)), 2)
        parts_list.append({"type": "taper", "material": "hull",
                           "size": [width, height, length], "taper": [nose, nose]})
    else:
        parts_list.append({"type": "cylinder", "material": "hull",
                           "radius": round(width / 2, 2), "length": length,
                           "taper": round(float(rng.uniform(0.2, 0.7)), 2), "segments": 16})
    hardpoints.append({"kind": "Weapon", "index": 0,
                       "offset": [0.0, 0.0, round(hz + 0.5, 2)], "forward": [0, 0, 1]})

    # Canopy.
    parts_list.append({"type": "ellipsoid", "material": "cockpit",
                       "size": [round(width * 0.22, 2), round(height * 0.4, 2), round(length * 0.18, 2)],
                       "pos": [0.0, round(height * 0.45, 2), round(hz * 0.45, 2)]})

    # Engines: 1 centered, or 2 mirrored.
    twin = rng.random() < 0.6
    eng_kind = "MainEngine" if rng.random() < 0.5 else "Booster"
    er = round(float(rng.uniform(0.3, 0.6)), 2)
    el = round(float(rng.uniform(1.2, 2.0)), 2)
    ez = round(-hz + el * 0.3, 2)
    if twin:
        ex = round(width * 0.32, 2)
        parts_list.append({"type": "cylinder", "material": "engine", "radius": er,
                           "length": el, "segments": 12, "pos": [ex, 0.0, ez], "mirror": "x"})
        hardpoints.append({"kind": eng_kind, "index": 0, "offset": [-ex, 0.0, round(ez - el / 2, 2)], "forward": [0, 0, -1]})
        hardpoints.append({"kind": eng_kind, "index": 1, "offset": [ex, 0.0, round(ez - el / 2, 2)], "forward": [0, 0, -1]})
    else:
        parts_list.append({"type": "cylinder", "material": "engine", "radius": er,
                           "length": el, "segments": 12, "pos": [0.0, 0.0, ez]})
        hardpoints.append({"kind": eng_kind, "index": 0, "offset": [0.0, 0.0, round(ez - el / 2, 2)], "forward": [0, 0, -1]})

    # Optional swept wings (mirrored fins laid flat).
    if rng.random() < 0.6:
        span = round(float(rng.uniform(1.0, 2.5)), 2)
        chord = round(float(rng.uniform(1.5, 3.0)), 2)
        parts_list.append({"type": "wedge", "material": "hull",
                           "size": [0.15, span, chord], "rot": [0, 0, -90],
                           "pos": [round(width * 0.45, 2), 0.0, round(-hz * 0.2, 2)], "mirror": "x"})

    return {"name": name, "seed": int(rng.integers(0, 2**31)),
            "parts": parts_list, "hardpoints": hardpoints}


# ---------------------------------------------------------------------------

def _load_catalog(path: Path) -> list[dict]:
    data = yaml.safe_load(path.read_text())
    if isinstance(data, dict):
        data = data.get("ships", [])
    if not isinstance(data, list):
        raise ValueError(f"{path}: expected a list of ships (or {{ships: [...]}})")
    return data


def _emit(ship: dict, outdir: Path, tex_size: int) -> dict:
    data = compose(ship, tex_size=tex_size)
    glb_path = outdir / f"{ship['name']}.glb"
    glb_path.write_bytes(data)
    kb = glb_path.stat().st_size // 1024
    nparts = len(ship.get("parts", []))
    nhp = len({(h["kind"], int(h.get("index", 0))) for h in ship.get("hardpoints", [])})
    print(f"  {ship['name']}: glb={kb}KB  parts={nparts}  hardpoints={nhp}")
    return {"name": ship["name"], "bytes": glb_path.stat().st_size,
            "sha256": hashlib.sha256(data).hexdigest(), "parts": nparts, "hardpoints": nhp}


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    b = sub.add_parser("build", help="build every ship in a YAML catalog")
    b.add_argument("catalog", type=Path, nargs="?", default=HERE / "ships.yaml")
    b.add_argument("--out", type=Path, default=HERE / "build")
    b.add_argument("--tex-size", type=int, default=DEF_TEX)

    g = sub.add_parser("generate", help="generate random ships from a seed (writes YAML + GLB)")
    g.add_argument("--seed", type=int, default=1)
    g.add_argument("--count", type=int, default=5)
    g.add_argument("--out", type=Path, default=HERE / "build")
    g.add_argument("--tex-size", type=int, default=DEF_TEX)

    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    if args.cmd == "build":
        ships = _load_catalog(args.catalog)
        print(f"Building {len(ships)} ship(s) from {args.catalog.name} -> {args.out}")
        manifest = [_emit(s, args.out, args.tex_size) for s in ships]
    else:
        rng = np.random.default_rng(args.seed)
        print(f"Generating {args.count} random ship(s) (seed {args.seed}) -> {args.out}")
        manifest = []
        for i in range(args.count):
            name = f"ship-{args.seed}-{i:02d}"
            ship = random_ship(rng, name)
            (args.out / f"{name}.yaml").write_text(yaml.safe_dump(ship, sort_keys=False))
            manifest.append(_emit(ship, args.out, args.tex_size))

    (args.out / "manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"Wrote {args.out / 'manifest.json'}")


if __name__ == "__main__":
    main()
