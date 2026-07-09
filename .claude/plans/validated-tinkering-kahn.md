# GLB-authoritative hardpoint geometry

## Context

Hardpoint positions/directions are authored twice today: hand-written `off-*`/`dir-*` in
`server/Content/core/hulls.yaml`/`stations.yaml` (streamed to clients, drives sim muzzles +
client prediction), and artist-placed `HP_<Kind>_<Index>` nodes baked into the GLBs. They
disagree — e.g. fighter bolts spawn at (±0.9, 0, 3) while the visible barrels sit at
(±0.42, −0.86, 2.40). This change makes the GLB nodes the authoritative geometry source,
loaded programmatically per mesh (no hardcoded per-mesh values), with YAML as an explicit
override knob. User decisions: GLB-only non-weapon hardpoints (lights/thrusters/turret) join
the streamed defs; YAML offsets become optional and are stripped where the GLB covers them,
but YAML wins when authored; ship nav-light beacons get wired on the client.

Everything ships over the existing binary `MsgDefs` (variable-length hardpoint lists, all
kinds already in the enum) — **no wire/protocol change, no JSON needed**.

## Verified facts that shape the design

- Ship GLBs already reach the server: `server/SimServer.csproj` Content-links
  `../client/assets/ships/*.glb` (+ bases, asteroids) into `output/assets`.
  `SimAssets.TryLoad` → `SimModel.Hardpoints` (name, pos, forward in authored units) +
  `LongestAxis`. The `.simmodel` sidecar cache already serializes hardpoints and is
  SHA256-keyed — no version bump needed.
- Scale parity: client `GlbLoader.NormalizeLongestAxis` and server `ConvexHull.LongestAxis`
  compute the same AABB max extent, so `ws = ModelLength / LongestAxis` (ships) and
  `ws = Radius*2 / LongestAxis` (base, matches `World.LoadBase`, `server/Sim/World.cs:344`).
- One merge seam covers everything: sim (`Simulation.BuildMuzzles` :78, payload :1024) and
  wire (`Protocol.BuildDefs` :932) read the **same def objects**; all client consumers
  (PredictionController, ShipModelLoader markers/glow, CameraRig, TargetMarkers,
  LoadoutPreview, DefRegistry.WeaponMounts) read the streamed defs. No consumer changes for
  positioning.
- **Bomber trap (verified from GLBs):** bomber GLB `HP_Weapon_0/1` are mirrored gun barrels;
  YAML weapon index 1 is the belly torpedo rack. Kind+index matching alone would mount the
  rack on the left barrel → the bomber rack keeps its YAML offsets (the override knob).
  Fighter's `HP_Weapon_2` does match its YAML rack, so fighter strips fully.
- Scout/fighter/bomber GLBs carry `HP_Booster_*` but no `HP_MainEngine`; no GLB carries
  `HP_Cockpit`. Scout GLB has an extra `HP_Weapon_1` that must NOT become an armed mount.

## Steps

### 1. Library: nullable geometry
`factions/src/Allegiance.Factions/Model/RuntimeData.cs`: `Hardpoint.OffX/Y/Z`, `DirX/Y/Z`
→ `double?` (CoreSerializer already OmitNull). `FactionsContentProjection.ProjectHardpoint`
(`server/Content/FactionsContentProjection.cs:335`) → `(float)(h.OffX ?? 0)` etc.

### 2. Merge pass — new `server/Content/HardpointGeometryMerge.cs`
Runs in `ContentLoader.Load` between `CoreValidator.Validate` (:35) and `Project` (:41),
mutating `Hull.Hardpoints`/`Station.Hardpoints` on the core model (projection stays dumb;
no `authored` flag reaches `HardpointDef` or the wire).

```
Apply(core):
  hulls    → glb = SimAssets.TryLoad($"ships/{ModelName}.glb"),  ws = ModelLength / glb.LongestAxis
  stations → glb = SimAssets.TryLoad($"bases/{ModelName}.glb"),  ws = Radius*2 / glb.LongestAxis

Merge(id, hps, glb, ws):
  nodes[(kind,index)] = (pos*ws, fwd)     # parse HP_<Enum>_<int>; unparsable → warn+skip;
                                          # duplicate → throw
  foreach hp in hps (YAML order preserved — barrel spread seeds):
    pos: YAML-authored (any off-* non-null) > nodes[key].Pos > BOOT ERROR naming id+kind+index
    dir: YAML-authored (normalize; zero-len → error) > nodes[key].Fwd > BOOT ERROR
  unconsumed GLB nodes, ordered by (kind byte, index), appended AT END:
    Weapon → info-log + skip (weapon existence/weapon-id stays YAML-authoritative)
    else   → append new Hardpoint (lights, thrusters, turret, docking)
```

Append-at-end preserves weapon-index/spread-seed order (server `Simulation.cs:~1364`,
client `DefRegistry.WeaponMounts`) and existing positional asserts. No ModelName / missing
GLB ⇒ every YAML entry must be fully authored, else boot error (validate-at-boot idiom).

### 3. YAML strips (`hulls.yaml`, `stations.yaml`)
- **scout**: weapon → `{ kind: weapon, weapon-id: 0 }`; delete `main-engine` (GLB
  `Booster_0/1` become the nozzles); cockpit keeps offsets. GLB `Weapon_1` logged+skipped.
- **fighter**: weapons 0/1/2 and boosters → binding-only entries; cockpit keeps offsets.
- **bomber**: weapon 0 stripped (GLB right barrel); **weapon 1 (torpedo, id 5) KEEPS its
  YAML offsets** with a comment (GLB `Weapon_1` = left barrel, not the rack); delete both
  `main-engine` entries; cockpit keeps offsets. GLB `Weapon_2` skipped; `Turret_0` appends.
- **pod**: unchanged (keeps YAML main-engine + cockpit; gains Thruster + 6 Lights via merge).
- **garrison** (stations.yaml): add `model-name: base`; delete the whole `hardpoints:` list —
  base.glb supplies docking entrances/exit + 12 lights in the exact frame `World.LoadBase`
  already uses, unifying client markers with server dock discs.
- Update the "cockpit kept LAST" comments to describe the append rule.

### 4. Remove `World.cs` hardcoded scale table
Delete `ShipClassAssets` (`server/Sim/World.cs:146-151`) + `PodTargetLength`; `World` ctor
gains `IReadOnlyList<ShipClassDef>`; `LoadShipBodies` scales by `def.ModelLength` per
ClassId. Update ~18 call sites (all already have `content` in scope): `Program.cs:212/230`,
`MapCatalog.cs:57` (+ its Build param from Program.cs:196), `SelfTest.cs:60`, tests
(AlephTest, MineTest, StrategyTest, ShieldTest, MissileTest, FogTest ×8, RescueTest).

### 5. Client: ship nav-light beacons + glow fix
- `BaseModelLoader.cs`: parameterize `BaseBeacon` (public MoteSize/Range + new Intensity);
  base visuals unchanged by default. Leave its GLB-lights-first branch as-is.
- `ShipModelLoader.cs`: (a) nozzle filter (:110) drops `Thruster` (RCS ports must not sprout
  engine plumes now that they stream); (b) in `AttachEngineGlow` (single seam, local+remote
  ships incl. pods, has `team`): attach a team-tinted `BaseBeacon` per `Light` hardpoint at
  its def offset — `MoteSize ≈ len*0.09`, `Range ≈ len*0.6`, `Intensity ≈ 0.4`. Pods get
  beacons even though glow is skipped.

### 6. Validation
Merge-time boot errors (id+kind+index named): no position source, no direction source,
zero-length authored dir, duplicate (kind,index) in YAML or GLB, ModelLength/Radius ≤ 0 with
a model-name. Add to `shared/ContentValidator.cs`: non-zero Dir + unique (Kind,Index) per
def (covers operator Upsert paths).

### 7. Schemas + docs
Regen JSON schemas (`dotnet run --project server/SimServer.csproj -- --gen-schemas`);
update `docs/GLB-AND-HARDPOINT-FORMAT.md`. No ship-gen regeneration needed.

## Verification

- **Unit**: update `tests/ContentTest` (scout def = weapon, cockpit, booster×2, thruster,
  light×3; exactly ONE weapon; garrison = 18 hardpoints). New assert: fighter def
  hardpoint[0] ≈ `HP_Weapon_0` pos × (5.5/LongestAxis) within 1e-4, and bomber
  hardpoint[1] still at YAML (0,−0.8,2) — proves the override knob. Update
  `tests/FactionsTest` raw-bundle counts.
- **Suites**: factions ValidationTests, FactionsTest, ContentTest, FlightModelTest,
  Aleph/Mine/Missile/Shield/Fog/Rescue/Strategy/Collision; server `--selftest` and
  `--pregen-assets`.
- **Client smoke**: run-server + client with `--autofly`: bolts leave the visible barrels,
  engine glow sits on the GLB nacelles, beacons blink on ships/pod/base, docking still works
  (base markers now def-driven from the same GLB frame the sim uses).

## Risks

1. Fighter glow/trail moves to mid-hull nacelle mouths (GLB z≈+0.06 vs old tail −2.75) —
   if it looks wrong, fix the art in ship-gen, not the pipeline.
2. Scout muzzle moves (0,0,3)→(0,−0.97,1.5); bomber cannon becomes right-barrel-only
   (asymmetric) — feel changes, nothing asserts positions. Scout GLB `Weapon_1` stays dark
   by design (logged).
3. Boot now requires the assets dir for stock content (was best-effort placeholder) —
   deliberate; published layout and test dirs both resolve it via `SimAssets.Resolve`.
