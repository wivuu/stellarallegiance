# Plan — Decompose WorldRenderer into collaborating classes (M27) + TeamStateStore (M24)

## Context

`client/scripts/WorldRenderer.cs` is a 3,468-line god-class (`partial class WorldRenderer : Node3D` —
the `partial` is only Godot's source-gen requirement, it is one file). It is the client-side renderer
of the networked world and owns ~18 unrelated concerns (ships, bolts, asteroids, bases, fog, warp,
mining, construction, team-state/research, HUD queries, collision, shadow occluders, …), ~110 fields
interleaved with methods that freely reach across every concern.

Splitting it into `partial` files was rejected — that only relocates the same omniscient object. The
goal is **genuine decomposition**: each concern becomes a single-responsibility class that owns its own
state and reaches other concerns only through **narrow, explicit dependencies**. This subsumes the two
deferred review findings: **M27** (god-class split) and **M24** (`TeamStateStore` extraction).

This is CLIENT rendering only — **not** the deterministic server sim, so there is no bit-exact
constraint. The correctness guards are: (1) the client keeps compiling, (2) existing `tests/*` suites
stay at their known baseline, and (3) **visual/behavioral parity** in the running Godot client (only
you can eyeball that — hence the phased gates).

## Chosen architecture (your decisions)

1. **Expose subsystems, rewire consumers.** WorldRenderer exposes each extracted class as a public
   property (`public ShipRenderer Ships { get; }`, `public TeamStateStore TeamState { get; }`, …). The
   ~15 external files change `_world.EnemyShips()` → `_world.Ships.EnemyShips()`, `GameNetClient` routes
   decode to the owning subsystem, and the 6 nested DTOs move to their owning class. **No forwarder
   veneer** — real external boundaries.
2. **Warp/sector/home stays in the coordinator.** The `SetViewSector`/`ApplySectorEnv`/
   `RefreshSectorVisibility`/`HideForWarp`/`Begin+Tick+AbandonWarp`/`RehomePreLaunch`/`HomeSector`
   cluster (~250 lines) is the coordinator's legitimate job; it exposes only narrow `IWarpDriver.BeginWarp`
   (to ShipRenderer) and `IWarpState` (to Fade/Asteroids). No second broad-access class.
3. **Phased milestone gates (A–D).** I compile + run suites at each gate; you run the Godot client and
   spot-check the named subsystems before I continue.

## Naming convention (one rule)

Suffix encodes kind, so a reader knows a collaborator's shape from its name:
- **`…Renderer`** — owns live scene nodes + per-frame visual upkeep, driven by the coordinator's ordered
  fan-out. Plain `sealed class` (NOT a Godot Node — see Risks: `_Process` order is load-bearing).
- **`…Store`** — owns queryable CPU-side state (dicts/lists) + accessors; no nodes, no per-frame work.
- **Services** get role names: `MatchClock`, `Palette`, `PlayerContext`, `SectorView`, `ClipCache`,
  `FadeController`, `WarpState`, plus one stateless static `NodeFx`; alongside existing `DefRegistry` +
  `CollisionWorld`.

All new files live in `client/scripts/world/` (global namespace; csproj auto-globs — no csproj edit).
Mirror `client/scripts/ui/LoadoutState.cs` for the `sealed class` state-holder shape. CSharpier formats
only touched files.

## Class list

### Shared services (constructed first, injected downward)
| Class | File | Kind | Owns |
|---|---|---|---|
| `MatchClock` | `world/MatchClock.cs` | plain | `ServerTick`, `Phase`, `Winner`, `Seconds` — the one tick/phase everyone reads; written only by coordinator `NetSetMatch` |
| `Palette` | `world/Palette.cs` | plain | the 7 shared materials; `ShipMaterial(team,isPig)` etc. |
| `PlayerContext` | `world/PlayerContext.cs` | plain | `LocalTeam`/`LobbyTeam`/`MarkerTeam` identity scalars |
| `SectorView` | `world/SectorView.cs` | service | `_sectors _localSector _viewOverride` + `InSector`/`SetNodeSector`/radius/center/`SectorName`/`MapSectors` (visibility primitives; only coordinator writes `_localSector`) |
| `ClipCache` | `world/ClipCache.cs` | service | the static bolt/sun-occlusion geometry (`_asteroidClip _baseClip`), the seam that decouples Bolts from Asteroid/Base internals |
| `FadeController` | `world/FadeController.cs` | service | `_fades _fadeScratch`; `FadeNode`/`AdvanceFades`/`SetNodeSectorFading`/`RestTransparencyFor` (meta-based, see Couplings) |
| `WarpState` | `world/WarpState.cs` | plain | `Covering`/`Settling`/`NoteSectorRock` — tiny surface Fade + Asteroid-insert read during warp |
| `NodeFx` | `world/NodeFx.cs` | static | stateless movers: `DimNode`, `QuietFade`, mesh-vert collectors |

### Subsystems (each owns its concern's exclusive fields + methods)
| Class | File | Kind | Owns (fields → methods) |
|---|---|---|---|
| `TeamStateStore` | `world/TeamStateStore.cs` | Store | 6 team dicts + `_baseResearch _constructorStates BuildQueueLimit`; all `Team*`/research/constructor-status accessors + `CheckSpawnGate` (DefRegistry injected). **DTOs move here:** `TeamStateSnapshot`, `BaseResearch`, `ConstructorStatus`, `SpawnGate`. |
| `FogStore` | `world/FogStore.cs` | Store | `_ghosts _radarVisible _ghostScratch _contactLostUntil`; `GhostContacts`/`NetSetContacts`/`ContactIsFriendly`/`TryContactPos`/`ContactLostActive`/`OpenContactLostWindow`. **DTO moves here:** `GhostContact`. |
| `ShipRenderer` | `world/ShipRenderer.cs` | Renderer | `_shipNodes _shipShield _shipMounts _mountShadow _pilotNames _reclaimedShipId` + death-cam + `LocalShip` + dock memory; Insert/Update/Delete/PromoteLocal/loadouts/pilot-names; `EnemyShips/FriendlyShips/FriendlyShipById/ShipsInLocalSector`; `static ClassOf/TeamOf` |
| `BaseRenderer` | `world/BaseRenderer.cs` | Renderer | `_baseNodes _baseList _baseType _baseHealthFrac _baseTeams`; `NetAddBase/InsertBase/NetUpdateBaseHealth/BaseMaxHealthOf`; `VisibleBases/LockableEnemyBases/AllVisibleBases/VisibleBaseHealth/KnownBases/MapBaseTeams`; `BaseIsDead/SectorTeamStale` |
| `AsteroidRenderer` | `world/AsteroidRenderer.cs` | Renderer | `_asteroidNodes _asteroidSpins _asteroidRows _rockScaleBasis _rockShrinkTarget _regolithTintCache` + static mesh cache; `NetAddAsteroid/InsertAsteroid/NetUpdateRock/NetRemoveRock/GetAsteroid/AsteroidsInView`; spin+shrink `Tick`; `static AsteroidMesh` |
| `AlephRenderer` | `world/AlephRenderer.cs` | Renderer | `_alephNodes _alephLinks`; `NetAddAleph/InsertAleph/VisibleAlephs/MapAlephLinks` |
| `BoltRenderer` | `world/BoltRenderer.cs` | Renderer | `_bolts _shotMaskMs`; `SpawnBoltFor/SpawnLocalBolt/AddBolt/ClipBoltTtl/SunVisibility/NewProjectileMesh`; `CheckBoltImpacts` + cull `Tick` |
| `MissileRenderer` | `world/MissileRenderer.cs` | Renderer | `_missiles`; `NetUpsertMissile/NetMissileGone` |
| `ProbeRenderer` | `world/ProbeRenderer.cs` | Renderer | `_probes _probeScratch`; `NetUpsertProbe/NetProbeGone/VisibleProbes` |
| `MinefieldRenderer` | `world/MinefieldRenderer.cs` | Renderer | wraps `_chaffFx _minefieldViews`; `NetSpawnChaff/NetUpsertMinefield/NetMineGone/NetMinefieldGone/VisibleMinefields` |
| `MiningBeamRenderer` | `world/MiningBeamRenderer.cs` | Renderer | `_miningBeams _miningBeamPrune _minerTargetRock`; `NetUpdateMinerTargets/UpdateMiningBeams(Tick)/MiningTargetRock/NearestHe3Rock/IsMinerHarvesting` |
| `ConstructionRenderer` | `world/ConstructionRenderer.cs` | Renderer | `_constructorBuilds _buildSpheres _constructorDebris _buildRockRadius`; `NetUpdateConstructorBuilds/UpdateBuildSpheres(Tick)`+helpers/`HasBuildRow/IsRockUnderConstruction`. **DTO moves here:** `ConstructorBuild`. |
| `EnvironmentRenderer` | `world/EnvironmentRenderer.cs` | Renderer | `_occluderScratch _sectorEnvOccluders _hullVertCache` + static mesh-hull cache; `GatherShadowOccluders/UpdateShadowOccluders(Tick)/HullVertsFor`; `static WarmAsteroidVariant` |
| `CollisionSystem` | `world/CollisionSystem.cs` | plain | `_collidingShips _collidingPairs _pairScratch _shipObstacleScratch`; `CheckCollisions(Tick)/ShipObstacles/PlayCollisionSfx` |

`AsteroidAmbience` (already a Node) stays a coordinator child.

## What WorldRenderer becomes (`~500 lines`, real coordinator)

1. **Node-tree parent + container ownership.** Builds the six group `Node3D`s + `_staticGroups`/
   `_transientGroups` in `_Ready`; injects each container into its renderer (renderers `AddChild` into it).
   Container-level visibility partition + single-pass `Reset` stay generic (must not know which subsystem
   owns a node).
2. **Graph construction / `_Ready` wiring** — build services, then all subsystems (ctor-inject services +
   containers), then a **two-phase `Wire()`** to inject the peer interfaces that form cycles.
3. **Exposes subsystems as public properties** — `public ShipRenderer Ships { get; }`, `Bases`, `Asteroids`,
   `Alephs`, `Bolts`, `Missiles`, `Probes`, `Minefields`, `Mining`, `Construction`, `Fog`, `TeamState`,
   `Environment`, `Collision`, plus services where a consumer needs them (`SectorView`, `MatchClock`).
4. **`_Process` fan-out** — the ordered pipeline, unchanged order: warp Phase-B → death-cam expiry →
   `Bolts.CheckBoltImpacts` → `Collision.CheckCollisions` → `Asteroids.Tick` (spin+shrink) → `Mining.Tick`
   → `Construction.Tick` → `Fade.AdvanceFades` → `TickWarpSettle` → `Environment.Tick(refPos)` →
   `Ambience.Tick(refPos)` → `Bolts.CullTick`.
5. **`Reset` fan-out** — free group children, call `subsystem.Reset()` in the current clearing order, reset
   services, then `AbandonWarp` + `ApplySectorEnv(HomeSector)`.
6. **Warp/sector/home cluster stays here** (decision 2) — implements `IWarpDriver`/`IEffectSink`, owns
   `SectorView` mutation, reads `Bases.BaseTeams` for `HomeSector`, calls `Environment.Gather`/`Starscape`/
   `SectorEnvironment` for `ApplySectorEnv`. Events `Warped`/`WarpSettled` stay declared here (Hud subscribes).
7. `static WarmAsteroidVariant` forwarder so `AssetPreloader` is untouched.

## Dependency graph + injection

**Style:** constructor-inject concrete services + **narrow read interfaces** for peer subsystems; consumers
never receive the coordinator, so the god-object cannot re-form. Two-phase `Wire()` breaks the cycles.

Narrow interfaces (in `world/`): `IShipQuery` (Nodes, TryGetShield, LocalShip, Count → Bolts/Collision/
Mining/Construction/Fog); `IAsteroidQuery` (GetAsteroid/Node/InView → Mining/Construction/Environment);
`IProbeQuery`, `IAlephQuery` (→ Bolts); `IBaseQuery` (BaseList/BaseTeams → Environment, coordinator
HomeSector); `IBoltSource` (SpawnBoltFor → Ships); `IShipObstacleSource` (ShipObstacles → Collision);
`IBuildQuery` (HasBuildRow/IsRockUnderConstruction → Collision); `IContactLostSink` (OpenContactLostWindow
→ Ships); `IEffectSink` (SpawnEffect → Bolts/Missiles/Probes/Construction, implemented by coordinator);
`IWarpDriver` (BeginWarp → Ships); `IWarpState` (Fade/Asteroids).

Two intentional cycles, broken by `Wire()`: **Ships↔Bolts** (`IBoltSource` in / `IShipQuery` out) and
**Ships↔Collision** (`IShipObstacleSource` in / `IShipQuery` out). Construct all subsystems, then `Wire()`.

`ShadowRefPos` needs the live camera (a Node call) — the coordinator computes `refPos` and passes it into
`Environment.Tick`/`Mining.Tick`/`Ambience.Tick` rather than injecting the viewport into plain classes.

## Coupling resolutions (verified against the code)

- **Bolts → `_asteroidClip`/`_baseClip`** → the `ClipCache` service. `AsteroidRenderer`/`BaseRenderer`
  insert-paths call `ClipCache.AddAsteroid/SetAsteroidRadius/RemoveAsteroid/AddBase`; `BoltRenderer` reads it.
- **Fade → Bases (`RestTransparencyFor` walked `_baseList`)** → replaced by a `restTransparency` **node meta**.
  `BaseRenderer.NetUpdateBaseHealth` stamps `node.SetMeta("restTransparency", 0.55f)` on the alive→dead edge;
  `FadeController.RestTransparencyFor(n)` reads the meta. Mirrors the existing `sector`/`shadowRadius` meta
  pattern (confirmed present in the file). `FadeController` then references no subsystem.
- **Bases → Fog** → `FogActive` is just `_defs.FogOfWar` (confirmed); `BaseRenderer` reads `DefRegistry`
  directly, no Fog dependency. Dim uses `NodeFx.DimNode` (static).
- **Ships → Warp (`UpdateShip`)** → injected `IWarpDriver.BeginWarp(destSector)`; Ships computes `warped =
  newRow.SectorId != SectorView.LocalSector` and fires one intention; coordinator's `BeginWarp` runs the
  Phase-A body (`_localSector=…; HideForWarp; _pendingWarpSector=…; Warped?.Invoke()`).
- **Bolts → Ships/Probes/Aleph (`CheckBoltImpacts`)** → the three loops iterate injected `IShipQuery`/
  `IProbeQuery`/`IAlephQuery`; effects via `IEffectSink`.
- **Construction → Asteroids/Fade/Collision/Ships/Effects** → `IAsteroidQuery` + `CollisionWorld` +
  `IShipQuery` + `IEffectSink`; rock dim/undim via `NodeFx.DimNode` + the `restTransparency` meta.

## Nested-DTO relocation (with external ref updates)

| DTO | New home | External constructors to update |
|---|---|---|
| `TeamStateSnapshot` | `TeamStateStore` | `GameNetClient.cs:1315` |
| `BaseResearch` | `TeamStateStore` | `GameNetClient.cs:1336,1347`; pattern-matches in `ResearchTab.cs`, `CommandSidebar.cs` |
| `ConstructorStatus` | `TeamStateStore` | `GameNetClient.cs:967,979`; `BuildTab.cs` |
| `SpawnGate` (enum) | `TeamStateStore` | pre-launch caller(s) of `CheckSpawnGate` |
| `GhostContact` | `FogStore` | `GameNetClient.cs:1614,1627` |
| `ConstructorBuild` | `ConstructionRenderer` | `GameNetClient.cs:942,950` |

## Consumer rewiring (decision 1)

Change `_world.X(...)` → `_world.<Subsystem>.X(...)` and requalify moved DTO names. Representative files
(each keeps its single `_world` field and reaches subsystems through the new properties):
- `GameNetClient.cs` — routes all ~40 `Net*` decode calls to the owning subsystem property; constructs
  relocated DTOs by their new names.
- `Hud.cs` — `_world.EnemyShips()`→`_world.Ships.EnemyShips()`, `VisibleBases`→`_world.Bases.…`, credits/
  score→`_world.TeamState.…`, contacts→`_world.Fog.…`, keeps `_world.Warped` subscription.
- `TargetMarkers.cs`, `SectorOverview.cs`, `Minimap.cs`, `LensFlare.cs`, `CameraRig.cs`, `ShipController.cs`
  — retarget their `_world.X` reads to the owning subsystem.
- `ui/BuildTab.cs`, `ui/ResearchTab.cs`, `ui/TechDetailPanel.cs`, `ui/ShipLoadout(.Hangar).cs`,
  `ui/CommandSidebar.cs`, `DefRegistry.cs` (`MigrateWeaponTier`), `Chat.cs`, `Lobby.cs` → `_world.TeamState.…`.

## Migration sequence — 4 milestones (compiles + visually identical at each gate)

Leaf state/producers first, cross-cutting orchestration last; a producer is extracted before its consumers
where possible.

**Milestone A — Shared services + the two Stores + M24.**
Extract `MatchClock`, `Palette`, `PlayerContext`, `SectorView`, `NodeFx`, `WarpState`; then `TeamStateStore`
(with its 4 DTOs) and `FogStore` (with `GhostContact`). Expose the properties; rewire team-state + fog +
GameNetClient consumers; move the DTOs. Add a headless **`tests/TeamStateStoreTest`** (Apply→OwnsTech/HasAll,
miner count/cap, credits/score, rock-class discovery, research round-trip + progress, constructor status).
→ **GATE A:** build + suites + new test green. You verify: Build tab affordances/prices/lock + miner N/M +
rock-class gating; Research tab clusters/timers/banners; CommandSidebar research %; HUD credits/score;
contact chime + "CONTACT LOST" toast + ghost glyphs; match-end banner.

**Milestone B — Static-world producers: Bases, Asteroids, Aleph + `ClipCache` + `FadeController`.**
Extract `ClipCache`, `BaseRenderer`, `AsteroidRenderer`, `AlephRenderer`, `FadeController`. Apply the
`restTransparency` meta (resolves Fade/Bases). Move rock spin+shrink into `AsteroidRenderer.Tick`. Rewire
Hud/TargetMarkers/SectorOverview/Minimap base+asteroid+aleph reads.
→ **GATE B:** Welcome-frame world builds; stations, per-rock regolith tint, mining shrink, rock-gone
dissolve, aleph funnels, stale-base ghost dim, fog-reveal fade-in, minimap tints, bolt/sun occlusion vs
rocks+bases (via ClipCache), base damage bars.

**Milestone C — Dynamic + combat: Ships, Bolts, Missiles, Probes, Collision, Mining, Construction.**
Extract these; do the two-phase `Wire()` (Ships↔Bolts, Ships↔Collision); route `UpdateShip→BeginWarp`;
move `CheckBoltImpacts`/`CheckCollisions`/`UpdateMiningBeams`/`UpdateBuildSpheres`/bolt-cull into `Tick`s
(coordinator fans out in the same order). Move `ConstructorBuild` DTO; rewire GameNetClient combat/mining/
construction routes + any ShipController `LocalShip` reads.
→ **GATE C:** local prediction + collision thud; remote-ship bolt synthesis + hit sparks/shield flashes;
death blast + death-cam; dock/relaunch defaults; warp cover→swap→reveal; missiles/probes/chaff/mines;
mining beams to exact rock; constructor build spheres + drone hide + rock dissolve; Tab-cycle/lock; autopilot.

**Milestone D — `EnvironmentRenderer` + coordinator slimming.**
Extract shadow-occluder gather/`Tick`. Reduce `WorldRenderer` to the coordinator role above; confirm the
only remaining bodies are `_Ready` wiring, `_Process`/`Reset` fan-out, subsystem properties, the warp/
sector-view/home cluster, `IWarpDriver`/`IEffectSink`, and the `WarmAsteroidVariant` forwarder.
→ **GATE D:** per-sector sun + 3D dust shadow volumes track camera + rock tumble; reconnect `Reset`→
re-Welcome rebuilds cleanly; full `tests/*` suites at baseline.

## Verification tooling

- **I run headlessly each gate:** `dotnet build client/stellarallegiance.csproj -c Release`,
  `dotnet build wivuullegiance.slnx -c Release`, the new `TeamStateStoreTest`, and the existing `tests/*`
  suites (baseline = the known pre-existing failures only).
- **You run the Godot client** for visual parity (I can't render). Self-drive flags that make gates easy:
  `--autofly` (combat/ships/bolts — Gate C), `--warp-test` (warp — Gate C), `--hangar` (loadout/build UI),
  `--ui-showcase`/F9 (UI). Research/Build tabs via in-game keys for Gate A/B.
- Each milestone ends **green + auto-committed** before I hand the gate to you.

## Risks & mitigations

- **`_Process` order is load-bearing** (bolt-impacts before cull; warp Phase-B before visibility reads;
  shrink before mining aim) → subsystems are plain classes driven by explicit coordinator fan-out, NOT
  self-driven Node `_Process`. Do not convert renderers to Nodes.
- **Ships↔Bolts / Ships↔Collision cycles** → narrow interfaces + two-phase `Wire()`; a third cycle would
  use a one-shot event/callback, never a back-reference.
- **Consumer rewiring churn (~15 files + GameNetClient)** → a missed site is a compile error, caught
  immediately; phased gates isolate any behavioral regression to one milestone's subsystems.
- **Scratch-buffer zero-alloc contract** — each `_xScratch` moves with its accessor; the "read immediately,
  don't retain" contract is preserved (a stray copy would add per-frame GC).
- **Shared static caches** (`_asteroidMeshes`/`_meshHullVertCache`, warmed by `AssetPreloader`) → split to
  their owners; keep the `WorldRenderer.WarmAsteroidVariant` static forwarder so AssetPreloader is untouched.

## Out of scope
No wire/protocol changes, no server changes, no gameplay/behavior changes. M28 (Lobby factory-kit) is
separate. No blanket CSharpier reformat — touched files only.
