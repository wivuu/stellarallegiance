# ship-gen — procedural spaceship generator

Composes **reusable, modular parts** described in **YAML** into a single, self-contained
**GLB** model the game loads directly. A build step turns a ship definition into one
`<name>.glb` bundling geometry, **baked PBR textures** (embedded), and the
`HP_<Kind>_<Index>` **hardpoint nodes** the client reads for weapons / engine FX.

Sibling of [`../asteroid-gen`](../asteroid-gen) and follows the same toolchain: pure Python,
`uv`-managed, Docker-built, deterministic.

## Quick start

```bash
# Build every ship in the catalog (Docker, reproducible) -> ./build
./build.sh

# Or run locally with uv:
uv run generate.py build ships.yaml --out build

# Procedurally generate a wide variety from a seed (writes <name>.yaml + <name>.glb):
uv run generate.py generate --seed 1 --count 20 --out build
#   (override the Docker default the same way: ./build.sh generate --seed 1 --count 20)
```

Output for each ship: `build/<name>.glb` (+ a generated `<name>.yaml` for the `generate`
path) and a `build/manifest.json` (per-ship size, sha256, part/hardpoint counts).

To use a ship in the client, copy its GLB into `client/assets/ships/<name>.glb`. The four
canonical hulls (`scout`, `fighter`, `bomber`, `pod`) are already wired:
`client/scripts/ShipModelLoader.cs` loads `res://assets/ships/<name>.glb` when present and
falls back to the procedural placeholder otherwise.

## Conventions

- **Axes / scale:** local **+Z forward**, **+Y up**, right-handed, ~1 unit ≈ 1 m.
- **Hardpoints:** empty nodes named exactly `HP_<Kind>_<Index>` with local **+Z = forward**.
  `Kind` must be one of the `HardpointKind` names in `shared/Defs.cs`
  (`Weapon`, `MainEngine`, `Booster`, `Thruster`, `Turret`, `Light`, `DockingEntrance`,
  `DockingExit`). The canonical hulls' hardpoints match `Defs.cs` exactly so they line up
  with the server's weapon/engine positions (e.g. `NoseOffset = 3.0` → `HP_Weapon_0` at z=3).
- **Albedo neutral:** the hull base color stays neutral; the client applies the team/pig
  color at runtime and adds engine glow as a separate effect — so no baked hull emission.
- **Determinism:** byte-stable within a pinned environment (the Docker image), like
  asteroid-gen. PNG bytes can differ across Pillow/zlib versions; always build via Docker
  for reproducible artifacts.

## YAML schema

A catalog is a list of ships (or `{ships: [...]}`). Each ship:

```yaml
- name: fighter            # output basename -> fighter.glb
  seed: 102                # seeds the texture bake (optional)
  parts:                   # composed in order, each is one GLB primitive
    - type: taper          # part primitive (see below)
      material: hull       # material kind: hull | cockpit | engine | trim
      size: [3.6, 1.6, 5.5]
      taper: [0.35, 0.5]   # front [x,y] scale (0 = pinch to a point)
      pos: [0, 0, 0]       # placement translate (ship-local)
      rot: [0, 0, 0]       # euler degrees (XYZ)
      scale: [1, 1, 1]     # optional pre-rotation scale
      mirror: x            # optional: also emit a copy mirrored across X
  hardpoints:              # explicit, authoritative (wins over part-contributed)
    - {kind: Weapon, index: 0, offset: [0, 0, 3.0], forward: [0, 0, 1]}
    - {kind: Booster, index: 0, offset: [-1.1, 0, -2.75], forward: [0, 0, -1]}
    - {kind: Booster, index: 1, offset: [1.1, 0, -2.75], forward: [0, 0, -1]}
```

Hardpoints are **first-class**: declare them directly on the ship to pin gameplay mounts
exactly where `Defs.cs` expects, regardless of how the visual parts are arranged. (Parts may
also contribute hardpoints in their local frame; explicit ship-level entries win on a
`(kind, index)` collision.)

### Part primitives (`parts.py`)

| `type`      | key params                                  | use |
|-------------|---------------------------------------------|-----|
| `box`       | `size [x,y,z]`                              | fuselage blocks |
| `taper`     | `size`, `taper [tx,ty]`                     | nose cones, fuselage tapers |
| `cylinder`  | `radius`, `length`, `taper`, `segments`     | nacelles, barrels, cone (`taper: 0`) |
| `ellipsoid` | `size [x,y,z]`, `segments`, `rings`         | cockpit canopies, escape pod |
| `wedge`     | `size [thickness,height,length]`            | fins / swept wings (rotate + `mirror: x`) |

### Material kinds (`bake.py`)

`hull` (panelled neutral metal), `cockpit` (dark glossy glass), `engine` (dark metal),
`trim` (lighter painted metal). Each kind bakes one tileable albedo/normal/ORM set shared by
every part that uses it.

## Files

- `parts.py` — parametric primitive generators + placement (transform/mirror baked into verts).
- `bake.py` — seamless tileable PBR maps per material kind.
- `glb.py` — multi-primitive GLB assembly, embedded textures, tangents, `HP_` nodes.
- `generate.py` — CLI: `build` (YAML → GLB) and `generate` (seed → random ship YAML → GLB).
- `ships.yaml` — canonical scout/fighter/bomber/pod (hardpoints matched to `shared/Defs.cs`).
