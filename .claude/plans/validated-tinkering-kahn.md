# GLB-authoritative hardpoint geometry & inventory

## Context

Hardpoint geometry is authored twice today: hand-written `off-*`/`dir-*` in
`server/Content/core/hulls.yaml`/`stations.yaml` (streamed to clients, drives sim muzzles +
client prediction), and artist-placed `HP_<Kind>_<Index>` nodes baked into the GLBs. They
disagree — e.g. fighter bolts spawn at (±0.9, 0, 3) while the visible barrels sit at
(±0.42, −0.86, 2.40). This change makes the GLB the authoritative source for the hardpoint
**inventory and geometry** — including how many weapon mounts a hull has — loaded
programmatically per mesh (no hardcoded per-mesh values). YAML becomes a binding/override
layer: it binds weapon-ids to mounts and may override position/direction; unbound weapon
mounts are **empty** (exist, fire nothing, assignable later). No per-hull special cases in
code. Ship nav-light beacons get wired on the client.

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
  LoadoutPreview, DefRegistry.WeaponMounts) read the streamed defs.
- **Empty mounts already fit the system**: every weapon consumer guards with
  `WeaponDefs.TryGetValue(h.WeaponId, …)` and skips unresolvable ids (client
  `DefRegistry.WeaponMounts` :106, server muzzles :79/:1025, payload mass), and
  `LoadoutState` treats `hp.WeaponId` as the authored *default* for an assignable slot.
  So "empty" = a sentinel WeaponId that never resolves (`weapon-id: 0` is a real weapon,
  the scout cannon — 0 cannot mean empty).
- GLB inventories (verified): fighter Weapon_0/1/2 + Booster_0/1 + Thruster_0 + Light_0..4;
  scout Weapon_0/1 + Booster_0/1 + Thruster_0 + Light_0..2; bomber Weapon_0/1 (mirrored gun
  barrels) + Weapon_2 (side node) + Booster_0/1 + Turret_0 + Thruster_0 + Light_0..4;
  pod Thruster_0 + Light_0..5; base.glb docking entrances/exit + 12 lights. No GLB carries
  `HP_Cockpit` or `HP_MainEngine`.
- **Bomber, handled generically**: YAML weapon index 1 is the belly torpedo rack; mesh
  `HP_Weapon_1` is the left gun barrel. The YAML entry keeps its authored off/dir, and
  authored-YAML-wins is the standard override rule — torpedo fires from the belly, no code
  special case. (Visual left barrel ends up empty; art/ship-gen can re-index later.)

## Design

### Merge rule (per hull/station, in one generic pass)

For each def with a `model-name`, load the GLB's `HP_<Kind>_<Index>` nodes
(pos × ws, unit forward). Then:

1. **YAML entries bind and override, keyed by (kind, index)**: a YAML hardpoint entry
   supplies `weapon-id` (for weapon mounts) and, when its `off-*`/`dir-*` are authored,
   overrides the mesh node's position/direction. Unauthored fields fall back to the mesh
   node; a YAML entry with neither authored geometry nor a matching mesh node is a boot
   error. Fully-authored YAML entries may add mounts the mesh lacks (cockpit, pod's
   main-engine, stub/model-less hulls).
2. **Every mesh node not claimed by a YAML entry is appended** (deterministic order:
   kind byte, then index) as a new hardpoint. Appended Weapon mounts get
   `WeaponId = HardpointDef.NoWeapon` (new shared sentinel, `uint.MaxValue`) = an empty,
   assignable mount. Other kinds (Light, Thruster, Turret, Booster, Docking…) get
   `WeaponId = 0` (meaningless for them, as today).
3. No model / missing GLB ⇒ every YAML entry must be fully authored, else boot error.

Armed-weapon order is stable: YAML-declared (bound) weapon entries keep their YAML order at
the head of the list; appended empty mounts land at the end and are skipped by every
armed-weapon consumer — barrel spread-seed indices (server `Simulation.cs:~1364`, client
`DefRegistry.WeaponMounts`) are unchanged.

## Steps

### 1. Library: nullable geometry + sentinel
- `factions/src/Allegiance.Factions/Model/RuntimeData.cs`: `Hardpoint.OffX/Y/Z`, `DirX/Y/Z`
  → `double?` (CoreSerializer already OmitNull). `FactionsContentProjection.ProjectHardpoint`
  (`server/Content/FactionsContentProjection.cs:335`) → `(float)(h.OffX ?? 0)` etc.
- `shared/Defs.cs`: add `public const uint NoWeapon = uint.MaxValue;` on `HardpointDef`;
  mirror the constant in the client (`client/scripts/NetTypes.cs` HardpointDef). Verify
  `Protocol.WriteHardpoints` (:912) / client reader carry WeaponId full-width (u32) so the
  sentinel round-trips.

### 2. Merge pass — new `server/Content/HardpointGeometryMerge.cs`
Runs in `ContentLoader.Load` between `CoreValidator.Validate` (:35) and `Project` (:41),
mutating `Hull.Hardpoints`/`Station.Hardpoints` on the core model (projection stays dumb).

```
Apply(core):
  hulls    → glb = SimAssets.TryLoad($"ships/{ModelName}.glb"),  ws = ModelLength / glb.LongestAxis
  stations → glb = SimAssets.TryLoad($"bases/{ModelName}.glb"),  ws = Radius*2 / glb.LongestAxis

Merge(id, hps, glb, ws):
  nodes[(kind,index)] = (pos*ws, fwd)      # parse HP_<Enum>_<int>; unparsable → warn+skip;
                                           # duplicate node or YAML (kind,index) → throw
  foreach hp in hps:                       # YAML order preserved (spread seeds)
    pos: YAML-authored (any off-* non-null) > nodes[key].Pos > BOOT ERROR (id+kind+index)
    dir: YAML-authored (zero-length → error; normalize) > nodes[key].Fwd > BOOT ERROR
    (weapon-id comes from YAML as today; validator requires it on YAML weapon entries)
  foreach unconsumed node ordered by (kind byte, index):    # append AT END
    append Hardpoint { Kind, Index, Off=Pos, Dir=Fwd,
                       WeaponId = kind==Weapon ? NoWeapon : 0 }
```

### 3. YAML strips (`hulls.yaml`, `stations.yaml`)
- **scout**: weapon → `{ kind: weapon, index: 0, weapon-id: 0 }`; delete `main-engine`
  (mesh `Booster_0/1` become the nozzles); cockpit keeps offsets. Mesh `Weapon_1` → empty
  mount (streamed, unarmed).
- **fighter**: weapons 0/1/2 → `{ kind: weapon, index: N, weapon-id: … }`; boosters →
  `{ kind: booster, index: N }`; cockpit keeps offsets.
- **bomber**: weapon 0 → `{ kind: weapon, index: 0, weapon-id: 2 }` (mesh right barrel);
  weapon 1 (torpedo, id 5) **keeps** `off-y/off-z/dir-z` + a comment (mesh `Weapon_1` is
  the left barrel; authored YAML wins — the generic override, not a special case); delete
  both `main-engine` entries; cockpit keeps offsets. Mesh `Weapon_2` → empty mount;
  `Turret_0` appends (marker/data only).
- **pod**: unchanged YAML (keeps fully-authored main-engine + cockpit; gains Thruster +
  6 Lights via merge).
- **garrison** (stations.yaml): add `model-name: base`; delete the whole `hardpoints:`
  list — base.glb supplies docking entrances/exit + lights in the exact frame
  `World.LoadBase` already uses, unifying client markers with server dock discs.
- Update the "cockpit kept LAST" comments to describe the append rule.

### 4. Remove `World.cs` hardcoded scale table
Delete `ShipClassAssets` (`server/Sim/World.cs:146-151`) + `PodTargetLength`; `World` ctor
gains `IReadOnlyList<ShipClassDef>`; `LoadShipBodies` scales by `def.ModelLength` per
ClassId. Update ~18 call sites (all already have `content` in scope): `Program.cs:212/230`,
`MapCatalog.cs:57` (+ its Build param from Program.cs:196), `SelfTest.cs:60`, tests
(AlephTest, MineTest, StrategyTest, ShieldTest, MissileTest, FogTest ×8, RescueTest).

### 5. Client: beacons, glow fix, empty-slot UI
- `BaseModelLoader.cs`: parameterize `BaseBeacon` (public MoteSize/Range + new Intensity);
  base visuals unchanged by default. Leave its GLB-lights-first branch as-is.
- `ShipModelLoader.cs`: (a) nozzle filter (:110) drops `Thruster` (RCS ports must not
  sprout engine plumes now that they stream); (b) in `AttachEngineGlow` (single seam,
  local+remote ships incl. pods, has `team`): attach a team-tinted `BaseBeacon` per `Light`
  hardpoint at its def offset — `MoteSize ≈ len*0.09`, `Range ≈ len*0.6`,
  `Intensity ≈ 0.4`. Pods get beacons even though glow is skipped.
- Loadout UI (`ui/ShipLoadout.cs`, `ui/LoadoutState.cs`, `ui/LoadoutPreview.cs`): audit the
  weapon-slot enumeration so a `NoWeapon` mount renders as an empty assignable slot rather
  than being dropped or crashing on an unresolvable id (most paths already TryGetValue-skip;
  make empty explicit where the UI lists slots).
- No other client changes: muzzles, HP_ markers (GLB node already overrides same-named def
  marker, `ShipModelLoader.cs:60`), CameraRig cockpit, TargetMarkers all read the
  now-accurate defs.

### 6. Validation
Merge-time boot errors (id+kind+index named): no position source, no direction source,
zero-length authored dir, duplicate (kind,index) in YAML or GLB, ModelLength/Radius ≤ 0
with a model-name. `shared/ContentValidator.cs` `ValidateWeaponHardpoints` (:286): accept
`NoWeapon` as valid (empty mount); still error on a bound id that doesn't resolve. Add:
non-zero Dir + unique (Kind,Index) per def (covers operator Upsert paths).

### 7. Schemas + docs
Regen JSON schemas (`dotnet run --project server/SimServer.csproj -- --gen-schemas`);
update `docs/GLB-AND-HARDPOINT-FORMAT.md` (inventory-from-mesh, YAML binding/override,
NoWeapon sentinel). No ship-gen regeneration needed.

## Verification

- **Unit**: update `tests/ContentTest`: scout def = weapon0(bound, id 0), cockpit +
  appended weapon1(NoWeapon), booster×2, thruster, light×3 — exactly ONE armed weapon;
  bomber = 3 weapon mounts, 2 armed, hardpoint[1] still at YAML (0,−0.8,2) proving the
  override knob; garrison = 18 hardpoints. New assert: fighter def hardpoint[0] ≈
  `HP_Weapon_0` pos × (5.5/LongestAxis) within 1e-4. Update `tests/FactionsTest`
  raw-bundle counts (pre-merge YAML shapes).
- **Suites**: factions ValidationTests, FactionsTest, ContentTest, FlightModelTest,
  Aleph/Mine/Missile/Shield/Fog/Rescue/Strategy/Collision; server `--selftest` and
  `--pregen-assets`.
- **Client smoke**: run-server + client with `--autofly`: bolts leave the visible barrels,
  engine glow sits on the GLB nacelles, beacons blink on ships/pod/base, loadout screen
  shows the scout's empty second mount, docking still works (base markers now def-driven
  from the same GLB frame the sim uses).

## Risks

1. Fighter glow/trail moves to mid-hull nacelle mouths (GLB z≈+0.06 vs old tail −2.75) —
   if it looks wrong, fix the art in ship-gen, not the pipeline.
2. Scout muzzle moves (0,0,3)→(0,−0.97,1.5); bomber cannon becomes right-barrel-only
   (asymmetric until art re-indexes or YAML binds the left barrel) — feel changes, nothing
   asserts positions.
3. Empty mounts surface in the loadout UI for the first time — needs the Step 5 audit; the
   sim/HUD paths already skip them safely.
4. Boot now requires the assets dir for stock content (was best-effort placeholder) —
   deliberate; published layout and test dirs both resolve it via `SimAssets.Resolve`.
