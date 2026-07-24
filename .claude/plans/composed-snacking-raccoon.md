# Launch/dock restriction by station class + largest-door gating + exitless-base launch gate

## Context

Today any hull launches from and docks at ANY friendly alive base — `ResolveLaunchBase` (`server/Sim/Simulation.cs:1647`) checks only team + health, and the Devastator's "needs a shipyard" gate is just a team-wide tech (`required-techs: [shipyard-1]`, flagged as placeholder in hulls.yaml). This implements the `.PLAN/README.md:25` item, per user decisions:

1. **Config attribute** (not hardcoded): hulls author `launch-station-classes: [shipyard]` — a list of the existing station `class:` keywords. Omitted/empty = current behavior. Shipyard (base-type-id 3) and Shipyard-Dry (6) are both `class: shipyard`, so one entry covers both.
2. **Applies to docking too**: a restricted hull bounces off friendly bases whose class isn't listed, exactly like an enemy base.
3. **Largest-door rule**: at an allowed base, a restricted hull may only dock through the base's LARGEST docking door (by rectangle area from the HP_DockingEntrance quad-face geometry); side doors stay small-ship-only. *Measured evidence rules out a geometric fit rule:* bomber (len 9.6) must keep using supremacy's only door (min dim 18.1, ratio 1.89) while the devastator (len 20) must be excluded from acs05's side door (min dim 37, ratio 1.85) — no margin factor separates them, and the sim collides all ships as a uniform r=3 sphere anyway. acs05 doors: front +X 97×91 (the capital door), top 46×37. Discrete rule = zero new authoring, byte-deterministic on both peers.
4. **Exitless bases can't launch**: LAUNCH greys when the selected base's model has no authored `HP_DockingExit`; server rejects to match. All four stock station models have ≥1 exit today (ss27: 2, ss90: 1, ss21a: 1, acs05: 1) so this is latent, but must not break model-less test-sim bases (see D4).

### Key design decisions

- **D1 — Encoding**: `ushort LaunchClassMask` appended at the TAIL of `ShipClassDef` (bit = `(int)StationClass`, 0 = unrestricted). Authoring: `List<StationClass> LaunchStationClasses` on `Hull`; YamlDotNet already parses kebab enum keywords (same mechanism as `class: shipyard` — `CoreSerializer.cs:16,24`), so an unknown keyword hard-fails at boot for free. Add mirror enum `StationClassId : byte {Starbase, Garrison, Shipyard, Ripcord, Mining, Research, Ordnance, Electronics}` to `shared/Defs.cs` (CapabilityId precedent — shared/ never references the factions library).
- **D2 — BaseTypeId→StationClass**: lookup built from the station catalog on BOTH peers (server `Content.StationCatalog`, client `DefRegistry` catalog). Verified the catalog streams ALL authored stations regardless of tech gating (`FactionsContentProjection.cs:66-69`, `Protocol.WriteStationCatalog:1534`). No BaseDef wire change.
- **D4 — Exit detection**: `World.LoadBaseModel` (`World.cs:1089-1116`) gives BOTH "GLB missing" and "GLB has zero exits" the same 1-element fallback `Exits`, so add `bool HasAuthoredExits` to `BaseModelData` (set only on the GLB path). Launch-capable ≜ `Model is null || HasAuthoredExits` — headless test sims (no assets → sphere bases) keep today's behavior. Client mirror: streamed `BaseDef.Hardpoints` already carries `DockingExit` kinds; `ModelName == ""` → capable.
- **D5 — Rejection semantics**: follow the existing silent-drop precedent (`ProcessRespawns` Simulation.cs:1635-1639) — server drops the join pre-charge; the client pre-check gains the mirror gate so doomed sends are suppressed with a visible reason (existing `SpawnHint`/`_launchHint` path).
- Wire discipline: tail-append + dated comment in `shared/Net/Wire.cs`, NO ProtocolVersion bump (2026-07-18 lockstep precedent).

## Steps

### 1. Authoring + content

- `factions/src/Allegiance.Factions/Model/Hull.cs`: add `public List<StationClass> LaunchStationClasses { get; set; } = new();` in the StellarAllegiance extension block, doc comment stating launch+dock+largest-door semantics. (OmitEmptyCollections keeps round-trips clean.)
- `factions/src/Allegiance.Factions/Validation/CoreValidator.cs` `ValidateHulls` (~:50): parse-time sanity only — error when the list is non-empty on a hull with no `class-id`. **No reachability validation** (user rule).
- `server/Content/core/hulls.yaml` devastator (~:290): add `launch-station-classes: [shipyard]` next to `required-techs` (which stays unchanged).
- Regenerate `schemas/allegiance-core.schema.json`: `dotnet run --project server -- --gen-schemas` (do not hand-edit).

### 2. Shared defs + wire

- `shared/Defs.cs`: append `public ushort LaunchClassMask;` after `RequiredTechIdx` (:177) + the `StationClassId` enum next to `CapabilityId` (:368).
- `server/Content/FactionsContentProjection.cs` `ProjectShip` (:222): `LaunchClassMask = LaunchMask(h.LaunchStationClasses)` — static helper OR-ing `1 << ((int)c & 15)`.
- `server/Net/Protocol.cs` `WriteShipDefs`: `w.Write(s.LaunchClassMask);` after the tech list (:1394), dated comment.
- `client/scripts/GameNetClient.cs` ship reader: `d.LaunchClassMask = r.ReadUInt16();` after `ReadTechList` (:1729).
- `shared/Net/Wire.cs`: dated changelog paragraph above `ProtocolVersion = 35` (:98).

### 3. Shared dock rules (both-peer parity)

- **New `shared/Collision/DockRules.cs`**:
  - `const byte UnknownStationClass = 255`
  - `bool ClassAllowed(ushort mask, byte stationClass)` → `mask == 0 || (stationClass < 16 && (mask & (1 << stationClass)) != 0)`
  - `int LargestFaceIndex(DockFace[] faces)` → max `Eu*Ev` area, tie → lowest index, −1 if empty (plain loop, deterministic)
  - `int AllowedFace(ushort mask, DockFace[] faces, int largestFaceIndex)` → `mask == 0 ? -1 : largestFaceIndex`
- `shared/Collision/Collide.cs`:
  - `IntersectsDockFace` (:437): add `int onlyFace` overload (restrict loop when ≥0); existing 4-arg signature forwards `-1` (call sites compile unchanged).
  - `StaticBody` (:124): append `readonly byte StationClass` (default 255) + `readonly int LargestDockFace` (default −1); extend both `BaseHull` factories (:169, :176); other factories pass defaults.
  - `ResolveStatics` (:238) + `Touches` (:276): add `ushort launchClassMask` param; own-base carve-out becomes `ClassAllowed(mask, b.StationClass) && IntersectsDockFace(..., AllowedFace(mask, b.DockFaces, b.LargestDockFace))`. Mask 0 reproduces today's bytes exactly.

### 4. Server enforcement (`server/Sim/`)

- `World.cs`: `BaseModelData` (:187) gains `HasAuthoredExits` + `LargestDockFace` (set in `LoadBaseModel` :1089 via `DockRules.LargestFaceIndex`); accessors `BaseLaunchCapableOf(byte)` (`Model is null || HasAuthoredExits`) and `BaseLargestDockFaceOf(byte)`.
- `Simulation.cs` ctor (~:595): `byte[256] _stationClassByBaseType` filled from `content.StationCatalog` (255 = unknown); helpers `StationClassOfBaseType(byte)`, `LaunchClassMaskFor(byte cls)` (def lookup, 0 fallback — pods/pigs/miners/constructors all mask-0).
- **Replace `ResolveLaunchBase` (:1647) with `TryResolveLaunchSite(team, cls, requestedBaseId, out BaseSite? site)`** — returns false = REJECT (no charge):
  - local func `CanLaunchFrom(i)` = friendly + alive + `BaseLaunchCapableOf` + `ClassAllowed(mask, class)`.
  - Explicit pick that fails: restricted hull or exitless-but-alive-friendly pick → reject; unrestricted hull with stale/dead/foreign id → legacy silent fallback scan.
  - `requestedBaseId == 0` → first base passing `CanLaunchFrom` (restricted hulls land on an allowed base, not "first").
  - No candidate: `return mask == 0 && !TeamHasAnyBase(team)` (unrestricted + zero bases keeps today's `PlaceAtBase(null)` sector default; restricted → reject).
- `ProcessRespawns` (:1613): call `TryResolveLaunchSite` BEFORE `TryReserveSpawn` (:1638); on false → `continue` (silent drop, no charge). `SpawnCombatShip` (:1281) signature: `ulong launchBaseId` → `World.BaseSite? launchSite` (ProcessRespawns is its only launch-base caller — verified). Update `EnqueueJoin` + ClientHub MsgSpawn comments.
- `ResolveOwnBaseDock` (:958): head check `if (!ClassAllowed(mask, StationClassOfBaseType(b.BaseTypeId))) { ResolveBaseCollision(...); return false; }`; the dock-face test (:964) gains the `AllowedFace(...)` filter. Model-less sphere branch (:985) unchanged (class check already ran). Miners hit `OffloadMiner` before this matters and fly mask-0 hulls; pigs fly Scout/Fighter/Bomber only (`Simulation.Pig.cs:282-284`).
- Autopilot (`PlayerAutopilot` case 1 ~:1888): restricted hull at a class-disallowed friendly base → enemy-style `ArriveAt(...standoff)` instead of `DockApproach` (no shell-grinding). `SelectDockDoor` (:2263): pin `ApDockDoor` to `AllowedFace(...)` when ≥0 before the argmin loop — DockAlign/DockCreep then steer at the big door. (Client has no dock-face steering — verified prediction-only usage.)

### 5. Client prediction parity

- `client/scripts/DefRegistry.cs`: `StationClassOfBaseType(byte)` (cached byte[256] from station catalog), `LaunchClassMask(byte cls)`, `HullMayLaunchFrom(byte cls, byte baseTypeId)`, `BaseLaunchCapable(byte baseTypeId)` (no def/empty ModelName → true; else `Hardpoints.Any(Kind == DockingExit)`).
- `client/scripts/CollisionWorld.cs` `AddBase` (:193): pass station class + `DockRules.LargestFaceIndex(faces)` into the extended `BaseHull` factory.
- `client/scripts/PredictionController.cs` (:156): plumb local hull's mask into `Collide.ResolveStatics` (set where the local hull binds; pods → mask 0).
- `client/scripts/world/CollisionSystem.cs` (:68): `Touches` gains the per-ship mask (thud SFX parity).

### 6. Client UX

- `client/scripts/world/TeamStateStore.cs`: `SpawnGate` (:67) gains `WrongBase`. New overload `CheckSpawnGate(team, cls, bool wrongBase)` — order: Locked → **WrongBase** → TooPoor (base-fix hint beats cost warn); 2-arg overload forwards `false` so all existing `== Locked` filters stand. Store stays Godot/defs-free — callers compute `wrongBase` via DefRegistry.
- `client/scripts/ui/ShipLoadout.Hangar.cs` `RefreshShipCardStates` (:151): `wrongBase = !_defs.HullMayLaunchFrom(classId, selectedBaseType)`; fold selected base + WrongBase bits into `_cardGateSig` (method already runs every `_Process` — sig is the only gate). Card stays **visible-greyed** (hidden-not-greyed rule: situational lock) with sub-label from mask names, e.g. `⚿ SHIPYARD ONLY`. Do NOT extend the hidden-card fallback (:170) to WrongBase.
- `client/scripts/ui/ShipLoadout.cs` `RefreshLaunchGate` (:493): add `wrongBase` + `noBay = !_defs.BaseLaunchCapable(selectedBaseType)`; disable LAUNCH on either; text precedence `⚿ LOCKED` → `SHIPYARD ONLY` → `NO LAUNCH BAY` → `OVER CAPACITY` → `◆ LAUNCH`.
- `client/scripts/ShipController.cs` pre-send gate (:475-513): resolve `SelectedBaseId → TypeId` via `_world.Bases.Known()`; `SelectedBaseId == 0` skips the new checks (server default-resolves); on refusal set `SpawnHint` ("Devastator can only launch from a Shipyard" / "selected base has no launch bay") — suppresses the doomed send via the existing hint path.
- `client/scripts/ui/CommandSidebar.cs`: minimal — `SetRowHints(IReadOnlyDictionary<ulong, string>)` recomputed by ShipLoadout on ship-select + sidebar refresh; dims incompatible base rows with a status line ("CAN'T LAUNCH DEVASTATOR" / "NO LAUNCH BAY"). Rows stay clickable (Build/Research tabs need them). `RememberDockedBase` needs no change (a restricted hull can only dock where it can relaunch).

### 7. Docs

- `GLOSSARY.md`: new mechanics entry (launch-station-classes, largest-door rule, exitless-base gate; key files).
- `.PLAN/README.md`: remove line 25 (implemented).
- `.claude/skills/hulls-weapons/SKILL.md`: document the new hull key.

### 8. Tests

- `tests/StrategyTest/Program.cs` (model-less sphere bases = launch-capable fallback path):
  1. Devastator unlocked, enqueue from garrison id → **no ship, credits unchanged** (pre-charge reject).
  2. `World.CreateBase(0, baseTypeId: 3, ...)`, enqueue from it → spawns, cost charged. Also `launchBaseId: 0` default-pick lands on the shipyard.
  3. Dock: devastator teleported into garrison dock sphere → bounced, alive; into shipyard dock sphere → docked, `PaidCost` refunded.
  4. Regression: scout from garrison + scout with dead-base id still silently falls back.
- `tests/CollisionTest/Program.cs` (probes real GLBs): `DockRules.LargestFaceIndex` on acs05 picks the ~97×91 front door; `IntersectsDockFace(onlyFace: largest)` accepts a big-door point, rejects a top-door point. **4 pre-existing failures — baseline; count must not grow.**
- `tests/FactionsTest`: YAML round-trip preserves `LaunchStationClasses`; empty list omitted.
- `tests/ContentTest`: assert stock devastator projects `LaunchClassMask == 1 << 2`.

## Verification

1. `dotnet build wivuullegiance.slnx`; CSharpier on touched files ONLY.
2. Run suites: FactionsTest, ContentTest, StrategyTest, CollisionTest (baseline the 4 known failures), LoadoutTest, AutopilotTest.
3. Server boot self-test (`server/Assets/SelfTest.cs` asserts per-type doors) — boot once and watch.
4. Wire change isn't covered by dotnet suites → `verify` skill: server + headless client `--autofly` (scout parses new MsgDefs tail); then scripted session — garrison selected → devastator card greys "SHIPYARD ONLY" + LAUNCH disabled; launch from shipyard works; fly at acs05 top door → bounce, front door → dock; autopilot-dock at garrison → parks at standoff. Screenshot evidence.
5. Boot sanity: `class: shipyardd` typo → YAML enum error refuses boot; `--gen-schemas` diff shows only the new key.

## Files touched

`factions/.../Model/Hull.cs` · `factions/.../Validation/CoreValidator.cs` · `server/Content/core/hulls.yaml` · `schemas/allegiance-core.schema.json` (regen) · `shared/Defs.cs` · `shared/Collision/DockRules.cs` (new) · `shared/Collision/Collide.cs` · `shared/Net/Wire.cs` · `server/Content/FactionsContentProjection.cs` · `server/Net/Protocol.cs` · `server/Net/ClientHub.cs` (comments) · `server/Sim/World.cs` · `server/Sim/Simulation.cs` · `client/scripts/GameNetClient.cs` · `client/scripts/DefRegistry.cs` · `client/scripts/world/TeamStateStore.cs` · `client/scripts/ShipController.cs` · `client/scripts/ui/ShipLoadout.cs` · `client/scripts/ui/ShipLoadout.Hangar.cs` · `client/scripts/ui/CommandSidebar.cs` · `client/scripts/CollisionWorld.cs` · `client/scripts/PredictionController.cs` · `client/scripts/world/CollisionSystem.cs` · `GLOSSARY.md` · `.PLAN/README.md` · `.claude/skills/hulls-weapons/SKILL.md` · `tests/StrategyTest` · `tests/CollisionTest` · `tests/FactionsTest` · `tests/ContentTest`
