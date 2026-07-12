# Mining + Economy (Stage 4 · XL)

## Context

The Stage-2 economy is a flat per-team paycheck (`AccrueTeamCredits`, `Faction.IncomeMoney`). This item upgrades it into the real Allegiance economy: asteroids get resource classes, each team runs AI **miner** ships that harvest helium-3 rocks and offload ore at friendly bases for team credits. Miners are purchasable (capped, world-YAML configurable), auto-pilot themselves (reusing the shared `AutoSteer` shipped with player autopilot), cross sectors to offload, and only *mine* in sectors the team has authorized. This also forces two long-deferred foundations into existence: **multi-hop aleph routing** and a **non-combat AI ship kind** — both explicitly anticipated by the autopilot work ("reuse for miners/constructors").

**User decisions (locked):**
- Rocks **shrink** as mined (volume-proportional), never vanish; He3 capacity varies randomly per rock with world-level AND map-level knobs.
- Mining income runs **alongside** the flat paycheck (both).
- **Any teammate** can buy miners / issue mine-sector orders (Stage-2 "any-player-spends" bootstrap; commander tightens later).
- v1 UI = **chat commands only** (`/buyminer`, `/mine <sector>`, `/miners`).
- Miner model comes from **pick-assets** (original-Allegiance `*mnr*` meshes) — user picks from rendered screenshots at an implementation checkpoint.
- Fog: miners target **discovered rocks only** (base vision sphere discovers home-sector rocks at tick 0, so income starts immediately; a miner's own vision reveals more as it flies).
- Transit is **free**: the authorization gate applies to rock *selection* only; routes may pass through any sector.

## Architecture (verified against code)

- **Rock ore state** — `World.Rock` (readonly record, `server/Sim/World.cs:68`) stays the immutable static baseline. New mutable `World.RockOre : Dictionary<ulong, OreState>` (`OreCapacity`, `OreRemaining`, `CurrentRadius`, `byte RockClass`) built after asteroid seeding; `World.RockCurrentRadius(id)` falls back to static radius. Match restart rebuilds it via the `StartMatch` world swap.
- **Determinism constraint (critical)** — rocks and alephs draw from the SAME `DetRng(seed)` stream (`World.cs:260-292`: gates placed via `RandomOuterPos(ref rng,…)` *after* rock seeding). Ore class/capacity therefore come from a **per-rock derived sub-RNG** (`new DetRng(Mix(seed, rock.Id))`), never extra draws on the shared stream — existing layouts stay byte-identical for a pinned seed (guard-tested).
- **`enum RockClass : byte { Carbonaceous=0, Silicon=1, Uranium=2, Helium3=3 }`** (append-only, `shared/Defs.cs`). Only He3 harvestable; others are cosmetic now, future refinery/shipyard hooks.
- **Shrink** — `CurrentRadius = floor + (spawn−floor)·(OreRemaining/Capacity)^(1/3)`; floor = `shrink-floor-frac` (default 0.4) × spawn radius. Non-He3 never shrinks.
- **Miner ship** — new hull **class-id 4** (3 is soft-reserved for the Interceptor stub, `hulls.yaml:127`), `ShipSim.IsMiner` + separate `MinerSlot` pool (NOT `PigSlot` — purchased lifecycle, no wave respawn; team seeds 1 free slot per match). Brain in new `server/Sim/Simulation.Miner.cs` mirroring the PIG 5 Hz decide / 20 Hz execute split. `PigBrainStep` brains every `IsPig` ship — miners use the distinct flag so PIG logic never touches them. Enemy PIGs auto-hunt miners via the existing `GatherPigContext`/`TryChaseEnemy` path — desired, no exclusion.
- **Offload** — reuse the dock-trigger collision pass (`Simulation.cs:722-748`) with an `IsMiner` branch **before** `DockShip` (which refunds `PaidCost` + calls `ClearClientShip` — miners must not enter it): credit team, `GoneClean` despawn, `slot.RespawnAtTick = tick + offloadDelay`, relaunch via `PlaceAtBase` preserving `LastRockId`.
- **Commands** — ride the existing chat seam: client `Chat.cs` relays `/` text (precedent `/pigs` at `:219`) → server `ClientHub.HandleCommand` (`:732`) → thread-safe `_sim.Enqueue*` (precedent `EnqueueSetAutopilot`, `Simulation.cs:574`); replies via `SystemTo`/`SystemAll` (`:756-764`).
- **Wire** — one protocol version bump covers: 3 fields appended to `WriteRockStatic` (shared by Welcome + `MsgReveal` — keep the single helper), new `MsgRockUpdate = 22` (live shrink), `ShipFlagMiner = 32`, `ShipClassDef.OreCapacity` in `MsgDefs`.

## Workstreams (dependency order; Δ = delegate to Opus subagent)

### 1. Rock classes + ore table + config knobs (server-only)
- `server/Sim/World.cs` — post-seeding **per-sector selection pass** (not independent per-rock rolls, so counts are guaranteed): per sector, `he3Count = clamp(round(he3-fraction × sectorRockCount), he3-min, he3-max)` (clamped again to the sector's actual rock count; `asteroids: none` sectors get 0). Select WHICH rocks deterministically by ranking the sector's rocks on a per-rock derived hash (`Mix(seed, rock.Id)`) and taking the top `he3Count` — seed-stable, no draws on the shared stream. Remaining rocks split among Carbonaceous/Silicon/Uranium by the same hash. Capacity per He3 rock = `Lerp(cap-min, cap-max, roll from derived sub-RNG) × (radius/ref)³ × sector-richness-mult`.
- `shared/Defs.cs` — `RockClass`; `WorldMiningTuning` on `WorldConfig` (pattern `WorldMechanicsTuning:512`): `max-miners-per-team=4`, `harvest-rate-per-second`, `credits-per-ore-unit`, `offload-delay-seconds`, `he3-fraction`, **`he3-per-sector-min` / `he3-per-sector-max`** (world defaults for the count clamp), `ore-capacity-min/max`, `shrink-floor-frac=0.4`, `miner-standoff`. Per-sector nullable overrides on `SectorConfig` (`Defs.cs:275`, pattern `AsteroidDensityMult`): **`he3-min`, `he3-max`**, `he3-fraction-mult`, `ore-richness-mult`.
- Δ `server/Content/WorldLoader.cs` (`WorldMiningDef` nullable block + merge; server-only, NOT streamed — `Protocol.BuildDefs` skips it like ai/combat/seeding), `server/Content/core/world.yaml` `mining:` block, `server/Content/MapLoader.cs` + `schemas/world.schema.json` + `schemas/map.schema.json` mirrors.
- **Tests**: same seed ⇒ identical class/capacity; **pinned-seed golden**: rock AND aleph positions byte-identical to pre-change (the canary); per-sector He3 count always within [min, max] across seeds; map-level min/max overrides world defaults.

### 2. Sector graph + multi-hop routing (server-only) — Δ, parallel with 1
- `server/Sim/World.cs` — adjacency from `Alephs` at ctor end; `Gate? NextGateTo(uint from, uint to)` (precomputed all-pairs next-hop; graph is ~10 nodes).
- `server/Sim/Simulation.cs` — upgrade the player-autopilot `CrossSector` local (`:1319`) from direct-gate `AlephTo` to `NextGateTo` (players get multi-hop autopilot free). **Do NOT touch PIG decide paths** — `AlephTo` callers in `Simulation.Pig.cs` are determinism-guarded.
- **Tests**: next-hop on a 3+ sector chain map fixture; unreachable ⇒ null (caller disengages, matching today); AutopilotTest 2-hop arrival.

### 3. Wire + client: rock class, live shrink (after 1; client half Δ)
- `Protocol.WriteRockStatic` (`:400`) — append `u8 rockClass | f32 currentRadius | u8 orePct` (late joiner/discoverer sees shrunk state immediately; client never re-derives the shrink curve). Proto bump in `shared/Net/Wire.cs`.
- New `MsgRockUpdate = 22`: `[u8 count] × (u64 id | f32 radius | u8 orePct)`, on-change + coarse keepalive (minefield cadence pattern, `ClientHub.cs:876`). Fog on ⇒ per-team filter by `DiscoveredRocks` (no enemy-mining leak; fog-off broadcast). Sim exposes changed-rock list cleared per step.
- `ShipFlagMiner = 32` in `WriteShip`; `ShipClassDef.OreCapacity` into `MsgDefs`.
- Δ Client: `GameNetClient.ReadRockStatic:1203` + `MsgRockUpdate` handler → `NetTypes.Asteroid` new fields; `WorldRenderer.InsertAsteroid:1555` spawns at current scale, new `NetUpdateRock` lerps mesh scale + updates client collision rock (verify `CollisionWorld` radius update; else remove/re-add — client prediction must track the shrunk hull); target bracket + F3 show class name + `DEPLETED` at 0%.
- **Tests**: FogTest-style — updates only for discovered rocks; welcome-after-shrink carries current radius.

### 4. Miner hull content + asset checkpoint (parallel; Δ after asset pick)
- **⚠ USER CHECKPOINT**: render screenshots of the `*mnr*` candidates in `pick-assets/` (`tfmnr`, `ta_mnr`, `wc_gtmnr`, `rainmnr/v2/3`, `p1_mnr`, `weed_mnr`, `faohshmnr`, `dn_faphshmnr`, `drgmnr`, `dminr`, `quizmnrv3`) and ask the user to pick. Then: copy GLB → `client/assets/ships/`, `godot --headless --import`, bake collision via the `collision-hull-generator` skill (`--kind ship`), verify HP_ nodes via the `hardpoints` skill (needs ≥1 engine nozzle; no weapon HPs).
- `factions/` `Hull` model — `ore-capacity` field (+ `CoreValidator`: an ore hull may be unarmed); `FactionsContentProjection.cs` → `ShipClassDef.OreCapacity`.
- `server/Content/core/hulls.yaml` — `miner`, class-id 4: slow (~70), heavy, no weapon hardpoints, decent hull + shield, `price` (~400), `ore-capacity`, hull vision values (it scouts as it flies), `required-capabilities: [base]`. Reject player `MsgSpawn` of class 4 (AI-only hull).
- Nice-to-have (Δ, optional): `miningsound.ogg` harvest loop via `SfxManager.PlayAt`.
- **Tests**: ContentTest/FactionsTest — projection carries OreCapacity; validator accepts unarmed ore hull; stock bundle boots.

### 5. Harvest core: ore transfer + physical shrink (after 1+4)
- `ShipSim`: `float Ore`, `bool IsMiner`. Harvest transfer (test-seam callable): within `miner-standoff` of target rock, move `min(rate·dt, OreRemaining, capacity−Ore)`; recompute `CurrentRadius`; mark changed.
- Shrink propagation (the careful part): `World.RockBodies[id]` scale reassigned (dict of structs) so hull collision tracks; sphere-fallback + bolt-vs-rock resolution route through `RockCurrentRadius(id)` at resolution point; **grid membership stays spawn-size** (conservative — rocks only shrink); autopilot kind-2 standoff (`:1386`) uses current radius. PIG avoidance + vision occlusion stay at spawn radius v1 (conservative, documented).
- **Tests** (new `tests/MiningTest`, cloned from `tests/StrategyTest` boot pattern): exact ore/radius arithmetic; depletion floor; `RockBodies` scale tracks; two-sim determinism.

### 6. Miner AI: slots, brain, claims, offload loop (after 2+4+5) — **hardest stream, no delegation**
- New `server/Sim/Simulation.Miner.cs`: `MinerSlot { MinerId, Team, Ship, State, TargetRockId, LastRockId, RespawnAtTick, HomeBaseId }`; `enum MinerState { Launching, ToRock, Harvesting, ToBase, Docking, Offloading, IdleDocked }`. 1 free slot/team seeded in `StartMatch`; killed ⇒ slot removed (repurchase only, no pod ejection).
- `TeamState` (`World.cs:98`): `HashSet<uint> AuthorizedMiningSectors`, reset each match to team garrison sector(s).
- Brain (5 Hz, `tick % PigBrainEvery`): select over **team-discovered** He3 rocks in authorized sectors with ore > 0 — (c) prefer `LastRockId`; (a) exclude friendly-claimed (per-team claim dict, released on retarget/death/depletion) unless miners > eligible rocks; (b) nearest by route (hop count, then euclid). Full hold or no eligible rock with cargo ⇒ `ToBase` to nearest friendly base, **re-evaluated every decide tick** (handles base destroyed inbound; zero bases ⇒ Idle).
- Execute (20 Hz via `InputFor:1214` `IsMiner` branch): `AutoSteer.ApproachPoint`/`SteerToPoint` as-is (never modify the PIG-determinism-critical bodies); cross-sector legs steer to `NextGateTo` gate mouth (`TryWarp` transits); `Docking` reuses the 3-phase `DockApproach` via the ship's `ApDock*` fields.
- Offload branch in the dock-trigger pass: `Credits += (int)(Ore × credits-per-ore-unit)`; `TeamStateChangedThisStep = true` (rides existing `MsgTeamState`, zero wire work); despawn + timed relaunch.
- Idle/depleted: fly home, dock, `IdleDocked`, one chat notice; re-check each brain tick (`/mine` order or repurchase wakes it). `PigBrainStep` skips `IsMiner`.
- **Tests** (MiningTest): full loop harvest→fill→return→offload→credits→relaunch→same-rock resume; claim rules a/b/c incl. miners>rocks override; 2-hop cross-sector return; authorized-sector gating; discovered-only gating; base-destroyed reroute; kill ⇒ slot removal; paycheck + mining income both accrue.

### 7. Purchase + orders via chat (plumbing Δ, parallel vs stubs)
- Δ `client/scripts/Chat.cs` — relay `/buyminer`, `/mine <sector>`, `/miners` (pattern `/pigs`); `/help` text.
- `server/Net/ClientHub.cs` `HandleCommand:732` — resolve team (reject NoTeam), sector by case-insensitive name or id; `EnqueueMinerBuy(team)` / `EnqueueMineOrder(team, sector)`; `SystemTo` errors (at cap / too poor / bad sector / not Active), `SystemAll` announcements ("X bought a miner — 2/4").
- `Simulation` drain: buy = cap check + `TryReserveSpawn(team, MinerClassId)` (`:1198` — already team-level unlock+charge, reused verbatim); order = `AuthorizedMiningSectors.Add` (idempotent).
- **Tests**: sim-side enqueue APIs (cap, charge, gating); chat round-trip via `verify` skill.

### 8. Integration: tests, docs, runtime verify — Δ
- `tests/MiningTest/` csproj wired like StrategyTest (content beside binary, PASS/FAIL, non-zero exit); consolidate stream assertions + pinned-seed layout golden.
- Full suite re-run (Autopilot/Missile/Mine/Fog/Strategy/Content/Flight) — note 6 pre-existing content-drift failures on master (Shield/Content/Factions) are the known baseline.
- `GLOSSARY.md` (mining/miner/rock-class terms), `.PLAN/README.md` roadmap tick, schema doc comments.
- `verify` skill: buy miner, watch harvest + visible shrink + credit ticks + cross-sector return; screenshots.

## Execution / delegation

Per CLAUDE.md + user instruction: **build work delegated to Opus subagents**; I (Fable) hold streams needing hard reasoning and review all diffs.
- **Wave 1 (parallel Opus)**: 1 (I spec the RNG derivation, Opus does YAML/loader/schema), 2, 4-content (asset checkpoint blocks only the GLB wiring, not the YAML).
- **Wave 2**: 3 (I own fog filtering; Opus does client render half), 5 (I own collision propagation).
- **Wave 3**: 6 (mine, not delegated), 7 plumbing (Opus).
- **Wave 4**: 8 (Opus) + my review pass.
- Repo auto-commits+pushes mid-session — keep each stream compiling + tests green before moving on.

## Verification
1. `dotnet test`-style suite runs: new MiningTest + all existing determinism suites (esp. the pinned-seed layout golden and Autopilot/Fog).
2. `--selftest` server boot with the new content (validator gates).
3. `verify` skill end-to-end: headless server + client, `/buyminer`, observe harvest, shrink, MsgTeamState credit growth, 2-hop return, offload/relaunch; capture screenshots/movie.
4. Protocol bump smoke with `--autofly` (dotnet suites don't cover the Godot client).

## Open items
- Miner mesh choice — user picks from rendered `*mnr*` screenshots (workstream 4 checkpoint).
- Balance seed: propose full miner load ≈ 2–3 paychecks; playtest-tune knobs.
- Deferred (documented, not built): shrunk-rock vision occlusion + PIG avoidance at spawn radius; refinery/shipyard uses for uranium/silicon/carbonaceous; commander authority tightening; miner escort AI / VO lines.
