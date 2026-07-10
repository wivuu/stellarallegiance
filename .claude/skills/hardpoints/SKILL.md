---
name: hardpoints
description: Inspect the HP_<Kind>_<Index> hardpoint nodes baked into GLB meshes (ships/bases) and reason about how they merge into the streamed defs. Use when asked what hardpoints a model has, where a muzzle/nozzle/light/docking point sits, why a bolt/glow/beacon appears where it does, when authoring or debugging hulls.yaml/stations.yaml hardpoint entries, or when adding HP_ nodes in tools/ship-gen.
---

# Inspecting GLB hardpoints

**The mesh is the authoritative hardpoint inventory + geometry** (since 2026-07, see
`docs/GLB-AND-HARDPOINT-FORMAT.md` and `server/Content/HardpointGeometryMerge.cs`). To see
what a model actually carries, dump its `HP_` nodes:

```sh
python3 .claude/skills/hardpoints/glb_hardpoints.py client/assets/ships/fighter.glb --length 5.5
python3 .claude/skills/hardpoints/glb_hardpoints.py client/assets/ships/ --length 5.5   # whole dir
python3 .claude/skills/hardpoints/glb_hardpoints.py client/assets/bases/base.glb --length 180
```

- Output replicates `shared/Collision/GlbReader.cs`: per node its **position** (world
  translation) and **forward** (world +Z) in **authored GLB units**, plus the mesh AABB and
  longest axis (same math as `ConvexHull.LongestAxis` / `GlbLoader.MeshAabb`).
- `--length` additionally prints **world-unit** positions via `ws = length / longest-axis` —
  the exact scale the sim and client apply. Pass the hull's `model-length` from
  `server/Content/core/hulls.yaml` (scout 4.5, fighter 5.5, bomber 7.2, pod 2.8) or, for a
  base, `radius * 2` from `stations.yaml` (garrison: 180). The `world=` columns are what
  lands on the streamed `HardpointDef` (and what YAML `off-*` would override).
- Stdlib-only; works on any .glb (canonical copies: `client/assets/ships|bases/`, identical
  builds in `tools/ship-gen/build/`; raw art in `pick-assets/` may carry no HP_ nodes).

## Interpreting what you see

- Node contract: `HP_<Kind>_<Index>`, local +Z = the hardpoint's forward. Kinds = the
  `HardpointKind` enum member spelling (`Weapon`, `MainEngine`, `Booster`, `Thruster`,
  `Turret`, `Light`, `DockingEntrance`, `DockingExit`, `Cockpit`). Anything else is skipped
  by the merge with a warning.
- `DockingEntrance` is SPECIAL: the markers are **not** independent points — they group in
  **fives** (sorted by index) into one bounded rectangular docking DOOR (marker 0 = the face,
  its +Z = inward normal; markers 1–4 = the rectangle boundary side-midpoints, order-agnostic).
  A base may author N doors. Parsed by `shared/Collision/DockFace.cs` for BOTH the server and
  client; see `docs/GLB-AND-HARDPOINT-FORMAT.md` §"Docking doors". `DockingExit` stays one-per-exit.
- Merge rules (`HardpointGeometryMerge`): YAML entries bind/override by (kind, index) —
  `weapon-id` binding always comes from YAML; authored `off-*`/`dir-*` beat the mesh node.
  Unclaimed mesh nodes append at the end ordered by (kind byte, index); unbound Weapon nodes
  become **empty mounts** (`HardpointDef.NoWeapon`). No mesh node AND no authored geometry =
  boot error. `HP_Cockpit` exists in no mesh — always YAML-authored.
- To verify what the server actually streamed after a change, `tests/ContentTest` asserts the
  merged layouts, and a live check is `scripts/run-server.sh --local --autostart` + the
  `verify` skill's autofly capture.
- Changing hardpoints on canonical hulls = edit the part YAML in `tools/ship-gen` and rebake
  (nodes are baked into the GLB), NOT hand-editing hulls.yaml offsets — YAML offsets are the
  deliberate-override knob (e.g. bomber torpedo belly rack vs mesh `HP_Weapon_1` left barrel).
