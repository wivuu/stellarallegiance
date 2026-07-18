# Per-garrison build pipeline (parallel + queue limits) for the Build tab

## Context

Today the docked **Build** tab lets a commander order **constructors** (base builders) and **miner
drones**. Both flow through one server-side production list (`_constructors`, a `ConstructorSlot`
per order — `ProducesMiner` distinguishes miners). But every ordered item starts its `Producing`
countdown **immediately and in parallel**, and the only throughput gates are two unrelated caps:
`constructor.max-constructors-per-base` (live constructors per garrison) and
`mining.max-miners-per-team` (live miner drones per team). There is no way to author a real *build
pipeline* — "build one thing at a time, queue the rest."

**Goal:** a faction/world-configurable pipeline scoped **per garrison** (confirmed with user), shared
across miners and constructors:

- `build-parallel-limit` — how many ordered items may be actively **building** (counting down) at
  once at a garrison. The rest sit **QUEUED** at 0% progress.
- `build-queue-limit` — total ordered items (building + queued) a garrison may hold. When full, the
  Build tab **grays out every buy button + card** ("BUILD QUEUE FULL").

**Scenario (parallel=1, queue=2):** order a miner, then a constructor → the miner builds, the
constructor shows QUEUED at 0%. All other Build items gray out (queue is full at 2). When the miner
**launches** (leaves the pipeline), the constructor promotes and starts its build time.

**Decisions (confirmed with user):** scope is **per-garrison**; the new limits **replace**
`max-constructors-per-base` (removed), while `max-miners-per-team` **stays** as a separate live-drone
fleet cap. A constructor that has *launched* (flying out to build a base) has left the pipeline and
no longer counts toward the queue.

**Naming:** repo YAML is kebab-case, so the knobs are `build-parallel-limit` / `build-queue-limit`
(the user's `build_parallel_limit` intent, spelled to match `max-constructors-per-base` etc.).

**Protocol:** no `Wire.ProtocolVersion` bump (stays 34) — following the established convention, this
adds a body to the new `MsgBuyMiner=16`, appends one tail field to each `MsgConstructorState` row +
the `MsgTeamState` tail, and adds a new `ConstructorState` enum value at the END (existing byte
values unchanged).

---

## Changes

### 1. Config — new `WorldBuildTuning` + `build:` YAML block
- **`shared/Defs.cs`** (~L842, beside `WorldConstructorTuning`): add
  ```csharp
  public sealed class WorldBuildTuning {
      public int ParallelLimit = 4; // items actively building at once per garrison
      public int QueueLimit    = 4; // total ordered (building + queued) per garrison
  }
  ```
  Defaults 4/4 preserve today's constructor throughput. Add `public WorldBuildTuning Build = new();`
  to `WorldTuning` (beside `Constructor` at L636) and remove `MaxConstructorsPerBase` from
  `WorldConstructorTuning` (L844).
- **`server/Sim/World.cs`**: add `public readonly WorldBuildTuning Build;` beside `Constructor`
  (L60), wired in the same ctor/projection path.
- **`server/Content/WorldLoader.cs`**: add `WorldBuildDef { int? ParallelLimit; int? QueueLimit }`
  (mirror `WorldConstructorDef`, L473), parse a `build:` block, and bind with `??` fallback like
  L670. Remove `MaxConstructorsPerBase` from `WorldConstructorDef` (L476) and its bind (L670).
- **`server/Content/core/world.yaml`**: delete `max-constructors-per-base` (L183); add a new block
  with explanatory comments (leave stock at 4/4 — the user flips to `1`/`2` for the serialized
  behavior above):
  ```yaml
  build:
    parallel-limit: 4   # items a garrison builds at once (1 = strictly one-at-a-time)
    queue-limit: 4      # total ordered (building + queued) before the Build tab locks
  ```

### 2. Server pipeline — `server/Sim/Simulation.Constructors.cs`
- **New `Queued` state**: append `Queued` as the LAST `ConstructorState` value (byte 8) so 0–7 are
  unchanged. A queued slot has no running timer.
- **Slots start Queued**: `NewConstructorSlot` and `NewMinerProductionSlot` create the slot with
  `State = Queued` (don't stamp `PhaseStartTick`). `NewMinerProductionSlot` gains a `launchBaseId`
  param and stores it on `LaunchBaseId` (miners now belong to a garrison for pipeline scoping).
- **Promotion pass** `PromoteQueuedBuilds(uint tick)`: per `LaunchBaseId`, count `Producing` slots;
  promote `Queued` slots in list order (FIFO = purchase order) to `Producing` (stamp
  `PhaseStartTick = tick`) until `ParallelLimit` is reached. Set `ConstructorChangedThisStep` (and
  `TeamStateChangedThisStep` for miners). Call it (a) at the end of `DrainConstructorQueues` so a buy
  starts building the same tick when there's room, and (b) at the end of `ConstructorBrainStep` so a
  launch/graduation/retire frees a slot and the next queued item starts.
- **Queue gate** replaces the per-base constructor cap: add
  `BuildPipelineCountForBase(ulong launchBaseId)` = count slots (either type) with that
  `LaunchBaseId` in `{Queued, Producing}`. In `TryBuyConstructor`, replace the
  `ConstructorCountForBase >= MaxConstructorsPerBase` check (L349-353) with
  `BuildPipelineCountForBase(gb.Id) >= QueueLimit` → notice "This garrison's build queue is full
  (N)." Remove the now-unused `MaxConstructorsPerBase` property (L1048) and `ConstructorCountForBase`
  (L135, no other callers). **Keep** `ConstructorCount(byte team)` (used by ClientHub + tests).
- **Cancel covers queued orders**: in `CancelConstructorProduction` (L405) match
  `State is Producing or Queued` so a commander can cancel/refund a not-yet-started order.
- **View + brain**: `ConstructorStatesView` adds a `Queued` case (start=0, dur=`ProductionTicks` →
  reads 0% on the client) and a new tuple element `ulong LaunchBaseId`. The `Producing` brain arm
  (L493) is unchanged; only promoted slots reach it. `QueueLimit`/`ParallelLimit` read from
  `World.Build`.

### 3. Miner buy carries a garrison — `Simulation.Mining.cs` + hub + client
- **`EnqueueMinerBuy(byte team, ulong launchBaseId = 0)`** (default keeps the 11 test call sites +
  ClientHub compiling). `_minerBuyQueue` becomes `Queue<(byte Team, ulong LaunchBase)>`;
  `DrainMinerQueues` passes it to `TryBuyMiner(team, launchBaseId, tick)`.
- **`TryBuyMiner`** (L183): after the fleet-cap check (`MaxMinersPerTeam`, kept), resolve the
  garrison via `ResolveConstructorLaunchBase(team, launchBaseId)` (fallback `LaunchBaseId = 0` if a
  world has no garrison, so garrison-less test worlds still buy). Gate on
  `BuildPipelineCountForBase(gb.Id) >= QueueLimit`. Pass the garrison id into
  `NewMinerProductionSlot(team, tick, orderTicks, gb.Id)`. The `orderTicks == 0` instant path
  (`NewMinerSlot`, L224) stays fleet-cap-only (no build time to serialize).
- **`server/Net/ClientHub.cs`** (L847): read the u64 launch-base from the `MsgBuyMiner` body and pass
  it: `_sim.EnqueueMinerBuy(minerTeam, launchBaseId)`.
- **`client/scripts/GameNetClient.cs`**: `SendBuyMiner(ulong launchBaseId)` writes `[16][u64]`
  (mirror `SendBuildConstructor`, L379). **`BuildTab.OnMinerBuyPressed`** (L513) passes `_baseId`
  (the sidebar-selected/last-docked base already used for constructor buys).

### 4. Wire the pipeline to the client — `Protocol.cs` + `GameNetClient.cs` + `WorldRenderer.cs`
- **`Protocol.cs`**: (a) update `MsgBuyMiner=16` doc (L78) — now `u64 launchBaseId` body. (b)
  `BuildConstructorState` (L1219) writes `r.LaunchBaseId` per row after `ProducesMiner`; update the
  `MsgConstructorState` doc (L110). (c) `BuildTeamState` (L1162) appends one byte
  `(byte)world.Build.QueueLimit` after `minerCap`; update the `MsgTeamState` doc.
- **`GameNetClient.cs`**: `ApplyConstructorState` (L943) reads the trailing `ulong launchBaseId` per
  row; the `MsgTeamState` tail (L1253) reads `byte buildQueueLimit` and passes it to
  `NetUpdateTeamState`.
- **`WorldRenderer.cs`**: `ConstructorStatus` (L891) gains `ulong LaunchBaseId`. Store the streamed
  `BuildQueueLimit` (world-scalar) + accessor. Add
  `int BuildPipelineCountForBase(ulong baseId)` = count `_constructorStates` where
  `LaunchBaseId == baseId && State is 0 (Producing) or 8 (Queued)` (client mirror of the server gate).

### 5. Build-tab UI — `client/scripts/ui/BuildTab.cs`
- **Roster** `RebuildRoster` (L276): add `case 8` (Queued) → a QUEUED row —
  `"◷ QUEUED · {name}{suffix}"`, no bar / 0% (reuse `ConfigureNote`, or a 0%-pinned timed row), with
  the commander `✕ CANCEL` action (queued orders are cancelable, matching Producing). The
  `UpdateRoster` signature (L251) already keys on `State`, so Queued↔Producing transitions rebuild.
- **Queue-full gray-out**: let `full = BuildQueueLimit > 0 && BuildPipelineCountForBase(_baseId) >=
  BuildQueueLimit`. In `UpdateFooter` (L520) and `UpdateMinerFooter` (L470) add a top-priority
  disabled branch → `SetFooter(true, "⊘ BUILD QUEUE FULL ({N})", …)`. In `RebuildGrid` (L355) /
  `AddMinerCard` (L380) AND the card `available` flag with `!full` so every card dims too.
- **Re-render on change**: fold `BuildQueueLimit` and `BuildPipelineCountForBase(_baseId)` into
  `ComputeStatusSig` (L228) so the grid re-grays/re-enables as the queue fills and drains.

### 6. Tests — `tests/`
- **`tests/MiningTest`** already zeroes `OrderTimeSeconds` suite-wide (miners launch instantly, fleet
  cap only) and calls `EnqueueMinerBuy(0)` — the default `launchBaseId = 0` keeps it compiling; the
  world has a garrison so resolution + pipeline count behave. The dedicated produce→graduate test (a
  single order, default parallel 4) still promotes immediately.
- **`tests/ConstructorTest`** buys one/two constructors and expects immediate `Producing` — the
  same-tick `PromoteQueuedBuilds` (parallel default 4) preserves that. Update it only if it asserts
  the removed `max-constructors-per-base` message (grep showed none).
- **Add** a small case (extend MiningTest or ConstructorTest): with `Build = {ParallelLimit:1,
  QueueLimit:2}`, order two items at one garrison → assert exactly one `Producing` + one `Queued`,
  a third order refused ("queue is full"), then after the first launches the second promotes to
  `Producing`.

---

## Verification

1. **Build**: `dotnet build` server + shared; `godot --headless --import` then build the client.
2. **Tests**: `dotnet run --project tests/MiningTest` and `.../ConstructorTest` stay green; the new
   parallel=1/queue=2 case passes. (`ContentTest`/`ShieldTest` carry known pre-existing failures.)
3. **End-to-end** (verify skill): `scripts/run-server.sh --local --autostart` with a scratch content
   copy whose `world.yaml` sets `build: { parallel-limit: 1, queue-limit: 2 }`
   (`--content <scratch>/core/core.manifest.yaml`). Client `scripts/run-client.sh --local`, take
   commander, dock, open **Build**:
   - Order a miner then a constructor → miner shows PRODUCING, constructor shows **QUEUED** at 0%;
     all cards + both buy buttons gray out with **BUILD QUEUE FULL (2)**.
   - When the miner launches, the constructor flips to PRODUCING and starts its build time; a card
     frees up.
   - Cancel the queued constructor → refunded, slot frees.
   - Confirm `ProtocolVersion` stays 34 and a stock client connects.
