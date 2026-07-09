# Plan: GLB-authoritative hardpoint geometry (positions + directions)

The `HP_<Kind>_<Index>` empty nodes baked into the ship/base GLBs become the authoritative
source for hardpoint POSITIONS and DIRECTIONS. YAML `off-*`/`dir-*` become an optional
explicit-override knob (YAML wins when authored). GLB-only non-Weapon hardpoints (nav
lights, RCS thrusters, bomber turret) join the streamed defs; GLB-only Weapon nodes are
skipped (weapon existence + weapon-id binding stays YAML-authoritative). Ship nav-light
beacons get wired on the client, reusing the BaseBeacon pattern.

## Verified facts that differ from / sharpen the briefing

- Bomber GLB has `HP_Booster_0/1` (NOT MainEngine) — same as scout/fighter. All three armed
  ships' YAML `main-engine`/`booster` entries map onto GLB `Booster` nodes.
- **Bomber GLB `HP_Weapon_1` is the LEFT GUN BARREL** at (-1.183,-0.687,+3.595), a mirror of
  `Weapon_0` (+1.184,-0.690,+3.596) — it is NOT the belly torpedo rack the YAML declares at
  index 1 (0,-0.8,2). Blind kind+index matching would put the torpedo rack on the left gun
  barrel. Resolution: the bomber torpedo rack KEEPS its YAML offsets (this is exactly the
  explicit-override knob). Bomber GLB `Weapon_2` (-2.516,+1.032,+0.110) is unclaimed → skipped.
- Fighter GLB `Weapon_2` (0.019,-1.056,+1.076) DOES match the YAML belly rack (0,-0.6,2) →
  fighter can strip all three weapon offsets.
- `SimModelCache` (server/Assets/SimModel.cs, Magic "SMDL" Version 1) already serializes
  Hardpoints and is keyed on the GLB's SHA256 — stale caches are impossible; **no version
  bump needed**. No `.simmodel` sidecars are committed outside bin/ despite the file comment.
- `ShipModelLoader.AttachEngineGlow` (client) treats MainEngine|Booster|Thruster ALL as glow
  nozzles (line 110). Streaming GLB `HP_Thruster_*` into defs would sprout an extra constant
  plume per ship → restrict glow to MainEngine|Booster.
- Fighter GLB boosters sit near mid-hull z≈+0.06 (nacelle mouths), not at the tail like the
  YAML's -2.75. Glow + trail anchor move; verify visually, fix ART if wrong (not the pipeline).
- `World` is constructed in 18 places (Program.cs:212/230, MapCatalog.cs:57, SelfTest.cs:60,
  7 test programs) — ctor threading is mechanical but touches all of them.
- FlightModelTest asserts NO muzzle positions — safe. ContentTest + FactionsTest assert
  hardpoint counts/kinds positionally — must be updated.
- `run-client.sh` has no `--autofly`; the CLIENT binary takes `--autofly`
  (client/scripts/ShipController.cs:169, picks Fighter, headless verify).
- ContentTest calls `ContentLoader.Load` from its bin dir; `SimAssets` (server/Assets/SimAssets.cs)
  probes up ≤8 dirs and finds `client/assets` from the repo — the new GLB dependency works in-repo.

## Architecture decision: where the merge runs

Merge on the **Core model** (library `Hull.Hardpoints` / `Station.Hardpoints`) inside
`ContentLoader.Load`, between `CoreValidator.Validate` and `FactionsContentProjection.Project`
(server/Content/ContentLoader.cs:35-41). Rationale:
- "Authored vs unauthored" falls out of making the library `Hardpoint` Off*/Dir* nullable —
  no `authored` flag threaded through `HardpointDef` (wire type unchanged, no protocol bump).
- Projection stays a dumb cast; sim (Simulation.BuildMuzzles), wire (Protocol.BuildDefs) and
  every client consumer see merged values with zero changes.
- CoreValidator (pure library) can't see GLBs; the no-position-source rule lives in the merge
  pass itself and throws `InvalidDataException` (same boot-gate behavior as ContentLoader).
- ContentTest exercises the identical path.

## Step 1 — Library model: nullable geometry

`factions/src/Allegiance.Factions/Model/RuntimeData.cs` (Hardpoint record):
- `OffX/OffY/OffZ/DirX/DirY/DirZ`: `double` → `double?`. Update XML docs: "null = take the
  position/direction from the hull's GLB `HP_<Kind>_<Index>` node (world-scaled); authoring
  any component makes YAML the override".
- CoreSerializer already OmitNull (Serialization/CoreSerializer.cs:18) — round-trips cleanly.
- `FactionsContentProjection.ProjectHardpoint` (server/Content/FactionsContentProjection.cs:335):
  `(float)(h.OffX ?? 0)` etc. (post-merge, runtime entries are always filled; `?? 0` covers
  catalog-only hulls).
- factions ValidationTests / FactionsTest keep compiling (implicit double→double? conversion).

## Step 2 — New merge pass: server/Content/HardpointGeometryMerge.cs

Static class, called from `ContentLoader.Load` after Validate:

```
HardpointGeometryMerge.Apply(core);   // throws InvalidDataException on authoring errors
return FactionsContentProjection.Project(core, WorldLoader.Load(worldPath));
```

Pseudocode:

```
Apply(Core core):
  foreach hull in core.Hulls where hull.ClassId != null:            # pod (255) included
    MergeEntity(id: hull.Id, hps: hull.Hardpoints,
                glbRel: hull.ModelName is empty ? null : $"ships/{hull.ModelName}.glb",
                worldScaleOf: model => hull.ModelLength / model.LongestAxis,
                requirePositive: hull.ModelLength)
  foreach st in core.Stations where st.BaseTypeId != null:
    MergeEntity(id: st.Id, hps: st.Hardpoints,
                glbRel: st.ModelName is empty ? null : $"bases/{st.ModelName}.glb",
                worldScaleOf: model => st.Radius * 2 / model.LongestAxis,
                requirePositive: st.Radius)

MergeEntity(id, hps, glbRel, worldScaleOf):
  glb = glbRel != null ? SimAssets.TryLoad(glbRel) : null
  if glb != null and glb.LongestAxis <= 1e-3: glb = null (warn)
  nodes = {}                                    # (Kind,Index) -> (Pos, Fwd)
  if glb != null:
    ws = worldScaleOf(glb)                      # error if ModelLength/Radius <= 0
    foreach (name, pos, fwd) in glb.Hardpoints: # SimModel.Hardpoints, authored units
      if !TryParseHpName(name, out kind, out index):   # "HP_<EnumName>_<int>"
        warn $"{glbRel}: unrecognized hardpoint node '{name}' — skipped"; continue
      if nodes.ContainsKey((kind,index)):
        throw $"{glbRel}: duplicate hardpoint node {kind}_{index}"
      nodes[(kind,index)] = (pos * ws, fwd)     # fwd already unit (GlbReader normalizes)
  consumed = {}
  foreach hp in hps:                            # YAML-declared entries, authored order kept
    key = ((RuntimeHardpointKind)hp.Kind, hp.Index)
    if consumed.Contains(key): throw $"hull '{id}': duplicate hardpoint {key}"
    consumed.Add(key)
    posAuthored = hp.OffX ?? hp.OffY ?? hp.OffZ is any non-null
    dirAuthored = hp.DirX ?? hp.DirY ?? hp.DirZ is any non-null
    hasNode = nodes.TryGetValue(key, out node)
    # position: YAML wins > GLB > boot error
    if posAuthored: fill null components with 0
    elif hasNode:   (hp.OffX,hp.OffY,hp.OffZ) = node.Pos
    else: throw $"'{id}' hardpoint {kind}_{index}: no position source (no off-* in YAML, no HP_{kind}_{index} node in {glbRel ?? "<no model>"})"
    # direction: same precedence; YAML dir must be non-zero, then normalized
    if dirAuthored: fill nulls with 0; if |dir|<1e-6 throw; normalize
    elif hasNode:   (hp.DirX,hp.DirY,hp.DirZ) = node.Fwd
    else: throw $"'{id}' hardpoint {kind}_{index}: no direction source"
  # append GLB-only nodes, deterministic order (kind byte, then index), at the END of the
  # list so every existing positional index (weapon barrel order, FactionsTest positions,
  # cockpit) is untouched
  foreach key in nodes.Keys.Except(consumed).OrderBy(kind byte).ThenBy(index):
    if key.Kind == Weapon:
      log $"[Content] '{id}': GLB-only weapon node HP_Weapon_{index} ignored (weapons are YAML-declared)"
      continue
    hps.Add(new Hardpoint { Kind, Index, Off = node.Pos, Dir = node.Fwd, WeaponId = 0 })
```

Notes:
- Pods participate uniformly (deviation from the briefing's "pods fall back to YAML"): pod
  has model-name `pod`; it keeps its YAML `main-engine` + `cockpit` (no GLB engine node) and
  gains `Thruster_0` + `Light_0..5` — the lifeboat's blinking lights are wanted (decision 3/5).
- Hull with ClassId but empty ModelName (procedural placeholder): `nodes` empty → every YAML
  entry must be fully authored or boot error. Missing GLB file for an authored model-name:
  `SimAssets.TryLoad` null → same rule (server without assets dir now refuses to boot on
  stock content — correct per "validate at boot" iron rule; the csproj ships the GLBs).
- `TryParseHpName`: split on '_', `Enum.TryParse<RuntimeHardpointKind>(part1, out kind)`
  (exact member names match node names), `byte.TryParse(part2)`.
- Barrel spread-seed safety: server (Simulation.cs:1364) and client (PredictionController.cs
  ~343, DefRegistry.WeaponMounts:99) both index barrels by the weapon-only sublist in
  hardpoint declaration order; YAML order is preserved and appends are non-Weapon, so seeds
  stay aligned.

## Step 3 — YAML edits

server/Content/core/hulls.yaml:
- scout: `- { kind: weapon, weapon-id: 0 }` (strip off/dir → GLB Weapon_0 (0,-0.967,+1.529));
  DELETE the `main-engine` entry (no GLB MainEngine node; GLB Booster_0/1 take over as the
  nozzles — kind changes MainEngine→Booster on the wire, client treats them identically);
  cockpit unchanged (no GLB carries HP_Cockpit — YAML stays the source). GLB Weapon_1 is
  logged + skipped (YAML declares one gun).
- fighter: weapons 0/1/2 → `- { kind: weapon, index: N, weapon-id: ... }` (GLB Weapon_0/1/2
  cover all three, incl. the belly rack); DELETE booster offsets → `- { kind: booster, index: 0 }`
  / `index: 1` (GLB Booster_0/1); cockpit unchanged.
- bomber: weapon 0 → `- { kind: weapon, index: 0, weapon-id: 2 }` (GLB Weapon_0, the right
  barrel — flag: single cannon becomes visually asymmetric; acceptable, bolts now leave a real
  barrel); weapon 1 (torpedo rack) KEEPS `off-y: -0.8, off-z: 2, dir-z: 1` with a comment
  "YAML override: GLB Weapon_1 is the second gun barrel, not the rack"; DELETE both
  `main-engine` entries (GLB Booster_0/1); cockpit unchanged. GLB Weapon_2 logged + skipped;
  GLB Turret_0 appends (data + marker only).
- pod: unchanged (main-engine + cockpit keep YAML values; gains Thruster_0 + Light_0..5 from GLB).
- Update the "kept LAST" comments: cockpit stays last among YAML-DECLARED entries; GLB-only
  hardpoints append after it; weapon barrel order = weapon declaration order.

server/Content/core/stations.yaml (garrison):
- add `model-name: base` (Buildable.ModelName, already in the model; merge resolves
  `bases/base.glb` — same file World.LoadBase and the client hardcode).
- DELETE the whole `hardpoints:` list — GLB covers everything: 5 DockingEntrance, 1
  DockingExit, 12 Lights, scaled by ws = radius*2/LongestAxis = exactly the frame
  World.LoadBase (server/Sim/World.cs:339-378) already bakes, so streamed def markers and
  server dock discs unify.

## Step 4 — World.cs: kill the hardcoded ShipClassAssets table

server/Sim/World.cs:
- Delete `ShipClassAssets` (:146-151) and `PodTargetLength` (:152).
- Ctor gains a parameter: `IReadOnlyList<ShipClassDef> ships` →
  `World(ulong seed, WorldConfig cfg, float baseMaxHealth, FactionStart start, IReadOnlyList<ShipClassDef> ships)`.
- `LoadShipBodies(ships)`: size `_shipHulls` to max non-pod ClassId+1; foreach def with
  non-empty ModelName and ModelLength > 0: `LoadShipHull($"ships/{def.ModelName}.glb",
  def.ModelLength)` into `_shipHulls[def.ClassId]`, pod (`GameContent.PodClassId`) into `_podHull`.
- Update ALL call sites (all have `content` in scope; pass `content.Ships`):
  server/Program.cs:212 + BuildWorldForMap (~:230), server/Content/MapCatalog.cs:57 (Build
  gains a `ships` param; Program.cs:196 passes `content.Ships`), server/Assets/SelfTest.cs:60,
  tests/AlephTest:50, MineTest:56, StrategyTest:33, ShieldTest:40, MissileTest:64,
  FogTest:42/1176/1391/1392/1406/1463/1524/1634, RescueTest:44.

## Step 5 — Client: beacons + glow-kind fix

client/scripts/BaseModelLoader.cs (`BaseBeacon`, :225):
- Promote the private consts to public fields with the current defaults:
  `MoteSize = 2.4f`, `Range = 12f`, and add `Intensity = 1f` scaling the emission/light
  energies in `ApplyBlink` — base visuals unchanged by default.

client/scripts/ShipModelLoader.cs:
- `AttachEngineGlow` (:99): nozzle filter (:110) drops `HardpointKind.Thruster` (RCS ports
  now stream on every ship and must not carry a constant plume). Comment update.
- Same function (single seam — WorldRenderer.cs:1578/:1603 call it for local AND remote
  ships, pods included): after the trail, attach a beacon per `Kind == Light` hardpoint:
  `new BaseBeacon { Position = new Vector3(hp.OffX, hp.OffY, hp.OffZ), Color = <same team
  palette as base beacons>, MoteSize = len * 0.09f, Range = len * 0.6f, Intensity = 0.4f }`
  (len = TargetLength — fighter ≈ 0.5u motes vs the base's 2.4). Def offsets are world-unit
  on the unscaled ship container — exactly where the scaled GLB node sits post-merge.
  Pods keep beacons (drifting lifeboat blinks) even though glow is skipped.
- No other client changes: PredictionController muzzles, HP_ markers (Build:57-61, GLB node
  already overrides def marker of the same name), CameraRig cockpit, TargetMarkers,
  LoadoutPreview, DefRegistry all read the now-GLB-accurate defs. Leave BaseModelLoader's
  GLB-lights-first branch (:77-95) as-is (def path is now equivalent; simplification optional).

## Step 6 — Validation rules

- Merge pass (boot gate, server): no-position-source / no-direction-source / zero-length
  authored dir / duplicate (kind,index) in YAML or GLB / model-length (radius) <= 0 with a
  model-name → `InvalidDataException` naming hull-or-station id + kind + index (examples in
  pseudocode). Warn+skip unparsable HP_ names; info-log skipped GLB-only Weapon nodes.
- shared/ContentValidator.cs (2nd gate, projected defs): add one cheap geometric rule —
  every HardpointDef's (DirX,DirY,DirZ) must be non-zero-length; duplicate (Kind,Index)
  within one def is an error. (Covers operator Upsert paths that bypass the merge.)
- CoreValidator: unchanged (pure library; can't see GLBs; runs pre-merge and only reads
  Kind/WeaponId, which the merge never alters).

## Step 7 — Schemas + docs

- Regenerate: `dotnet run --project server/SimServer.csproj -- --gen-schemas` (Program.cs:38
  writes schemas/*.json from the models; nullable Off*/Dir* flow in automatically).
- docs/GLB-AND-HARDPOINT-FORMAT.md: document GLB-authoritative geometry, the YAML override
  knob, the GLB-only-Weapon skip rule, append ordering. tools/ship-gen needs NO regeneration
  (GLBs already carry the nodes).

## Step 8 — Tests / verification

Update:
- tests/ContentTest/Program.cs (:54-60 +): scout def list becomes [weapon(id 0), cockpit,
  booster0, booster1, thruster0, light0..2] (count 8, appended sorted by kind byte —
  Booster(2) < Thruster(3) < Light(5)). Keep asserting exactly ONE Weapon hardpoint on scout
  (GLB Weapon_1 skipped). Garrison def: 18 hardpoints (12 Light, 5 DockingEntrance, 1 DockingExit).
- NEW ContentTest check (the muzzle == GLB*ws proof): load `SimAssets.TryLoad("ships/fighter.glb")`,
  compute ws = 5.5f / model.LongestAxis, find HP_Weapon_0, assert fighter def hardpoint[0]
  Off ≈ pos*ws and Dir ≈ fwd (1e-4). Also assert bomber hardpoint[1] (torpedo) still at the
  YAML (0,-0.8,2) — override knob works. Determinism check (existing) now covers the merge.
- tests/FactionsTest/Program.cs (:66-71, :181-191): raw stock-bundle YAML asserts — scout
  hardpoints count 2 (weapon, cockpit; main-engine deleted); bomber [1] weapon-id 5 still
  holds; garrison Hardpoints.Count == 0.
- All World-ctor call sites in tests (step 4).

Run:
```
dotnet test factions/tests/Allegiance.Factions.Tests
dotnet run --project tests/FactionsTest -c Release
dotnet run --project tests/ContentTest -c Release
dotnet run --project tests/FlightModelTest -c Release   # + Aleph/Mine/Missile/Shield/Fog/Rescue/Strategy/Collision
dotnet run --project server/SimServer.csproj -- --selftest
dotnet run --project server/SimServer.csproj -- --pregen-assets
```
Client smoke: `scripts/run-server.sh` + `scripts/run-client.sh` (client arg `--autofly`
auto-spawns a Fighter — ShipController.cs:169): bolts leave the visible barrels, glow sits
on the GLB nacelles, nav beacons blink team-tinted on ships and pod, base dock/markers
unchanged. No protocol bump (wire layout identical; only values/counts change — counts are
length-prefixed bytes, base 18 < 255).

## Risk list

1. Fighter GLB boosters at z≈+0.06 (mid-hull): glow/trail move forward vs the old -2.75.
   If it reads wrong in the smoke test, fix the GLB in tools/ship-gen — not the pipeline.
2. Scout muzzle moves (0,0,3) → ≈(0,-0.97,1.5); bomber cannon becomes right-barrel
   asymmetric. Minor gameplay-feel changes; no test asserts positions.
3. Scout GLB Weapon_1 stays dark (one YAML gun) — logged, by design.
4. ContentLoader now requires the assets dir for stock content (boot error otherwise);
   published layout + in-repo test probing both resolve it. Docker: assets ship in the image.
5. Trail anchor (AvgZ of nozzles) shifts with the new booster positions — cosmetic.
6. `.simmodel` cache: hash-keyed, hardpoints already in format v1 — no invalidation needed.
7. Spread-seed / weapon index stability: preserved by append-at-end + unchanged YAML weapon order.
8. Old clients: none (same wire format; defs streamed).

## Implementation order

1. Library nullable Hardpoint fields + projection `?? 0` (compiles everywhere).
2. HardpointGeometryMerge + ContentLoader wiring + validation rules.
3. YAML strips (hulls.yaml, stations.yaml).
4. World.cs table removal + ctor threading (all call sites).
5. Client glow-kind fix + beacons.
6. Schema regen + docs.
7. Test updates, full suite, --selftest, client smoke.
