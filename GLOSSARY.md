# Glossary — Stellar Allegiance

Reference guide for common terminology across the Stellar Allegiance codebase. Organized by domain with key file locations for modification and extension.

**When to update:** Add new entries when introducing new gameplay systems, content mechanics, or architectural patterns. Cross-reference CLAUDE.md memories and related terms for consistency.

---

## Simulation & Physics

### World Layout Seed
Single `ulong` from which the whole static arena — base positions, asteroid fields/belts, aleph pair, dust clouds — is deterministically placed (`new World(seed, …)`). By DEFAULT it is rolled fresh (cryptographically random) per match, so each match reshuffles the layout even on the same map and players must re-scout. Pin it with `SIM_SEED` / `--seed N` (flag wins over env) to rebuild an EXACT arena for tests/benchmarks/bug repro. Server-side only — clients never re-derive from the seed; every static is streamed per-entity via Welcome/MsgReveal.
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Program.cs` — seed sourcing (random default, `SIM_SEED`/`--seed` pin), per-match reroll in `BuildWorldForMap`
  - `server/Sim/World.cs` — `Seed`; `DetRng` streams for bases (`seed ^ 0xB453…`), asteroids/alephs (`DetRng(seed)`), dust (own RNG)
  - `tests/FogTest/Program.cs` — same-seed identical / different-seed differ layout assertions
- **Related:** [[Minefield]], [[Per-Sector Environment (God Rays / Nebula / Dust Clouds)]]
- **Notes:** Each rolled match seed is logged (`match world: … seed=…`) so any live layout is reproducible with `--seed`. Asteroids + alephs share `DetRng(seed)` (reroll together by design); dust runs on its own RNG so it never perturbs rock/aleph placement. Rock placement enforces minimum spawn spacing (`seeding.rock-min-gap` rock↔rock, `seeding.base-clearance` rock↔base; surface-to-surface, 0 disables) by deterministic rejection sampling — a rock that can't fit after a fixed attempt count is dropped, and ore classes are assigned afterwards from the survivors so guaranteed He3/special counts are unaffected. Aleph gate mouths in the SAME sector are likewise spaced (`seeding.aleph-min-gap`, centre-to-centre, 0 disables) so two gates never overlap by chance — but a gate is REQUIRED for connectivity and is never dropped: if no roll clears the gap the best-separated one is kept.

### Flight Model
Core deterministic physics system shared between server and client for ship movement, thrust, and rotation.
- **Frequency:** Very common
- **Key Files:** 
  - `shared/FlightModel.cs` — deterministic physics (shared across server/client)
  - `client/scripts/PredictionController.cs` — client-side input prediction and reconciliation
  - `server/Sim/Simulation.cs` — authoritative server simulation loop (20 Hz tick)
- **Related:** [[SimTick]], [[Held-Input Replay]]
- **Notes:** Server is single source of truth; client predicts and reconciles against server snapshots

### SimTick
Server's authoritative 20 Hz simulation loop that drives all gameplay state updates.
- **Frequency:** Very common
- **Key Files:**
  - `server/Sim/Simulation.cs` — main loop tick handler
  - `server/Sim/Simulation.Pig.cs` — pig brain decision integration
  - `server/Net/Protocol.cs` — snapshot quantization and transmission
- **Related:** [[Flight Model]], [[PigBrain]]
- **Notes:** Never blocks on network I/O; runs deterministically regardless of client connections

### Held-Input Replay
Client technique for smoothing input predictions: store held inputs, replay against server state deltas, avoid jitter when server corrects position.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/PredictionController.cs` — input history buffer and replay logic
  - `shared/FlightModel.cs` — deterministic replay against consistent physics
- **Related:** [[Flight Model]], [[Client Prediction]]
- **Notes:** Prevents popcorn/jitter when client overshoots and server corrects

### AOI (Area of Interest)
Distance-based visibility culling: server only streams entities within fixed distance tiers from each client.
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/World.cs` — entity spatial indexing
  - `server/Net/ClientHub.cs` — per-client entity interest filtering
  - `shared/Defs.cs` — AOI radius constants (SIM_*_RADIUS tuning)
- **Related:** [[Snapshot]], [[Spatial Grid]]
- **Notes:** Fixed nearest-60 replaced by distance tiers + environment knobs (SIM_NEAR_RADIUS, SIM_FAR_RADIUS, SIM_FAR_EVERY)

### Reliable / Lossy Outbound Tiers
Two-tier write discipline on the per-client outbound frame queue (bounded, `FullMode.Wait`). RELIABLE (`SendReliable`) is for one-shot frames with no repair path — Welcome, Defs, YouAre, ShipGone, chat, lobby roster, gone-events, rock deltas; a full queue parks them in the client's `PendingControl`, flushed FIFO next tick (delayed, never lost). LOSSY (`SendLossy`) is for self-healing streams — snapshots, change+keepalive frames, FX; a full queue just drops the write.
- **Frequency:** Every outbound frame
- **Key Files:**
  - `server/Net/ClientHub.cs` — `SendReliable` / `FlushReliable` / `SendLossy`, `OutboundQueueDepth`
- **Related:** [[Snapshot]], [[AOI (Area of Interest)]]
- **Notes:** NEVER use `DropOldest` or raw `TryWrite` for control frames — evicting a one-shot YouAre/ShipGone deadlocks the relaunch flow (client retries MsgSpawn forever; server drops each as "already flying"). Queue pressure is logged throttled (`OutboundQueuePressure`). The client additionally self-heals its local-ship binding from the lobby roster (`GameNetClient.ApplyLobbyState` adopt/ghost heal).

### Compound Base Hull (COL_ parts)
Per-part convex collision for a station: `COL_`-prefixed mesh nodes baked into the base GLB (`garrison.glb` — the shipping base; `Outpost.glb` is retained but unused) each become one sub-hull, replacing the single QuickHull shrink-wrap so ships bounce off the real superstructure and cannot fly into the hollow interior. Parts are GENERATED from the visual mesh volume (voxel solid-fill → marching cubes → CoACD convex decomposition) by `tools/collision-hull/bake.py --kind base --glb <glb>` — never hand-placed.
- **Frequency:** Domain-specific
- **Key Files:**
  - `tools/collision-hull/` — args-driven bake tool for any mesh GLB (per-kind presets, hull-containment / dock-corridor / reachability validations)
  - `shared/Collision/GlbReader.cs` (`CollisionParts`), `SimModel.cs` (`Hulls`), `Collide.cs` (`SphereVsBody` deepest-contact)
  - `server/Sim/World.cs` — `BaseSubHulls`; `server/Assets/SelfTest.cs` + `tests/CollisionTest` — deploy-guard assertions
- **Related:** [[Hull]], [[Dock Refund]]
- **Notes:** Merged-hull metrics stay bit-exact (LongestAxis 32.243610 / BoundingRadius 16.543488 / 172 planes) because every part is clamped strictly inside the visual convex hull. Deterministic bake (same GLB + same resolved args = same SHA). Runtime consumes compound hulls only for bases today. See the `base-collision` and `collision-hull-generator` skills.

### Autopilot / AutoSteer
Server-side hands-off navigation for player ships (protocol v30), reusing the PIG steering. The player picks a target — Tab cycles ships → bases → asteroids in view, or the F3 map left-click selects an entity / drops a grid-plane **waypoint** — and presses **T** (`engage_autopilot`) to fly there. `AutoSteer` is a shared, pure static extraction of the PIG steer/attack bodies (float-identical, so it's determinism-guarded by the PIG suites); the server synthesizes input at the `InputFor()` seam exactly like a PIG. Arrival rules: brake-to-standoff on waypoints/rocks/enemy bases, keep-station (never auto-fires) on enemy ships, fly-to-door auto-dock on a friendly base, single-hop `AlephTo` transit cross-sector. Disengage on arrival/dock/death/target-loss or any real manual stick input (cruise-control handback). The client runs **follow-authority prediction** while engaged: it suspends own-ship `Step()` and eases the render onto authoritative snapshots via the reconcile spring (chase cam stays smooth, `ReconcileCount` doesn't climb), and keeps sampling+sending real sticks so the server can detect override.
- **Frequency:** Domain-specific
- **Key Files:**
  - `shared/AutoSteer.cs` — pure `SteerToPoint` / `AttackPoint` (verbatim PIG steering; avoidance injected as a delegate so `shared/` never depends on server `World`)
  - `server/Sim/Simulation.cs` — `ShipSim.ApEngaged/ApKind/ApTargetId/ApWaypoint*`, `EnqueueSetAutopilot`, `InputFor` autopilot branch + `AutopilotStep` + `ManualOverride`; `server/Sim/Simulation.Pig.cs` — thin PIG wrappers over AutoSteer
  - `server/Net/Protocol.cs` — `MsgSetAutopilot=11` (client→server engage/disengage), `ShipFlagAutopilot=16` (echo bit in the ship-record flags byte)
  - `client/scripts/PredictionController.cs` — `SetAutopilot(bool)` follow-authority mode; `client/scripts/ShipController.cs` — T toggle / `EngageAutopilot` / manual-override handback / `ApEngagedLocal`; `client/scripts/SectorOverview.cs` — F3 pick + waypoint; `client/scripts/TargetMarkers.cs` — extended Tab, waypoint diamond, AUTOPILOT banner + disengage toast
  - `tests/AutopilotTest` — approach / standoff / stop+disengage / aleph / avoidance / manual-override / friendly-dock / target-loss
- **Related:** [[Flight Model]], [[PigBrain]], [[Client Prediction]], [[SimTick]]
- **Notes:** Follow-authority (not client-replicated steering) because bit-identical target/fog/rock state client-side is impossible; input latency is irrelevant hands-off, only smoothness matters. The engaged flag broadcasts to all viewers (shared record scratch — accepted v1 leak). ~~Cross-sector routing is single-hop only~~ (multi-hop since Stage-4 mining: the `CrossSector` leg routes via `World.NextGateTo`). *Deferred: enemy-ghost tracking through fog; reuse for constructors.*

### Rock Class / Ore (Mining)
Stage-4 resource layer over the seeded asteroids. Every rock gets a `RockClass` (Carbonaceous / Silicon / Uranium / Helium3, append-only in `shared/Defs.cs`) chosen by a **per-sector deterministic selection pass** — per-rock derived hashes (`Mix(seed, rock.Id)`), never extra draws on the shared world `DetRng` stream, so pinned-seed layouts stay byte-identical (guard-tested golden). Only **Helium-3** is harvestable: each He3 rock rolls an ore capacity (radius-cubed volume factor × world/map richness knobs) and **shrinks volume-proportionally** as it's mined down to a floor fraction, through the single seam `World.SetOreRemaining` (recomputes `CurrentRadius`, re-scales the collision body, flags the change). Live shrink streams as `MsgRockUpdate` (on-change only; fog on → per-team, discovered rocks only); Welcome/Reveal rock statics carry class + current radius + ore % so late joiners are never stale. **A rock's mesh/texture now reflects its class**: after the class pass, `World.AssignVariants` overwrites each rock's cosmetic `Variant` with one from that class's pool (`AsteroidShapes.VariantForClass`, keyed on the same per-rock `OreMix` hash — never the shared RNG, so positions stay byte-stable), and the rare **special** rocks (Carbonaceous/Silicon/Uranium — not He3) are landmark-**oversized** by `SpecialRockRadiusMult` (stock 3×). Each of the 5 classes owns a pool of `tools/asteroid-gen` variants sharing that class's PBR "kind" (He3 = pale-cyan crystal, Regolith = dull grey-brown dust, Uranium = green-steel, Silicon = tan, Carbonaceous = charcoal). The client stays dumb (loads the streamed variant name); the server's collision hull keys off the same class-derived `Variant`, so hull and visual match.
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/World.cs` — `RockOre` (OreState dict), `RockClassOf`, `RockCurrentRadius`, `SetOreRemaining`, class/capacity seeding, `AssignVariants` (class→mesh); `shared/Defs.cs` — `RockClass`, `WorldSeedingTuning` rock-class knobs (`He3PerSector`/`He3PerHomeSector`/`SpecialPerSector`/`HomeSpecialChance`/`SpecialRockRadiusMult`; per-sector `He3Count`/`SpecialCount` overrides on `SectorConfig`), `WorldMiningTuning` (harvest/economy + ore capacity)
  - `shared/AsteroidShapes.cs` — `Variants[]` (wire-indexed mesh names) + `ClassPools`/`VariantForClass`/`PoolFor` (RockClass → mesh pool); `tools/asteroid-gen` (`shapefield.py` `_MATERIAL`/`_SHAPE` per-class kinds, `asteroids.json` catalog) → `client/assets/asteroids/*.glb`
  - `server/Content/core/world.yaml` — rock-class knobs under `seeding:` (incl. `home-special-chance`, stock 0 = no special rock in a garrison sector), harvest/economy under `mining:` (both server-only, not streamed) + `schemas/world.schema.json` / `schemas/map.schema.json` mirrors
  - `server/Net/Protocol.cs` — `WriteRockStatic` (class/radius/orePct), `MsgRockUpdate = 22`, `BuildRockUpdates(For)`
  - `client/scripts/WorldRenderer.cs` — `NetUpdateRock` mesh re-scale + collision update; `client/scripts/TargetMarkers.cs` — class name + `DEPLETED` bracket
  - `tests/MiningTest` — class/capacity determinism, pinned-seed layout golden, shrink arithmetic, wire round-trip
- **Related:** [[World Layout Seed]], [[Fog of War (Team Vision)]], [[Miner (AI ore drone)]]
- **Notes:** Non-He3 classes are cosmetic v1 (future refinery/shipyard hooks). Vision occlusion + PIG avoidance stay at spawn radius (conservative, deferred). Grid membership stays spawn-size — rocks only shrink.

### Miner (AI ore drone)
A team-owned, unarmed AI ship (`ShipSim.IsMiner`, `ShipFlagMiner = 32`, hull class-id 4 with `ore-capacity`) that harvests He3 rocks and offloads ore at friendly bases for team credits — alongside, not replacing, the flat paycheck. Each team seeds **one free miner slot** per match; more are bought from the docked **Build tab** (`MsgBuyMiner=16`, commander-gated, same `TryReserveSpawn` charge seam as player hulls) up to `mining.max-miners-per-team`. `MinerSlot` is a separate pool from `PigSlot`: purchased lifecycle, no wave respawn, no pod — a killed miner's slot is gone until repurchased. Brain at the PIG 5 Hz cadence (`MinerBrainStep`): picks **team-discovered** He3 rocks in **authorized sectors** (`TeamState.AuthorizedMiningSectors`, seeded to the garrison, extended by a commander's `MsgOrder=12` sector task from F3; transit is free, only *selection* is gated), preferring its previous rock, avoiding friendly claims unless miners outnumber rocks, else nearest by gate hops. Steering at 20 Hz (`MinerExecute` via the `InputFor` seam) reuses `AutoSteer` + the 3-phase `DockApproach`; cross-sector legs route `World.NextGateTo`. The dock-trigger collision pass routes miners to `OffloadMiner` (credits + `GoneClean` despawn + delayed relaunch) — never `DockShip` (which refunds `PaidCost` and rebinds a client). `MinersEnabled` mirrors `PigsEnabled` as the test kill-switch; results/errors reach the team as `MinerNoticesThisStep` system chat. Miners collide like any ship (the ship-ship pass runs between ALL ships, friend or foe): any physical bump — ship, asteroid, or base, damaging or not — stamps `ShipSim.LastCollisionTick` and the `DisruptCollidedMiners` sweep knocks a Harvesting miner back to ToRock (beam reset, cargo + claim kept). A miner whose hull drops below `mining.retreat-health-frac` (stock 0.8) of max abandons the field (`GoHome`), offloads whatever it carries, and relaunches at full health.
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.Mining.cs` — `HarvestStep`, `MinerSlot`/`MinerState`, brain + steering + offload + buy/order/status queues
  - `server/Sim/Simulation.cs` — `SeedMinerSlots` (StartMatch), `MinerBrainStep`/`MinerExecute`/`OffloadMiner`/`KillMiner` hook sites, `DrainMinerQueues`
  - `server/Net/ClientHub.cs` — `MsgBuyMiner`/`MsgOrder` handling (commander-gated; miners are bought from the Build tab and sector/rock-tasked via F3, not chat commands), `SystemToTeam` notice relay; `client/scripts/Chat.cs` — command relay + `/help`
  - `server/Content/core/hulls.yaml` — `miner` (class-id 4, unarmed, `ore-capacity`); `client/assets/ships/miner.glb`
  - `tests/MiningTest` — full loop, claim rules, cross-sector return, authorization/discovery gating
- **Related:** [[Rock Class / Ore (Mining)]], [[PigBrain]], [[Autopilot / AutoSteer]], [[Dock Refund]]
- **Notes:** Enemy PIGs auto-hunt miners via the normal chase path (intended). Any teammate can buy/order v1 (Stage-2 bootstrap authority; commander tightens later). Miners scout as they fly (hull vision values) — a miner's own vision reveals new rocks.

### Constructor (AI base-builder drone) & per-type Bases
Proto v37 base building. A **constructor** is a team-owned AI drone (`ShipKind.Constructor`,
`ShipFlagConstructor=128`, hull `is-builder` → `ShipClassDef.IsConstructor`) modeled on the miner: a
commander buys one from the docked **Build tab** bound to a station TYPE (`MsgBuildConstructor=14`,
charges the station price), it launches from a **garrison** (win-condition base) only, and — F3-ordered
to a compatible asteroid (reuses the miner order plumbing; stock outpost → **Regolith**) — it navigates,
then runs the v38 build sequence: **Aligning** (nose-locked at the standoff shell for the station's
`align-time-seconds`), **Approaching** (creeps at `approach-speed` until the hull TOUCHES the rock),
**Sinking** (creeps at `sink-speed` until embedded `sink-depth-frac` below the surface — DISTANCE-gated,
not timed; the **build sphere** emerges from the rock center here, its growth tracking the real embed
depth), then **Building**: the sphere envelops the asteroid over the station's `build-time-seconds`
before the base appears fully constructed and its capabilities are granted. **The finished base CONSUMES its asteroid**: `CompleteConstruction` calls
`World.RemoveRock` (drops the rock from the asteroid list, spatial grid, ore + collision-body state, and
the id cache), and the removal broadcasts as `MsgRockGone=27` (reliable, fog-agnostic) so every client
deletes its rock node + client collision — nothing lingers under the new base. The swap is hidden under
the build sphere (full envelop + opaque core); the client caches the rock's last radius (`_buildRockRadius`)
so the sphere keeps growing after the node is gone. The **collision-flicker** fix is related: a
constructor skips asteroid collision with **its own target rock** while Aligning/Sinking/Building
(`ConstructorEmbeddedRock` → `ResolveAsteroidCollisions` ignore) so the embedded drone isn't shoved out
each tick (Approaching included — the phase ends BY touching). **Bases are now per-type data like ship
hulls**: `BaseDef.ModelName` picks the
GLB (server collision `World.LoadBaseModel` + client `BaseModelLoader`/`CollisionWorld`, keyed by
`BaseSite.BaseTypeId` on the wire), and `World.CreateBase` appends a base at runtime (the base list +
parallel `BaseHealth`/`ResearchByBase` are APPEND-ONLY so indices never shift). A new base reveals via
the owning team's fog reveal log (fog-off: a broadcast `BuildBaseReveal`). **Win condition reworked**: a
per-type `WinCondition` flag (the `start` ability, garrison-only) — a team loses only when ALL its
win-condition bases die, so a destroyed outpost never ends the match.
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.Constructors.cs` — the drone (FSM Idle→ToRock→Aligning→Approaching→Sinking→Building, buy/charge, brain+steering, completion, orders, build-stream view)
  - `server/Sim/World.cs` — `BaseSite.BaseTypeId`, `_baseModels`/`BaseHullOf`/`BaseRadiusOf`/…, `CreateBase`, `ResetMatchBases`, `GarrisonCount`; `Simulation.cs` — `ApplyBaseDamage` win-condition, per-type collision sweep, `IsConstructorClass`
  - `server/Net/Protocol.cs` — `MsgBuildConstructor=14`, `MsgConstructorBuilds=25`, `MsgRockGone=27`/`BuildRockGone` (rock despawn), `BuildBaseReveal`, `WriteBaseStatic` (+baseTypeId/per-type radius); `server/Content/core/stations.yaml` (outpost) + `hulls.yaml` (constructor)
  - `client/scripts/BuildSphere.cs` — the enveloping VFX; `WorldRenderer.UpdateBuildSpheres`/`NetRemoveRock`, `CollisionWorld.RemoveAsteroid`; `ui/BuildTab.cs` — commander BUILD action (`GameNetClient.SendBuildConstructor`)
  - `tests/ConstructorTest` — full loop, rock-class gate, win-condition, def flags
- **Related:** [[Miner (AI ore drone)]], [[Autopilot / AutoSteer]], [[Commander orders / F3 select-and-command]], [[Tech Paths / Research]], [[Build Tab]]
- **Notes:** The outpost base type binds `ss90.glb` (IGC Outpost Hvy), which carries real docking doors, for both server collision and client model. The other 6 station types are authored placeholders — add `base-type-id`+`model-name`+`build-on-rock-class` to activate, no code. Cost = station price. **All beats are YAML (v38)**: per-station `align-time-seconds` + `build-time-seconds` (stations.yaml, streamed on the catalog), drone-wide creep speeds / standoff / embed depth / production dwell under world.yaml `constructor:` (server-only, mirrors `mining:`). Gotcha: `AutoSteer.ApproachPoint` is bang-bang (thrust 0/1) — the slow legs instead COMMAND a speed (throttle = speed/hull-max via `SteerToPoint`, the `Creep` helper), so tuning a hull's max-speed is NOT how you pace the approach.

### Rock-Discovery Construction Gate (DiscoveredRockClasses)
A constructor station is only buyable once the team's fog of war has **revealed at least one asteroid
of its `build-on-rock-class`** — the Supremacy Center (Carbonaceous, a rare hash-rolled special
seeded only in NON-home sectors) must literally be scouted before it unlocks; common Regolith unlocks
on the garrison's first vision apply so outpost/shipyard never notice the gate. State is a per-team
byte bitmask `World.TeamState.DiscoveredRockClasses` (`1 << (byte)RockClass`), **monotonic like
`TeamVision.DiscoveredRocks`** and folded at exactly that set's sim-thread write sites
(`Simulation.Vision.DiscoverRockClass`: boundary apply, warp-time discovery, `ResetVision` clear).
**Fog OFF stamps `0xFF`** at match (re)start AND the server gate short-circuits on `!FogEnabled` —
without fog there is no gate. Server-enforced in `TryBuyConstructor` (before the caps/tech
`StationAvailableTo` check, one seam covering the Build tab + `/build` chat); streamed as a trailing
`u8` on `MsgTeamState` so the client Build tab predicts the lock (card greyed, footer
"NO {CLASS} ASTEROID DISCOVERED"). The tech bases are otherwise **independent of the Outpost** —
the placeholder-era `expansion-allowed` chain was removed (IGC-faithful: rock + credits are the gate).
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.Vision.cs` — `DiscoverRockClass` + the three write sites; `server/Sim/World.cs` — `TeamState.DiscoveredRockClasses`
  - `server/Sim/Simulation.Constructors.cs` — the `TryBuyConstructor` gate; `server/Net/Protocol.cs` — `BuildTeamState` tail
  - `client/scripts/WorldRenderer.cs` — `TeamRockClassDiscovered` (defers-to-server pre-team-state); `client/scripts/ui/BuildTab.cs` — `RockDiscovered` card/footer/buy-guard
  - `tests/ConstructorTest` scenarios 6/7, `tests/FogTest` 14/14b
- **Related:** [[Constructor (AI base-builder drone) & per-type Bases]], [[Fog of War (Team Vision)]], [[Rock Class / Ore (Mining)]]
- **Notes:** A given map/seed may hold NO Carbonaceous rock (stock: 1 special per non-home sector, class = per-rock hash % 3) — then the Supremacy stays locked all match by design; map authors tune supply via `special-count`/`special-per-sector`. Mask is monotonic: building on the only discovered Carbonaceous rock does NOT re-lock the card (a second constructor just finds no valid target, same as any no-rock case). Test gotcha: an unmapped test world (no `MapLoader.ApplyTo`) has ONLY home sectors ⇒ zero specials ⇒ no Carbonaceous anywhere — use ConstructorTest's `NewMappedSim`.

---

## Weapons & Combat

### Missile
Guided projectile with seekers, can lock targets, detonate via proximity or time-out.
- **Frequency:** Very common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — missile data model
  - `client/scripts/MissileView.cs` — client-side missile rendering
  - `server/Net/Protocol.cs` — MsgMissiles message separate from ship snapshots
- **Related:** [[Seeker]], [[Chaff]], [[Blast Radius]]
- **Notes:** Proto v15: MsgMissiles separate stride (never extend MsgSnapshot records); author racks AFTER guns for barrel-seed alignment

### Seeker
Guidance system that locks onto and tracks targets; disrupted by chaff clouds.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — seeker behavior/accuracy
  - `server/Sim/Simulation.cs` — ResolveSeekerTarget logic (chaff substitution seam)
- **Related:** [[Missile]], [[Chaff]], [[Target Lock]]
- **Notes:** ResolveSeekerTarget checks chaff clouds; seekers can be spoofed

### Chaff
Expendable decoy puff (the Counter line) a ship ejects (key `C`); a seeker rolls a stateless hash
(`ChaffStrength` vs the missile's `ChaffResistance`) to break its lock and home on the puff.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Chaff.cs` — chaff model (ChaffStrength, DecoyRadius)
  - `server/Sim/Simulation.Chaff.cs` — ChaffSim + TryDropChaff/StepChaff/TryChaffAim (Track A fills)
  - `server/Net/Protocol.cs` — `MsgChaff=15` one-shot spawn broadcast (28 B); dispenser WeaponDef (Chaff kind)
  - `client/scripts/ChaffFx.cs` — client puff sprites (Track A fills)
- **Related:** [[Missile]], [[Seeker]], [[Expendables]], [[Minefield]]
- **Notes:** Proto v18: chaff is a launcher-projected `WeaponKind.Chaff` WeaponDef linked to its cargo item
  by `CargoId`; ammo comes from spawn cargo counts, not a rack; TryChaffAim is the D5 substitution seam

### Minefield
A deployed cloud of proximity mines (key `B`): one deploy scatters `MineCloudCount` mines within
`MineCloudRadius`, arms after `MineArmTicks`, then each triggers an enemy within `MineTriggerRadius`
for a `BlastPower`/`BlastRadius` splash; the field depletes mine-by-mine.
- **Frequency:** Domain-specific
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Mine.cs` — mine model (CloudRadius/CloudCount/ArmDelay/BlastRadius)
  - `shared/MinefieldLayout.cs` — splitmix64 seed→cloud offsets, shared by sim/client/tests
  - `server/Sim/Simulation.Mines.cs` — MineFieldSim + TryDeployMine/StepMines (Track B fills)
  - `server/Net/Protocol.cs` — `MsgMinefields=13` (41-B seed records, per anchor sector) + `MsgMineGone=14`
  - `client/scripts/MinefieldViews.cs` — client sprite clouds (Track B fills) + deploy expand animation
  - `server/Sim/Simulation.Vision.cs` — armed fields as radar targets (`VisibleEnemyMines`, per-def `MineSignature`)
- **Related:** [[Chaff]], [[Blast Radius]], [[Expendables]], [[Fog of War (Team Vision)]]
- **Notes:** Proto v18: seed-based wire (client regenerates offsets); `aliveMask` (CloudCount ≤ 64) self-heals a resync.
  Fog on: own fields always stream; an enemy field streams once ARMED and radar-detected (signature-scaled,
  probe-style — arming window is radar-silent) OR its center is in direct LOS; a freshly-laid field's cloud
  expands from center (~0.35 s) and a team-colored hazard-burst HUD glyph marks any visible field while on-screen

### Fog of War (Team Vision)
Server-authoritative per-team vision: undiscovered map data never reaches the client. Ships/bases/
probes contribute a directional cone (occluded by asteroids) and/or an omnidirectional proximity
sphere, both scaled by the target's `RadarSignature`; an outer "eyeball" tier streams a ship's mesh
without radar/HUD detection. Computed at 2 Hz on a dedicated worker thread, pipelined one interval
deep so the applied timeline is tick-deterministic regardless of worker speed. Enemy ships lost from
view persist as last-known "ghost" contacts (HUD/radar only) until re-scouted or re-spotted.
- **Frequency:** Core (fog-of-war on by default)
- **Key Files:**
  - `server/Sim/Simulation.Vision.cs` — `TeamVision`, the 2 Hz snapshot-in/apply-at-boundary worker, `IsPointVisibleToTeam`
  - `server/Net/Protocol.cs` — `MsgReveal=16` (newly-scouted statics), `MsgContacts=17` (ghosts + radar-id list)
  - `client/scripts/WorldRenderer.cs` — `GhostContacts`, `NetSetContacts`; `TargetMarkers.cs` — dim ghost glyphs
  - `server/Sim/Simulation.Pig.cs` / missile lock gating — PIGs and lock acquisition respect team vision
- **Related:** [[Recon Probe]], [[Threat Lock (being-locked warning)]], [[Radar Signature (dynamic pipeline)]]
- **Notes:** Per-server world-YAML knob `fog-of-war` (default on); off ⇒ behavior/bytes identical to pre-fog

### Radar Signature (dynamic pipeline)
A ship's effective radar signature — what scales every viewer's fog detection range against it — is a
composable per-tick value, not the hull constant: `clamp((RadarSignature + SigBias) × fireMult ×
boostMult × shieldMult × dustMult, rails)`. Firing (guns/missiles, decaying window) and the
afterburner (ramped by live `AbPower`) make a ship louder; an EQUIPPED shield (capacity > 0,
regardless of pool) radiates; sitting inside a dust cloud quiets it (stacking with the sightline dust
attenuation — hiding in dust beats being seen through it). `ShipSim.SigBias` is the per-ship additive
equipment/ability seam (seeded at spawn from the projected `ShipClassDef.SignatureBias` = authored
`Hull.Signature` + preferred-parts `Part.Signature` sum; a future loadout/cloak system mutates it
live). Computed on the sim thread at vision capture (`SignatureModel.Compute`); server-only — never
streamed, no protocol impact. All world knobs default neutral (1.0) ⇒ byte-identical to
fire-boost-only fog.
- **Frequency:** Core (fog-only; stock world.yaml authors boost 1.4 / shield 1.15 / dust 0.5)
- **Key Files:**
  - `server/Sim/SignatureModel.cs` — the pure pipeline (`SignatureKnobs`/`SignatureInputs`/`Compute`)
  - `server/Sim/Simulation.Vision.cs` — capture-time call, `DustCoverageAt`, `_sigKnobs` cache
  - `server/Content/core/world.yaml` — `boost/shield/dust-signature-mult`, `signature-min/max-mult` rails
  - `server/Content/FactionsContentProjection.cs` — `SignatureBias` projection
- **Related:** [[Fog of War (Team Vision)]], [[Per-Sector Environment (God Rays / Nebula / Dust Clouds)]]
- **Notes:** tests/FogTest section 0 (unit) + 21 (live-sim boost/shield/dust/bias) guard the pipeline

### Per-Sector Environment (God Rays / Nebula / Dust Clouds)
Optional `environment:` block on each sector in a map YAML, driving that sector's look AND — for dust —
its gameplay. Four sub-blocks, all optional (an omitted block keeps the legacy default): `sun`
(azimuth/elevation + color + energy + `god-rays` strength), `nebula` (color/intensity/seed override of
the client's procedural backdrop), `belt` (per-sector asteroid field/belt shape — **server-only**, the
client already receives concrete rocks), and `dust`. **Dust clouds** are procedurally *distributed*
(YAML sets count/size/coverage/density/color + attenuation; the server seeds the actual clouds
deterministically on a dedicated RNG so they never perturb asteroid/aleph placement). Sun/nebula/dust
stream to the client per sector; the client renders each dust cloud as a **3D entity** — a `MultiMesh`
of soft billboard "puffs" (one MultiMesh per cloud) whose puffs are placed along a **ridged fractal-noise
(fbm) field ported from the nebula sky shader** (Starscape.cs), so a cloud clumps into wispy filaments
rather than a round ball. A custom billboard shader adds per-puff **fbm noise** (cloudy internal texture)
and reads the per-instance colour; each puff gets a two-tone **colour variation** plus **sun shading
baked in** (sun-facing side of the cloud bright, far side in shadow — the sector sun is static). It
**never touches Godot's volumetric fog** (an earlier global-fog + `FogVolume` attempt tinted every
ship/asteroid instead of drawing clouds and was removed). **God rays** are a **screen-space crepuscular
pass** (`GodRayShaderCode`) on a CanvasLayer below the HUD: it smears the bright sun + sunlit dust into
shafts and only amplifies already-bright pixels, so it never flat-tints geometry; driven by
`sun.god-rays`.
- **Frequency:** Core (map-authored; stock map "Brimstone Gambit" ships env blocks)
- **Gameplay:** A dust cloud on the viewer→target sightline **shrinks radar/vision range** via
  `dust.vision-mult` — the effective sphere/cone/eyeball radius is multiplied by an optical-depth factor
  in `ClassifyTarget` + `IsPointVisibleToTeam` + `TeamStillSeesShipLive`. Fog-off never runs this path
  (byte-identical). New vision-range modifiers must fold into all three, like signature.
- **Key Files:**
  - `shared/Defs.cs` — `SectorEnvironment`/`SectorSun`/`SectorNebula`/`SectorBelt`/`SectorDust` on `WorldSectorConfig.Env`
  - `server/Content/MapLoader.cs` — YAML DTOs + `ProjectEnv`; `server/Content/maps/brimstone-gambit.yaml` — reference
  - `server/Sim/World.cs` — `DustCloud`/`DustClouds`, `SeedDustClouds` (own RNG), belt overrides threaded into `SeedAsteroid*`
  - `server/Sim/Simulation.Vision.cs` — `DustVisionMult`/`SegmentSphereChord`, `_dustClouds`/`_dustFloor` cache
  - `server/Net/Protocol.cs` — `WriteSectorEnv` appended to `WriteSectorStatic` (Welcome + `MsgReveal`); proto v25
  - `client/scripts/SectorEnvironment.cs` — sun + 3D dust (`MultiMesh` fractal billboard puffs, custom shader: noise + colour variation + baked sun shading) + screen-space god rays; `Starscape.cs` — nebula override
  - `client/scripts/WorldRenderer.cs` — `ApplySectorEnv` seam (routes every sector transition)
- **Related:** [[Fog of War (Team Vision)]], [[YAML Content Pipeline]], [[MsgWelcome]]
- **Notes:** Belt tuning is server-only; only sun/nebula/dust-visual + the seeded cloud list ride the wire

### Recon Probe
Deployable, invulnerable, stationary sensor buoy (key `G`): one deploy spends a probe-cargo charge
and drops a probe just ahead of the ship, granting its team an unoccluded vision sphere
(`ProbeSightRadius`) until it expires after `ProbeLifespanSec`. Streams only to the owning team.
- **Frequency:** Domain-specific
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Probe.cs` — probe model (SightRadius/Lifespan/ModelName)
  - `server/Sim/Simulation.Probes.cs` — `ProbeSim` + `TryDeployProbe`/`StepProbes`; feeds an extra unoccluded
    sphere viewer into `Simulation.Vision.cs`'s `CaptureVisionInput` (no new worker code path)
  - `server/Net/Protocol.cs` — `MsgProbes=18` (owner-team-only, minefield-style cadence), `MsgProbeGone=19`
  - `client/scripts/ProbeView.cs` — stationary GLB visual (`assets/probes/<ModelName>.glb`), team-tinted fallback
- **Related:** [[Fog of War (Team Vision)]], [[Minefield]], [[Chaff]], [[Expendables]]
- **Notes:** Proto v23: `WeaponKind.Probe` dispenser, ammo/cadence rides the same D6/D9 seam as chaff/mine

### Fuel Pod
Reserve afterburner fuel carried as pure cargo (no launcher, no key): when a fuel-modeled hull's
tank hits 0 while boost is held, one charge auto-loads pre-Integrate and the tank refills by
`fuel-per-charge` (clamped to `max-fuel` — the stock 999 value means "full refill").
- **Frequency:** Domain-specific
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/FuelPod.cs` — authoring model (`fuels:` in expendables.yaml)
  - `server/Sim/Simulation.cs` — `ShipSim.FuelPodAmmo` + the Pass A auto-load before `FlightModel.Integrate`
  - `client/scripts/PredictionController.cs` — `ConsumeFuelPod` prediction mirror (live Step + reconcile replay)
  - `client/scripts/SystemRing.cs` — `POD +N` reserve readout under the FUEL arc
- **Related:** [[Expendables]], [[Payload]], [[Afterburner]]
- **Notes:** Proto v35: ship record appends u8 fuelPodAmmo; cargo defs append f32 FuelPerCharge.
  FlightModel.Integrate is untouched (PIG determinism) — the refill lands between InputFor and
  Integrate so the boost gate (which reads pre-tick fuel) never blinks. Hangar hides the row on
  fuel-less hulls; server rejects fuel cargo there (whole-request authored fallback).

### Threat Lock (being-locked warning)
Warning that an enemy missile-armed ship is locking you: `ShipSim.ThreatLockState` (0 none / 1 locking /
2 locked) rides free bits in the snapshot flags byte (`ShipFlagLockingMe=4`, `ShipFlagLockedMe=8`).
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.cs` — per-tick ThreatLockState reset before Pass A; UpdateLock raises it (Track A)
  - `client/scripts/GameNetClient.cs` — decodes flags → `Ship.ThreatLock` / `LocalThreatLock`
- **Related:** [[Target Lock]], [[Missile]]

### Dock Refund
Voluntary dock at your own base refunds the ship's `PaidCost` to team credits (relaunch pays again →
net-free rearm/repair); death refunds nothing (pods don't inherit PaidCost).
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.cs` — `ShipSim.PaidCost` set in SpawnCombatShip; DockShip refunds (Track A)
- **Related:** [[Hull]], [[Payload]]

### Docking Door
Where a ship docks at its own base. `HP_DockingEntrance_*` markers group in **fives** into one bounded
**rectangular face** (1 face marker whose +Z is the inward normal + 4 boundary side-midpoints; the
face marker is detected by ORIENTATION within its five — rim forwards point outward in-plane — not
assumed first); a base may author N doors. A ship (a `ShipRadius` sphere) docks by pure geometric intersection — inside the
rectangle laterally and within a depth window `[−DockFaceDepth, +ShipRadius]` along the inward normal
(no facing/velocity gate). The rest of the base is a solid hull; the same test carves the no-bounce
carve-out so client prediction and server agree. `HP_DockingExit_*` (one = one exit) is the launch
mouth. NOT the old "dock disc" per-hardpoint model (retired 2026-07).
- **Frequency:** Domain-specific
- **Key Files:**
  - `shared/Collision/DockFace.cs` — `DockFace` struct + `DockFaceParser.Build` (the ONE parser both peers call)
  - `shared/Collision/Collide.cs` — `IntersectsDockFace` test + dock carve-out
  - `server/Sim/World.cs` — `LoadBase` → `BaseDockFaces`; `Simulation.cs` dock trigger
  - `client/scripts/CollisionWorld.cs` — client-prediction mirror (bit-identical parse)
  - `tools/collision-hull/bake.py` — per-door corridor carve; `docs/GLB-AND-HARDPOINT-FORMAT.md` — spec
- **Related:** [[Hardpoint]], [[SimModel]], [[Dock Refund]], [[Launch Station Classes]]

### Launch Station Classes (hull base restriction)
Per-hull authored `launch-station-classes: [shipyard]` (hulls.yaml, list of station `class:`
keywords) restricting which base CLASSES the hull may LAUNCH from and DOCK at. Omitted = anywhere
(stock). Restricted hulls (Devastator → shipyard, covering Shipyard(3) + Shipyard Dry(6)): other
friendly bases are fully solid (bounce like an enemy base), and at an allowed base only the
**largest docking door** (by rectangle area, `DockRules.LargestFaceIndex`) admits them — side doors
stay small-ship-only. Projected to `ShipClassDef.LaunchClassMask` (u16 bitmask over
`StationClassId`; streamed LAST in the ship block); base class resolved via the station catalog's
`BaseTypeId → StationClass` map on BOTH peers. Server rejects an illegal spawn pre-charge
(`TryResolveLaunchSite`); the hangar shows the card visible-greyed "SHIPYARD ONLY" (situational
lock — never hidden). Related rule: a base whose loaded model has **no authored `HP_DockingExit`**
can't launch anything ("NO LAUNCH BAY" grey; `World.BaseLaunchCapableOf`; model-less test bases
stay launch-capable).
- **Frequency:** Domain-specific
- **Key Files:**
  - `shared/Collision/DockRules.cs` — `ClassAllowed` / `LargestFaceIndex` / `AllowedFace` (both peers)
  - `factions/src/Allegiance.Factions/Model/Hull.cs` — `LaunchStationClasses` authoring
  - `server/Content/FactionsContentProjection.cs` — `LaunchMask` projection
  - `server/Sim/Simulation.cs` — `TryResolveLaunchSite` spawn gate; `ResolveOwnBaseDock` dock gate; `SelectDockDoor` autopilot pin
  - `client/scripts/DefRegistry.cs` — `HullMayLaunchFrom` / `BaseLaunchCapable` mirrors
  - `client/scripts/ui/ShipLoadout.cs` + `.Hangar.cs` — WrongBase / NO LAUNCH BAY UX
- **Related:** [[Docking Door]], [[Dock Refund]], [[Def (Definition)]]

### Shield
Regenerating energy layer over the raw-health model, authored per hull/faction (`shield-capacity`,
`shield-recharge` points/sec, `shield-delay` seconds). Absorbs incoming damage before the hull;
overflow from a shield-popping hit spills into the hull the same tick; recharges after the quiet
delay. A per-weapon `shield-damage-multiplier` (default 1.0) is the damage-type interaction.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Hull.cs` — `ShieldCapacity/ShieldRecharge/ShieldDelay`; `Model/Parts/Part.cs` — `ShieldDamageMultiplier`
  - `server/Sim/Simulation.cs` — `ApplyDamage` (single damage seam for all 7 sites), spawn init, end-of-Step recharge sweep, `ShieldsEnabled` test toggle
  - `server/Net/Protocol.cs` — shield f16 rides the ship record (`ShipRecordSize`, single-sourced in Protocol.cs) + 3 shield floats/1 shieldMult in MsgDefs (proto 19)
  - `client/scripts/SystemRing.cs` — cyan SHLD solid arc wrapping the HULL gauge; `client/scripts/ShieldFlash.cs` — hemisphere hit flash
- **Related:** [[Hull]], [[Blast Radius]], [[Direct Hit Multiplier]]
- **Notes:** Proto v19; a pod uses the Pod def's shield (0). `ShieldsEnabled=false` lets damage-mechanic tests isolate raw damage. `tests/ShieldTest` is the determinism guard.

### Blast Radius
Damage falloff zone around explosion epicenter; damps based on distance and intervening obstacles.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — blast_radius, blast_power fields
  - `server/Sim/World.cs` — collision query and damage propagation
  - `server/Sim/Simulation.cs` — blast resolution
- **Related:** [[Missile]], [[Direct Hit Multiplier]]
- **Notes:** Tuned via YAML; falloff curve is inverse-distance-squared

### Direct Hit Multiplier
Damage multiplier for projectiles striking target hull directly (vs. proximity detonation).
- **Frequency:** Domain-specific
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — direct_hit_multiplier field
  - `server/Sim/Simulation.cs` — impact detection and damage scaling
- **Related:** [[Blast Radius]], [[Missile]]
- **Notes:** Scales all projectile damage (ballistic + guided); tuned in YAML

### Projectile
Client-side synthetic representation of ballistic fire (bolts, cannon rounds); never stored server-side.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/ProjectileView.cs` — render and interpolation
  - `server/Sim/Simulation.cs` — server-side ballistic hit detection (in-memory `ShotResolution`, no wire message)
  - `shared/Defs.cs` — projectile speed/lifetime constants
- **Related:** [[ShotResolution]], [[Client-Side Hit Sparks]]
- **Notes:** No server Projectile table — the client synthesizes bolts (and intercepts hit sparks locally), while the server tracks ballistic hits in-memory; the native sim is the sole gameplay path.

### ShotResolution
In-memory, server-only ballistic hit record (target ship ID, impact position, time offset) — NOT a wire message (the old STDB `ShotResolution` table is gone).
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/Simulation.cs` — batched `ShotResolution` drain + ballistic hit detection (the in-memory equivalent of the removed module's table)
  - `client/scripts/ProjectileView.cs` — client-side bolt render only (no resolution consumer)
- **Related:** [[Projectile]], [[Client-Side Hit Sparks]]
- **Notes:** Hit detection is server-authoritative; there is no `MsgShotResolution` and the client never consumes resolutions — hit VFX are client-side interception (`CheckBoltImpacts`) per [[Client-Side Hit Sparks]].

### Per-Ship Weapon Loadout (mount overrides)
The hangar's weapon-slot assignments (swap or leave-empty per Weapon hardpoint) carried on MsgSpawn, validated + stored per-ship in the sim, and echoed to every client via MsgShipLoadout=28 (full table, reconcile-by-omission: an omitted ship flies its authored class loadout).
- **Frequency:** Domain-specific
- **Key Files:**
  - `client/scripts/ui/LoadoutState.cs` — hangar model; WeaponOverridesFor / ExpectedEffectiveIds
  - `server/Sim/Simulation.cs` — ResolveLoadout (joint mount+cargo validation: mountable kind, team tech, PayloadCapacity), ShipSim.MountWeaponIds/MountLastFire, WeaponIdAt
  - `shared/FireCadence.cs` — THE per-mount gun-cadence rule shared by server TryFire, PredictionController, and WorldRenderer.SpawnBoltFor
  - `client/scripts/DefRegistry.cs` — WeaponSlots (positional, empties kept); SlotsForShip loadout overlay; MissileMount
- **Related:** [[Projectile]], [[Dock Refund]], [[Held-Input Replay]]
- **Notes:** Guns fire on PER-MOUNT cooldowns; the wire carries only LastFireTick — clients derive WHICH mounts fired by replaying FireCadence against a per-ship shadow, so hardpoint count is unlimited (no fired-mask field). Barrel index = position in the FULL Weapon-hardpoint list (empties included) and seeds the spread — barrel-indexed code must never use the filtered mount list. Whole-request reject → authored fallback (mounts AND cargo); empty cargo alongside overrides = deliberately empty hold, not "seed default". Mount TYPES gate what fits each slot (gun mounts take guns, missile mounts take racks, `any` mounts take either, and an UNAUTHORED empty mesh mount is `NonMountable` — hidden in the hangar, not a slot — via `HardpointDef.MountAccepts`, enforced hangar-side and in ResolveLoadout). Bots/pods always fly authored (MountWeaponIds null). tests/LoadoutTest covers the seams.

---

## Content Pipeline & Game Data

### YAML Content Pipeline
Server-driven content authoring: gameplay/balance values (hulls, weapons, techs, factions) live in YAML files, compiled to binary defs, streamed to clients at runtime.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/core/` — all content YAML (*.yaml)
  - `factions/src/Allegiance.Factions/` — content model classes and serialization
  - `server/Content/ContentLoader.cs` — boot-time loading
  - `shared/ContentValidator.cs` — YAML→defs consistency checks
  - `server/Net/Protocol.cs` — Protocol.MsgDefs wire format
  - `client/scripts/DefRegistry.cs` — client-side def subscription and caching
- **Related:** [[Def]], [[Protocol.MsgDefs]], [[Tech Tree]], [[World Tuning Blocks]]
- **Notes:** Patchless runtime streaming; no client fallback (client holds authority until defs load)

### World Tuning Blocks
Server-side sim tuning authored in the standalone `server/Content/core/world.yaml` (NOT part of the factions bundle manifest; loaded by `WorldLoader`, overridable via `SIM_WORLD`/`--world`) — `ai:` (PIG drone difficulty/behavior), `combat:` (collision damage + boundary hazard), `mechanics:` (gates/docking/pods/economy/match flow), `seeding:` (asteroid field/belt shapes + base placement), `mining:` (harvest/ore economy), `constructor:` (base-builder creep speeds/standoff/embed/dwell), `build:` (per-garrison build-queue parallel/queue limits), plus root radar-signature knobs (`aleph-radar-signature`/`rock-radar-signature`, the `boost/shield/dust-signature-mult` fog multipliers, and the `signature-min/max-mult` rails). Every key optional; omitted keys keep stock values (the shared classes' field initializers). NEVER streamed — no protocol impact.
- **Frequency:** Common (any sim-balance sweep)
- **Key Files:**
  - `server/Content/core/world.yaml` — authored values (stock = documented defaults); standalone, not a manifest fragment
  - `factions/src/Allegiance.Factions/Model/RuntimeData.cs` — AiTuning/CombatTuning/MechanicsTuning/SeedingTuning records (nullable = "unauthored")
  - `shared/Defs.cs` — WorldAiTuning/WorldCombatTuning/WorldMechanicsTuning/WorldSeedingTuning (initializers = stock)
  - `server/Content/WorldLoader.cs` — WorldDef DTOs + Load/Project override-or-stock resolve
  - `server/Sim/Simulation.Pig.cs` — InitPigTuning (seconds→ticks conversion)
- **Related:** [[YAML Content Pipeline]], [[PigBrain]], [[WorldConfig]]
- **Notes:** Durations authored in seconds, converted at TickHz; NumTeams stays compile-time (World.MaxSupportedTeams — engine limit, not a knob)

### Def (Definition)
Compiled gameplay constant: hull stats, weapon stats, tech gating, prices, etc. Streamed from server to clients via MsgDefs.
- **Frequency:** Very common
- **Key Files:**
  - `shared/Defs.cs` — core def table registry and subscriptions
  - `client/scripts/DefRegistry.cs` — client caching and subscription logic
  - `server/Net/Protocol.cs` — MsgDefs serialization
- **Related:** [[YAML Content Pipeline]], [[Tech Tree]], [[Hull]], [[Weapon]]
- **Notes:** Immutable after server boot; clients guard all gameplay until defs load

### Hull
Playable ship chassis with base stats (armor, speed, turn-rate) and hardpoints for weapons/payloads.
- **Frequency:** Very common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Hull.cs` — hull data model
  - `server/Content/core/*.yaml` — hull definitions per faction
  - `client/scripts/ShipController.cs` — hull selection UI
  - `tools/ship-gen/` — modular hull generation from YAML parts
- **Related:** [[Weapon]], [[Payload]], [[Docking]], [[Hardpoint]]
- **Notes:** Immutable after game start; each hull has unique GLB 3D model and collision shape

### Hardpoint
Mount point on a hull/base: weapon muzzle, engine nozzle, nav light, turret, docking entrance/exit, cockpit eye. The GLB mesh's `HP_<Kind>_<Index>` empty nodes (local +Z = forward) are the AUTHORITATIVE inventory and geometry; YAML `hardpoints:` entries only bind weapon-ids and override pos/dir when `off-*`/`dir-*` are explicitly authored. An unbound mesh Weapon node that no YAML entry binds or types streams as `NonMountable` (`HardpointDef.NoWeapon`, hidden — not a loadout slot); author a `mount:` entry (no `weapon-id`) to expose it as an empty assignable mount. Every Weapon mount carries a MOUNT TYPE (`WeaponMountKind` any/gun/missile/non-mountable, streamed): authored via `mount:` in YAML or derived from the bound weapon (gun→gun, rack→missile, unauthored empty→non-mountable) — the hangar and `ResolveLoadout` both reject a weapon of the wrong category via `HardpointDef.MountAccepts`.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/HardpointGeometryMerge.cs` — the mesh→def merge pass (in ContentLoader.Load)
  - `shared/Collision/GlbReader.cs` — HP_ node extraction (pos + world +Z forward)
  - `shared/Defs.cs` — `HardpointDef` / `HardpointKind` / `NoWeapon` sentinel
  - `docs/GLB-AND-HARDPOINT-FORMAT.md` — the full contract
  - `.claude/skills/hardpoints/` — inspection tool (dumps a GLB's HP_ nodes, authored + world units)
- **Related:** [[Hull]], [[Weapon]], [[Def (Definition)]], [[Docking]]
- **Notes:** World scale = model-length / mesh longest axis (bases: radius*2); YAML weapon order stays at the list head so barrel spread-seed indices are stable; `HP_Cockpit` exists in no mesh — always YAML-authored

### Weapon
Armament with barrel, fire-rate, projectile type, and damage tuning.
- **Frequency:** Very common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Parts/Weapon.cs` — weapon model
  - `server/Content/core/*.yaml` — weapon definitions
  - `client/scripts/WorldRenderer.cs` (`SpawnBoltFor`), `PredictionController.cs`, `shared/FireCadence.cs` — client firing/cadence seams (HUD panel = `client/scripts/WeaponsPanel.cs`)
  - `shared/FlightModel.cs` — barrel velocity and spread calculations
- **Related:** [[Hull]], [[Projectile]], [[Blast Radius]]
- **Notes:** Barrel-seed alignment: author weapon racks AFTER guns in YAML for consistent spawn positions

### Payload
Cargo, expendables, or equipment slot on a hull (e.g., missiles, chaff, fuel pods).
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Parts/Launcher.cs`, `Model/Expendables/` — payload/cargo models
  - `server/Content/core/*.yaml` — payload definitions
  - `client/scripts/ui/LoadoutState.cs`, `client/scripts/ui/ShipLoadout.cs` — loadout UI
- **Related:** [[Hull]], [[Missile]], [[Chaff]], [[Expendables]]
- **Notes:** Consumable slots track ammo; non-consumable payloads are always active

### Expendables
Single-use consumables (missiles, chaff, fuel boost) with limited ammo count.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/` — all expendable types
  - `server/Sim/Simulation.cs` — expendable consumption and effect logic
- **Related:** [[Missile]], [[Chaff]], [[Payload]]
- **Notes:** Ammo tied to player ship; respawn restocks loadout

### Tech Tree
Unlock progression system: techs gate hull/weapon/payload availability; advancing development lines unlocks tiers.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/core/*.yaml` — tech tree structure
  - `factions/src/Allegiance.Factions/Resolution/TechResolver.cs` — tech unlock logic
  - `shared/Defs.cs` — tech table registry
- **Related:** [[YAML Content Pipeline]], [[Def]], [[Hull]], [[Weapon]]
- **Notes:** Server-side gates available items; clients only render unlocked techs; tech data is a def

---

### Tech Paths / Research
The team investment tree: a commander spends credits at a friendly base to research a YAML-authored
**development** over wall-clock time; on completion the whole team gains its granted **techs** and
**capabilities**, and the unlock set re-resolves mid-match (e.g. the bomber unlocks once
`heavy-ordnance` is researched). Per-base research runs in configurable slots plus one on-deck queue
slot; credits deduct at start and at queue-reservation; cancel refunds 100%. State encodes
startTick+duration — the client derives live progress from ServerTick.
**Research is scoped per base FAMILY:** a development is researched at the base family whose
tech/capability unlocks it — guns/missiles at the Supremacy, the starter lines (Mini-Gun/Mine/
Counter/Nanite/Bomber, gated only on `base`) at the Garrison, single-scope upgrades at their
from-type base. A base family = a root base type + every tier reachable through its
`SuccessorBaseTypeId` chain (Garrison↔Garrison Str, Supremacy↔Adv Supremacy, …). The home family is
**derived** (no authored field, no wire change) from `StationCatalogDef.GrantedTechIdx` + the
successor chains + the existing `UpgradeFromType`/`TriggeredUpgrades` derivation. The `ResearchTab`
canvas shows only the selected base's family; the sim's `ResearchOpStart` rejects a non-upgrade
development started at the wrong family (mirrored derivation — keep the two in sync).
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/Simulation.Research.cs` — the sim engine (enqueue/step/complete, on-deck promotion)
  - `server/Content/core/techs.yaml`, `developments.yaml` — the authored tech + development catalog
  - `client/scripts/ui/ResearchTab.cs` — the RESEARCH docked-screen tab (clusters, rail-line nodes, banners)
  - `client/scripts/ui/TechDetailPanel.cs` — shared right-hand detail column (schematic / cost-time-at / prereqs / unlocks / action footer), reused by RESEARCH and BUILD
  - `shared/Net/Wire.cs`, `server/Net/Protocol.cs`, `client/scripts/GameNetClient.cs` — `MsgResearch` (13, c→s: start/queue/cancel) + `MsgResearchState` (24, s→c: per-team live progress)
- **Related:** [[Tech Tree]], [[YAML Content Pipeline]], [[Def]], [[Build Tab]]
- **Notes:** Client status is derived from streamed data only (owned techs/caps + per-base research),
  never baked; non-commanders see a disabled affordance. Protocol v36 introduced the wire.

---

### Build Tab
The BUILD docked-screen tab: a responsive card grid of the **station catalog** from which a commander
buys **constructor drones** to raise forward bases (`MsgBuildConstructor=14`, one per station TYPE via
`GameNetClient.SendBuildConstructor`) and **miner drones** (`MsgBuyMiner=16`, `SendBuyMiner`). Runtime
forward bases (`BaseTypeId >= 1` with a `build-on-rock-class`, e.g. the outpost / supremacy-center) are
really constructible; the remaining authored-placeholder structures are display-only until their type is
filled in. Cards use the same owned-tech/cap rules as research, but an UNRESEARCHED structure is hidden
outright (no card until its prerequisites are owned); only situational locks — undiscovered build
rock, full build queue — render as dim "⊘ LOCKED" cards. It reuses `TechDetailPanel`, whose action
footer buys the drone (commander-only, rock-discovery + build-queue gated).
- **Frequency:** Occasional
- **Key Files:**
  - `client/scripts/ui/BuildTab.cs` — the tab + `StationCard` grid cell (`SendBuildConstructor`/`SendBuyMiner`, `RockDiscovered` buy-gate)
  - `client/scripts/ui/TechDetailPanel.cs` — shared detail column
  - `server/Content/core/stations.yaml` — the station catalog (runtime bases + authored placeholders)
  - `shared/Defs.cs` — `StationCatalogDef` (streamed catalog tail of `MsgDefs`)
- **Related:** [[Constructor (AI base-builder drone) & per-type Bases]], [[Miner (AI ore drone)]], [[Tech Paths / Research]], [[YAML Content Pipeline]], [[Def]]
- **Notes:** The `Catalog()` filter drops the garrison (type 0) and upgrade-tier runtime bases (reached
  only via research, no `build-on-rock-class`); an empty catalog shows an awaiting-uplink guard (no baked data).

---

## Networking & Protocol

### Protocol
Binary wire format with quantized/compressed snapshots, separate missile stride, WebRTC/WebSocket dual transport.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Protocol.cs` — message definitions and serialization
  - `client/scripts/GameNetClient.cs` — deserialization and state application
  - `shared/WireQuant.cs` — quantization (f16 compression)
- **Related:** [[MsgSnapshot]], [[MsgMissiles]], [[WebRTC]]
- **Notes:** Little-endian, delta-encoded snapshots; missiles in separate MsgMissiles (never extend MsgSnapshot)

### MsgSnapshot
Quantized world state: player positions, rotations, velocities, health, weapons state.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgSnapshot structure and serialization
  - `client/scripts/GameNetClient.cs` — snapshot application and reconciliation
  - `server/Sim/Simulation.cs` — snapshot generation per SimTick
- **Related:** [[Protocol]], [[WireQuant]], [[AOI]]
- **Notes:** Sent once per SimTick to clients within AOI; quantized positions use f16

### MsgMissiles
Separate protocol message for active missiles; never packed into ship snapshots.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgMissiles structure
  - `client/scripts/GameNetClient.cs` — missile state application
  - `server/Sim/Simulation.cs` — missile lifecycle updates
- **Related:** [[Missile]], [[MsgSnapshot]], [[Protocol]]
- **Notes:** Proto v15: separate stride prevents missile data bloat; missiles sent per-missile once per tick

### WireQuant (Wire Quantization)
Half-precision (f16) floating-point compression for network transmission of velocities, power levels, and health.
- **Frequency:** Common
- **Key Files:**
  - `shared/WireQuant.cs` — f16 encoding/decoding
  - `server/Net/Protocol.cs` — applied to position/velocity in MsgSnapshot
- **Related:** [[MsgSnapshot]], [[Protocol]]
- **Notes:** Trades ~1.5% accuracy for 50% bandwidth savings; safe for physics/visuals

### WebRTC
Peer-to-peer data channel transport; preferred over WebSocket for lower latency and direct connectivity.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/WebRtcListener.cs` — server-side WebRTC peer handler
  - `client/scripts/GameNetClient.cs` — client WebRTC data channel logic
  - `public-lobby/Signaling.cs` — SDP offer/answer relay
  - `shared/Net/WebRtcSdp.cs` — SDP parsing
- **Related:** [[DIRECT-FIRST]], [[Public Lobby]], [[Signaling]]
- **Notes:** Dual WS/WebRTC; DIRECT-FIRST: answerer ondatachannel fires already-open, attach onmessage early or Hello drops

### DIRECT-FIRST
Reachability probe strategy: attempt direct P2P connection first; only fall back to relay if P2P fails.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/ReachabilityProbe.cs` — STUN probing logic
  - `public-lobby/Signaling.cs` — fallback routing
  - `server/Net/WebRtcListener.cs` — direct accept handling
- **Related:** [[WebRTC]], [[Public Lobby]]
- **Notes:** NO TURN server; reintroducing TURN would add latency and cost

### Join Token
HMAC-SHA256 signed authorization: epoch + expiry + team + faction. Server verifies at connection handshake.
- **Frequency:** Common
- **Key Files:**
  - `shared/JoinTokens.cs` — token generation and validation
  - `server/Net/ClientHub.cs` — join validation
  - `public-lobby/PublicLobby.cs` — token issuance
- **Related:** [[MsgWelcome]], [[Team]]
- **Notes:** Prevents replay attacks and unauthorized teams; expiry ~5 minutes

### MsgWelcome
Handshake message from server to client: assigns player ID, initial ship, world state snapshot, reconnect token.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgWelcome structure
  - `client/scripts/GameNetClient.cs` — welcome handler and world rebuild
  - `server/Net/ClientHub.cs` — welcome generation
- **Related:** [[Reconnect Grace]], [[MsgSnapshot]]
- **Notes:** Triggers client world rebuild; reconnect token valid for 5s (ship held server-side during grace period)

### Reconnect Grace
5-second window after disconnect: server holds dropped ship state; client can reconnect and resume without respawn.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/ClientHub.cs` — reconnect token validation and ship reclaim
  - `client/scripts/GameNetClient.cs` — reconnect logic
  - `server/Net/Lobby.cs` — ship state caching
- **Related:** [[MsgWelcome]], [[Join Token]]
- **Notes:** Proto v9+; voluntary leave must send MsgBye to release ship immediately

---

## Client Rendering & UI

### Client Prediction
Client-side extrapolation of ship state between server snapshots to reduce perceived latency. The predicted local ship also resolves collisions each tick: against the sector's static hulls (`CollisionWorld` — same GLB bytes → bit-identical hulls) AND against the other ships it can see (`Collide.ShipShipContact` + `ResolveShipsLocal`, the shared kernel behind the server's ship-ship Pass C, applying only the LOCAL ship's mass-weighted impulse/push-out share against each remote's interpolated pose).
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/PredictionController.cs` — prediction state and reconciliation
  - `client/scripts/RemoteShip.cs` — remote ship interpolation (consumes [[MotionInterpolator]])
  - `client/scripts/CollisionWorld.cs` — client mirror of the server's static + per-class ship hulls
  - `shared/Collision/Collide.cs` — `ShipShipContact` / `ResolveShipsLocal` (one kernel, both peers)
  - `client/scripts/WorldRenderer.cs` — frame rendering + `ShipObstacles` provider
- **Related:** [[Flight Model]], [[Held-Input Replay]], [[MsgSnapshot]], [[MotionInterpolator]]
- **Notes:** Never blocks authority; server snapshot always wins. Remote poses lag by the interp delay, so a hard ship bump may still reconcile — the spring absorbs it; the win is never visibly interpenetrating another ship.

### MotionInterpolator
Reusable snapshot-smoothing engine for any server-controlled streamed entity (remote ships today; missiles etc. can adopt it). Samples are stamped on the server-tick timeline, rendered behind an adaptive delay sized to each entity's smoothed inter-arrival gap (full-rate ships ~100 ms, coarse-AOI ~1.5× their gap): cubic HERMITE interpolation between samples using the wire velocities as tangents (degrades to linear at full-rate gaps), bounded velocity/angular-velocity dead-reckoning past the newest sample, and error-blend correction (a late authoritative sample glides in over ~100 ms instead of snapping; a teleport-sized error snaps). Server side, same-sector miners are exempted from the coarse AOI tier (`SIM_MINER_MIDRATE`, default on) so slow station-keeping drones refresh at mid cadence.
- **Frequency:** Domain-specific
- **Key Files:**
  - `client/scripts/MotionInterpolator.cs` — the engine (Tunables, Push/Evaluate/Reset)
  - `client/scripts/RemoteShip.cs` — the ship-flavored consumer (flags/HUD/glow stay local)
  - `server/Net/ClientHub.cs` — AOI distance tiers + the miner mid-rate exemption
- **Related:** [[Client Prediction]], [[AOI (Area of Interest)]], [[Miner (AI ore drone)]]
- **Notes:** Wire velocity + LOCAL angular velocity (f16) already ride every ship record — no protocol change; rotation extrapolation right-composes yaw→pitch→roll like `FlightModel.Step`

### WorldRenderer
Master 3D scene renderer: camera, world geometry, ships, projectiles, effects.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/WorldRenderer.cs` — main rendering orchestrator
  - `client/scripts/CameraRig.cs` — camera control and follow logic
  - `client/scripts/ShipController.cs` — local player ship rendering
  - `client/scripts/RemoteShip.cs` — remote ship rendering
- **Related:** [[GLB]], [[Collision]], [[Client Prediction]]
- **Notes:** Uses Godot 4.6 .NET; GLB models imported per-hull

### Sector-Entrance Streaming (Time-Sliced Inserts & Prewarm)
How the client absorbs a heavy sector (dense belt + dust) without dropping frames as things appear. Welcome/MsgReveal rock rows are QUEUED, not inserted — `AsteroidRenderer.DrainInserts` spends a per-frame time budget (bigger pre-spawn and under the warp flash, small in open flight; a ~200-rock belt used to stall one frame ~0.5s). Dust clouds build ONCE per sector into hidden per-sector roots (prewarmed one cloud/frame as sector rows stream; `SectorEnvironment.ShowDust` just toggles visibility on entry). Shadow-volume meshes and hull-vert extremes are cached per MESH (all rocks of a variant share one baked volume), and `AssetPreloader` warms base/ship GLBs + SimModels + BVHs at boot so first-sight inserts don't cold-load.
- **Frequency:** Occasional
- **Key Files:**
  - `client/scripts/world/AsteroidRenderer.cs` — pending-row queue + `DrainInserts` budget drain
  - `client/scripts/SectorEnvironment.cs` — per-sector dust roots, prewarm queue, shadow-volume cache
  - `client/scripts/world/EnvironmentRenderer.cs` — per-mesh occluder extremes cache, `WarmModelScene`
  - `client/scripts/AssetPreloader.cs` — boot-time GLB/SimModel/BVH warming (asteroids + bases + ships)
- **Related:** [[WorldRenderer]], [[Fog of War]], [[Per-Sector Environment (God Rays / Nebula / Dust Clouds)]]
- **Notes:** Queued rocks still honor MsgRockUpdate/MsgRockGone (rows mutate/drop in the queue); the warp settle window stays open while the drain runs, so the flash covers the streaming. `[perf]`-tagged threshold logs at these seams stay in the build for future spike attribution.

### GLB
3D model format (glTF binary): embeds textures, used for hulls, asteroids, and collision geometry.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/GlbLoader.cs` — client-side GLB loading
  - `server/Assets/SimAssets.cs` — server-side GLB reading for collision
  - `shared/Collision/GlbReader.cs` — GLB parsing
  - `tools/ship-gen/` — hull GLB generation
- **Related:** [[Hull]], [[Collision]], [[SimModel]]
- **Notes:** Commit ONLY the .glb (embeds textures); .import sidecars are gitignored; run `godot --headless --import` for Godot import cache

### SimModel
Server-side collision representation: convex hulls and docking hardpoints extracted from GLB.
- **Frequency:** Common
- **Key Files:**
  - `shared/Collision/SimModel.cs` — hull + docking geometry
  - `server/Assets/SimAssets.cs` — cached .simmodel loading
  - `shared/Collision/ConvexHull.cs` — QuickHull hull generation
- **Related:** [[GLB]], [[Collision]], [[Hull]]
- **Notes:** Uncommitted .simmodel cache in `<binary>/sim-cache/`; ships exit via DockingExit

### Client-Side Hit Sparks
Visual hit feedback: spawned client-side on projectile collision, not server-driven.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ProjectileView.cs` — hit effect spawning
  - `client/scripts/SfxManager.cs` — impact audio playback
  - `server/Net/Protocol.cs` — ShotResolution message confirms hit
- **Related:** [[Projectile]], [[ShotResolution]], [[VFX]]
- **Notes:** Client visual interception; server never deletes/reduces health client-side; friendly fire sparks too

### VFX (Visual Effects)
Screen-space and world-space visual feedback: explosions, engine glow, hit flashes, lens flares.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ExplosionEffect.cs` — explosion particles
  - `client/scripts/EngineGlow.cs` — engine thrust glow
  - `client/scripts/HitFlash.cs` — hull damage flash
  - `client/scripts/LensFlare.cs` — optical effects
- **Related:** [[Client-Side Hit Sparks]], [[SfxManager]]
- **Notes:** Hooked into AddBolt/DeleteShip collision checks; spatial audio tied to world position

### DesignTokens
Godot centralized UI palette and type scale: colors, fonts, sizes. Source of truth for visual theme.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/ui/DesignTokens.cs` — token definitions
  - `client/scripts/ui/UiFonts.cs` — font asset loading
  - `DESIGN.md` — palette and type documentation
- **Related:** [[UI Components]], [[UiKit]], [[Theme]]
- **Notes:** Applied per-overlay (not globally); ChamferButton and all UI elements derive from tokens

### UI Components
Reusable Godot .NET UI controls: ChamferButton, BracketPanel, ModalHost, SettingsDialog, etc.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/ui/` — all component classes
  - `client/scripts/ui/ChamferButton.cs` — custom-draw retro button
  - `client/scripts/ui/ModalHost.cs` — overlay layer manager
  - `client/scenes/UiShowcase.tscn` — live gallery (F9 in-game)
- **Related:** [[DesignTokens]], [[DESIGN.md]], [[UiKit]]
- **Notes:** Never hardcode colors/fonts; derive from DesignTokens; add new components to UiShowcase.tscn

### UiKit
Static factory helper for stock UI controls: `MakeButton`, `MakeSlider`, `MakeToggle`, etc.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ui/UiKit.cs` — factory methods
- **Related:** [[UI Components]], [[DesignTokens]]
- **Notes:** Use `UiKit.MakeLabel(text, TextStyle, color?)` instead of setting fonts by hand

### ModalHost
Overlay layer manager: manages z-order for menus, dialogs, settings.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ui/ModalHost.cs` — layer orchestration
  - `client/scripts/ui/SettingsDialog.cs` — settings UI
  - `client/scripts/ui/EscapeMenu.cs` — escape menu
- **Related:** [[UI Components]], [[DesignTokens]]
- **Notes:** Layer 200 for modals; SettingsDialog uses live write-through + snapshot revert

### Theme
Per-overlay Godot UI theme application; not global.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ui/DesignTokens.cs` — theme wiring
  - `DESIGN.md` — theme application rules
- **Related:** [[DesignTokens]], [[UI Components]]
- **Notes:** Call `UiTheme.Apply(control)` on each full-screen overlay root; cannot live on CanvasLayer

### Zoom Mode (Telescopic Scope)
Circular picture-in-picture magnifier centred on screen: a second Camera3D renders the live world (shared World3D, narrow FOV) into a SubViewport, drawn clipped to a disc in place of the SystemRing gauges. `+`/`KpAdd` opens at 5x and steps 5→10→20 (capped); `−`/`KpSubtract` steps down and closes below 5x; Esc dismisses. Mouse-look sensitivity is divided by the magnification for fine aim.
- **Frequency:** Occasional
- **Key Files:**
  - `client/scripts/ZoomView.cs` — the scope (SubViewport + narrow-FOV camera, circular draw, input, `Active`/`Magnification` statics)
  - `client/scripts/Hud.cs` — instantiates it after WeaponsPanel
  - `client/scripts/SystemRing.cs` / `VelocityIndicator.cs` / `TargetMarkers.cs` — hidden while `ZoomView.Active`
  - `client/scripts/ShipController.cs` — Esc bail-out + mouse gain ÷ `ZoomView.Magnification`
- **Related:** [[UI Components]], [[DesignTokens]]
- **Notes:** Scope FOV = 2·atan(tan(75°/2)/M); shares the main World3D (split-screen idiom), never OwnWorld3D (that's the hangar preview)

### First-Person View (Cockpit Camera)
The chase camera's two-mode state machine: THIRD PERSON (the behind-the-ship chase shot) and FIRST PERSON (the pilot's eye, parked at the hull's `Cockpit` hardpoint). Both framings share the ship's basis, so switching is a purely positional, smoothstep-eased dolly (~0.3 s) between the chase offset and the cockpit offset — never a hard cut. First person is the DEFAULT, persisted per player (`UserPrefs.FirstPersonView`, default true). `V` toggles modes without touching the zoom; winding the wheel IN past the closest chase shot dives into the cockpit, winding OUT of the cockpit pulls back to the tightest chase framing. The own hull / team-trail / engine-glow / own-nameplate hide only once the transition completes (`CameraRig.FirstPersonActive`), so the ship stays visible throughout the dolly.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/CameraRig.cs` — the view-mode state machine, `V` toggle, wheel transitions, cockpit-offset lookup, `FirstPersonActive`/`ViewIsFirstPerson` statics, `--view-demo` self-drive
  - `client/scripts/PredictionController.cs` — hides the own ShipModel/TeamTrail/glow/nameplate while `FirstPersonActive && !SectorOverview.Active`
  - `client/scripts/EngineGlow.cs` — `Suppressed` flag folded into the per-frame `Visible` recompute
  - `client/scripts/ViewModeIndicator.cs` — transient "VIEW FPV/3RD" chip flashed on a toggle
  - `client/scripts/UserPrefs.cs` — `FirstPersonView` persisted pref
  - `server/Content/core/hulls.yaml` — the `kind: cockpit` hardpoint (eye point) on each hull
- **Related:** [[Zoom Mode (Telescopic Scope)]], [[Hardpoint]], [[UserPrefs]]
- **Notes:** `Cockpit` = `HardpointKind` byte 8 (append-only, client-only — no wire/sim change); marker `HP_Cockpit_0` resolved to ship-local space, fallback `(0, 0.5, 1)`; F3 sector overview un-hides the own ship

---

## Server Architecture

### Lobby
In-game social state: player roster, team assignment, ready status, faction selection.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Lobby.cs` — lobby state and player management
  - `server/Net/LobbyRegistrar.cs` — multi-lobby registry
  - `client/scripts/Lobby.cs` — client-side lobby UI
- **Related:** [[Team]], [[Faction]], [[Ready State]]
- **Notes:** Server-hosted; no external DB; team/ready state replicated to all clients

### Team
Player faction assignment: team 0 (Faction0, blue) or team 1 (Faction1, red).
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Lobby.cs` — team assignment logic
  - `shared/Defs.cs` — team constant definitions
  - `client/scripts/ui/DesignTokens.cs` — faction color mapping
- **Related:** [[Faction]], [[Join Token]]
- **Notes:** Immutable after game start; faction colors are Faction0 (blue) / Faction1 (red); TeamAccent (cyan) is chrome only

### Faction
Gameplay variant: faction-specific hulls, weapons, techs. Loaded from YAML at server boot.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/core/*.yaml` — all faction content
  - `server/Content/FactionStart.cs` — faction startup
  - `factions/src/Allegiance.Factions/Model/Faction.cs` — faction model
- **Related:** [[YAML Content Pipeline]], [[Tech Tree]], [[Hull]]
- **Notes:** Immutable after boot; factions are defs; team assignment determines which faction player uses

### Ready State
Player signal that they are prepared for match: used for team-balanced start conditions.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Lobby.cs` — ready flag and state machine
  - `client/scripts/Lobby.cs` — ready toggle UI
- **Related:** [[Team]], [[Lobby]]
- **Notes:** Server gates match start until all players ready

### PigBrain
Server-side AI decision system: 5 Hz decision tick, evaluates targets/actions, steers cached decisions.
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/Simulation.Pig.cs` — PigBrainTick and decision logic
  - `server/Sim/PigDecision.cs` — steering action encoding
  - `server/Sim/Simulation.cs` — decision caching and re-steering
- **Related:** [[SimTick]], [[Flight Model]], [[Commander Order]]
- **Notes:** Decoupled from SimTick (20 Hz vs 5 Hz); PigBrainTick evaluates fresh targets; SimTick re-steers from cache; safe to hot-swap scheduled table

### Commander
Per-team AI decision authority (proto 34): the ONE pilot whose orders AI vessels execute; also gates miner buys (`MsgBuyMiner`) and F3 miner orders (`MsgOrder`). Explicit STATE (not derived like the rename-gating LeaderOf): seeded to the first pilot to join the side, falls to the next-lowest client id when the commander leaves, manually handed off via `/commander <name>` (sitting commander or host). Streamed on the `MsgLobbyState` tail; gold CMDR badge in the roster.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Lobby.cs` — `CommanderOf` / `SetCommander` / `FixCommanderLocked` state
  - `server/Net/ClientHub.cs` — `CommanderOrWarn` gate, `/commander` hand-off
  - `client/scripts/GameNetClient.cs` — `CommanderIdOf` / `IsCommander`
  - `tests/LobbyTest/` — seed / fall-through / manual-set coverage
- **Related:** [[Commander Order]], [[Lobby]], [[Team]]
- **Notes:** Does NOT survive reconnect (new client id → rank falls to next senior; re-promote manually); host is a separate server-wide role

### Commander Order
An F3-map command for a friendly ship (`MsgOrder`, proto 34): left-click SELECTS entities (multi-select set tracked in `SectorOverview`, separate from Tab focus), right-click names a target; the SERVER infers the verb — enemy ship/base → attack, anything else → go-to-and-idle, targetKind 255 = release. Anyone may issue; AI executes only the commander's (hub-gated) — orders to human teammates relay as gold advisory chat directives (`MsgChatRelay` scope 2, `DesignTokens.CmdrGold`).
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/Simulation.Orders.cs` — `_pigOrders`, `ApplyCommandOrder` verb inference, `TryObeyOrder`
  - `server/Net/ClientHub.cs` — `HandleOrder` routing/authorization
  - `client/scripts/SectorOverview.cs` — selection + right-click dispatch
  - `tests/CommanderTest/` — 9 sim-level scenarios
- **Related:** [[Commander]], [[PigBrain]], [[Fog of War]]
- **Notes:** Orders complete-and-revert (target dead / radar contact lost / base destroyed → autonomy); rescue outranks orders; fog-gated at issue AND execution (no wallhack); keyed by ShipId so respawned drones never inherit; miner subjects map onto mining state (rock pins claim, point authorizes + retargets that miner's sector — the old `/mine`, friendly base = pinned offload)

---

## Public Lobby & Signaling

### Public Lobby
Standalone .NET web service: game server registry, WebRTC signaling relay, server browser UI.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/PublicLobby.cs` — main web service
  - `public-lobby/ServerRegistry.cs` — active server tracking
  - `public-lobby/Signaling.cs` — WebRTC SDP relay
  - Live: `wivuu-public-lobby-production.up.railway.app`
- **Related:** [[WebRTC]], [[DIRECT-FIRST]], [[Railway Deploy]]
- **Notes:** Separate from gameplay servers; handles discovery and P2P setup only

### ServerRegistry
Directory of active game servers: hostname, port, player count, faction mix.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/ServerRegistry.cs` — registry logic
  - `public-lobby/PublicLobby.cs` — registry queries
- **Related:** [[Public Lobby]]
- **Notes:** Periodically probed for health; stale entries auto-removed

### Signaling
WebRTC SDP offer/answer relay: matches peers for connection negotiation.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/Signaling.cs` — offer/answer routing
  - `server/Net/WebRtcListener.cs` — server-side SDP handler
  - `shared/Net/WebRtcSdp.cs` — SDP parsing
- **Related:** [[WebRTC]], [[DIRECT-FIRST]]
- **Notes:** Stateless relay; no TURN fallback in current design

---

## Testing & Validation

### Flight Model Determinism Test
Golden-file regression test: confirms server physics matches replayed client path deterministically.
- **Frequency:** Domain-specific
- **Key Files:**
  - `tests/FlightModelTest/` — test suite
  - `shared/FlightModel.cs` — golden reference
- **Related:** [[Flight Model]]
- **Notes:** All tests pass as of 2026-06-12; any failure is a real regression

### Missile Test
Validates missile mechanics: acceleration, seeker targeting, chaff substitution.
- **Frequency:** Domain-specific
- **Key Files:**
  - `tests/MissileTest/` — test cases
- **Related:** [[Missile]], [[Seeker]], [[Chaff]]

### Content Validator
Ensures YAML content compiles consistently: no missing references, type safety.
- **Frequency:** Common
- **Key Files:**
  - `shared/ContentValidator.cs` — validation logic
  - `server/Content/ContentLoader.cs` — pre-boot checks
- **Related:** [[YAML Content Pipeline]], [[Def]]
- **Notes:** Runs at server startup; aborts boot on schema violations

---

## Tools & Utilities

### ship-gen
YAML-to-GLB pipeline: converts modular hull part definitions into 3D models with baked PBR and HP (hardpoint) nodes.
- **Frequency:** Common
- **Key Files:**
  - `tools/ship-gen/` — generation logic
  - `server/Content/core/*.yaml` — hull part definitions
- **Related:** [[GLB]], [[Hull]], [[SimModel]]
- **Notes:** Canonical scout/fighter/bomber/pod wired into ShipModelLoader; output embeds textures

### Spatial Audio
3D sound positioning: plays effects anchored to world position, pans and attenuates based on listener.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/SfxManager.cs` — PlayAt/PlayUi API
  - `client/scripts/VFX.cs` — effect hooks
  - `tools/sfx-gen/` — synthetic placeholder generation
- **Related:** [[VFX]], [[Client-Side Hit Sparks]]
- **Notes:** Hooked into AddBolt/DeleteShip/CheckBoltImpacts/EngineGlow; collisions+settings-UI deferred

---

## Common Pitfalls & Anti-Patterns

### Order-Independent Cache / Signature Hashes
When hashing a SET of rows into a cache key or status signature, aggregate COMMUTATIVELY (XOR/sum) so iteration order can't shift the key — never an order-dependent FNV or sequential fold, and fold each owned cap/base exactly ONCE. (Historical origin: the removed SpacetimeDB `sql` had no ORDER BY and `Iter()` order was unstable across runs; the trap now lives in the Godot UI catalog status signatures.)
- **Fix:** Fold each row commutatively (XOR/sum); never rely on sequential iteration order.
- **Key File:** `client/scripts/ui/RefreshGate.cs`, `ui/BuildTab.cs` (`ComputeStatusSig`), `ui/ResearchTab.cs` (`ComputeStatusSig`), `ui/ShipLoadout.cs` (`ComputeBaseSig`)

### Client No Baked Tuning Fallback
Client reads tuning only from subscribed def tables; no compile-time constant fallback.
- **Fix:** Guard (hold authority) until a def loads; never hardcode gameplay values
- **Key File:** `client/scripts/DefRegistry.cs`

### WorldConfig Cube-Law Asteroid Bloat
Asteroid count balloons with `WorldConfig` cube-law; O(entities×asteroids) collision pass.
- **Fix:** Route through per-sector spatial grid; see [[Asteroid Grid Broad-Phase]]
- **Key File:** `server/Sim/World.cs`

### SubViewport UI Gotchas
SubViewport needs explicit `World3D` for raycasts; SetAnchorsAndOffsetsPreset for code-built overlays.
- **Fix:** Assign a new World3D to each SubViewport; use preset helpers
- **Key File:** `client/scripts/ui/ModalHost.cs`

### Godot GLB Import Cache
Godot 4.6 requires explicit import of .glb files or silently falls back to engine font/placeholder.
- **Fix:** Run `godot --headless --import` after pulling new assets or changing .glb
- **Key File:** Build/CI workflows

---

## Related Documentation

- **[CLAUDE.md](CLAUDE.md)** — project-specific architecture notes and gotchas
- **[DESIGN.md](DESIGN.md)** — UI component library and design-system reference
- **[server/Content/core/](server/Content/core/)** — YAML content definitions (gameplay values)
- **[tests/](tests/)** — determinism and integration tests
