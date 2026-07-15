# Constructor / Base-Building — Polish Pass (proto v37 → v38)

## Context

The base-building MVP shipped (proto v37): a commander buys a constructor from the docked **BUILD**
tab, it launches from the garrison, is F3-ordered to a Regolith rock, sinks in, a glowing sphere
envelops the asteroid, and an Outpost appears. Six rough edges remain, all confirmed in code:

1. **No production queue / progress / cancel in the Build tab.** Buying a constructor
   (`TryBuyConstructor` → `NewConstructorSlot` → `SpawnConstructor`, `Simulation.Constructors.cs:197,212`)
   spawns the drone instantly with zero UI feedback — unlike the Research tab's progress banners.
   **Decided with the user:** the constructor must *not exist or launch until a production timer
   finishes*. Buying enqueues a timed production shown in the Build tab (progress bar + cancel, like
   research); only on completion does the drone launch from the garrison. Cancel while producing
   refunds the station price (nothing was built yet).
2. **Sector (minimap) and waypoint orders don't work.** `ApplyConstructorCommandOrder`
   (`Simulation.Constructors.cs:463`) hard-rejects everything except `OrderTargetRock`; miners accept
   Point(3)/Sector(4) via a `Prospect`-style move.
3. **No order visual.** The gold destination diamond is fully client-side (`SectorOverview` `_orderedPoints`
   → `_orderMarkerDraws`) and role-agnostic, but only a kind-3 *point* order populates it — and
   constructors reject those, so nothing ever draws. Enabling #2 makes the same miner visual appear.
4. **Build sphere appears too early and never hides the drone.** `UpdateBuildSpheres`
   (`WorldRenderer.cs:2656`) creates the sphere at phase 0 (Aligning, before the meshes intersect).
   The shells are additive (`blend_add`, `BuildSphere.cs:21`) so they can't occlude the drone.
5. **Build sphere never disappears from a finished base.** Root cause: `BuildConstructorBuilds`
   returns `null` when nobody is building (`Protocol.cs:531`) and the hub only sends when non-null
   (`ClientHub.cs:1531`), so the client never receives an "empty" frame → the stale record persists →
   the sphere lives forever on the new base. There is also no fade-out.
6. **Every base is named "GARRISON 0N".** `CommandSidebar.cs:124` hardcodes `$"GARRISON {n:00}"` with a
   running counter, ignoring type and location. **Decided:** name as **Type · Sector** (e.g.
   "OUTPOST · CINDER BELT"), with a numeric suffix only when two same-type bases share a sector.

**This bumps the wire to proto 38** (new per-team constructor-state stream + cancel message; v37 ↔ v38
refuse). Server + client deploy together.

---

## Phase A — Server: production timer, move orders, empty-frame fix

### A1. Production-before-launch (issue #1)
`server/Sim/Simulation.Constructors.cs`:
- Add `Producing` as the FIRST `ConstructorState` (before `Idle`). A slot in `Producing` has
  `Ship == null` and a countdown; the ship is spawned only when it completes.
- `NewConstructorSlot` (`:201`): create the slot in `Producing` with `PhaseStartTick = tick`; **do not**
  call `SpawnConstructor`. Add a production duration — const `ConstructorProductionSeconds` alongside
  the align/sink consts (`:45`), noted as promote-to-`WorldConstructorTuning` later. `ConstructorCount`
  already counts the slot (cap includes producing drones — correct).
- `ConstructorBrainStep` (`:269`): the per-slot loop currently `continue`s when `Ship == null` (`:276`).
  Add a `Producing` branch *before* that guard: when `tick - PhaseStartTick >= SecondsToTicks(
  ConstructorProductionSeconds)`, call `SpawnConstructor(slot, tick)` and set `State = Idle` (the
  drone launches from the garrison here). Producing slots have no ship, so `ConstructorExecute` /
  `ConstructorBuildsView` naturally skip them.
- **Cancel + refund:** add `CancelConstructorProduction(team, id)` — find the team's slot by
  `ConstructorId`; only if `State == Producing`, refund `StationCatalogFor(type).Price` to
  `ts.Credits` (`TeamStateChangedThisStep = true`), remove the slot, add a notice. (Post-launch
  constructors are managed by F3 orders, not this cancel — matches the user's "until the queue
  finishes" framing.)

### A2. Move-to-sector / waypoint orders (issues #2, #3)
`server/Sim/Simulation.Constructors.cs`:
- Add `MoveTo` state + slot fields `MoveSector`/`MovePos`/`MoveFromEntry` (mirror the miner
  `Prospect*` fields, `Simulation.Mining.cs`).
- `ApplyConstructorCommandOrder` (`:463`): keep the Rock branch; additionally accept
  `OrderTargetPoint` (set `MoveSector = sector`, `MovePos = pos`, `MoveFromEntry = false`,
  `State = MoveTo`, clear any `TargetRockId`) and `OrderTargetSector` (`MoveFromEntry = true`), copying
  the authorize/notice shape from the miner handler (`Simulation.Orders.cs:351-418`). Refuse ship/base
  as before.
- `ConstructorExecute` (`:404` switch): add `case MoveTo` — `CrossSector(MoveSector, …)` then
  `Approach(MovePos, holdDist)` (or fly the aleph when `MoveFromEntry`); on arrival hold (stay `MoveTo`,
  station-keeping) — the drone waits at the waypoint until re-ordered. Both helpers already exist.
- No client visual work needed: the shared `_orderedPoints` gold diamond + selection bracket already
  fire for a friendly commandable ship on a kind-3 order (`SectorOverview.cs:902`). Verify it draws
  for a constructor (it is a normal `RemoteShip`).

### A3. Empty-frame latch (issue #5, server half)
`server/Net/Protocol.cs` `BuildConstructorBuilds` (`:528`): instead of `null` when the set is empty,
return a **0-count frame** (`[25][0]`) while the set has been empty for fewer than ~`SecondsToTicks(1.5)`
ticks since the last non-empty step (track `_lastConstructorBuildTick` on the sim). This guarantees the
client — despite lossy delivery — receives the drop so it can begin the sphere fade. After the grace
window return `null` (silent when idle).

### A4. Per-team constructor status stream (issue #1, wire)
- `shared/Net/Wire.cs`: `ProtocolVersion = 38` + changelog.
- New `MsgConstructorState = 26` (s→c, per-team) and `MsgConstructorCancel = 27` (c→s) in
  `server/Net/Protocol.cs`.
- `ConstructorStatesView(team)` on the sim (extend the existing `ConstructorSlotsView`, `:90`): per slot
  `(u64 id, u8 stationTypeId, u8 state, u32 startTick, u32 durationTicks, u64 targetId)` where `state`
  is the `ConstructorState` ordinal; `start/duration` = the current phase window (production, or
  align/sink/build), so the client animates a smooth bar exactly like research.
- `Protocol.BuildConstructorState(sim, team)` mirrors `BuildResearchStateFor` (`:1087`). Hub
  (`ClientHub.cs`, next to the research send at `:1357`): build per-team, gate on a
  `ConstructorChangedThisStep || coarse` flag, `SendReliable`. Add the `MsgConstructorCancel` handler
  (`CommanderOrWarn`-gated → `_sim.CancelConstructorProduction(team, id)`), mirroring MsgBuildConstructor.

### A5. Tests
`tests/ConstructorTest`: update the buy path to advance `ConstructorProductionSeconds` ticks before the
drone exists (assert no ship until then, ship after). Add: cancel-while-producing refunds + removes;
a Point order puts a launched drone into `MoveTo` and it crosses toward the sector; a Sector order too.

---

## Phase B — Client: Build-tab panel, build-sphere lifecycle, base naming

### B1. Build-tab production/status panel (issue #1)
- `client/scripts/GameNetClient.cs`: decode `MsgConstructorState=26` → `WorldRenderer.NetUpdateConstructorState(list)`;
  add `SendCancelConstructor(ulong id)` (writes `MsgConstructorCancel`).
- `client/scripts/WorldRenderer.cs`: store the per-team list + a `ConstructorStates()` accessor and a
  `ConstructorProgress(start,dur)` helper (clone of `ResearchProgress`, `:766`).
- `client/scripts/ui/BuildTab.cs`: add a status section above/below the catalog that renders one
  `ActiveBanner`-style row per constructor (reuse `ResearchTab`'s `ActiveBanner` pattern / `ProgressUnderlay`):
  `PRODUCING · {station} {mm:ss}` with a commander `✕ CANCEL` → `_net.SendCancelConstructor(id)`; and
  read-only status for launched drones (`IDLE` / `EN ROUTE · {sector}` / `ALIGNING` / `SINKING` /
  `BUILDING {mm:ss}`) resolved via `_defs`/`SectorName`. Fold constructor state into `ComputeStatusSig`
  (`:194`) so the tab repaints as builds advance.

### B2. Build-sphere lifecycle (issues #4, #5)
- `client/scripts/BuildSphere.cs`: add a third **opaque core** shell (`blend_mix`, `depth_draw_opaque`,
  tinted to `Tint`) whose `ALPHA` is driven by a new `SetCover(float 0..1)` — 0 during sink start,
  ramping to 1 so it fully hides the drone. Add `BeginFade()` + an internal fade timer: once fading,
  ramp all shells' `energy`/`alpha`/cover to 0 over ~1.2 s in `_Process`, then `QueueFree()` itself.
- `client/scripts/WorldRenderer.cs` `UpdateBuildSpheres` (`:2656`):
  - **Create only at phase ≥ 1 (Sinking)** — skip phase 0 (Aligning), so the sphere first appears as
    the meshes begin to intersect. Envelop fraction: drop the phase-0 case; sink `0.2→0.6`, build
    `0.6→1.35`.
  - Drive `sphere.SetCover(...)` up through Sinking and hold at 1 through Building; when a build reaches
    **phase 2 (Building)**, set the constructor ship node `Visible = false` (look it up by `b.ShipId` in
    `_shipNodes`) — the drone is now hidden inside the opaque core (server despawns it at completion).
  - **Fade instead of instant free:** when a rock id drops out of the live set, call
    `sphere.BeginFade()` and move it to a `_fadingSpheres` set (keyed the same) rather than `QueueFree`
    immediately; the sphere self-frees after its fade. The base has appeared underneath (normal reveal
    path, `InsertBase`), so the fade reveals the finished, usable base. A1/A3 guarantee the drop frame
    arrives.

### B3. Base naming: Type · Sector (issue #6)
- `client/scripts/WorldRenderer.cs`: add `BaseTypeId` to the `_baseList` tuple (`:45`, populated at
  `InsertBase` `:1775` where `row.BaseTypeId` is in scope) and surface it through `KnownBases()` (`:137`).
- `client/scripts/ui/CommandSidebar.cs` `Refresh` (`:124`): replace `$"GARRISON {n:00}"` with
  `{typeName} · {sname}` where `typeName = (_defs.GetBaseDef(typeId)?.Name ?? "BASE").ToUpperInvariant()`
  and `sname` is the already-computed sector name. Group by `(typeName, sector)` and append ` {k}`
  (k ≥ 2) only when a pair repeats, so duplicates stay distinct. (This is the only base-label site.)

---

## Verification

```sh
dotnet build server/SimServer.csproj -c Release && dotnet build client/stellarallegiance.csproj
dotnet run --project tests/ConstructorTest -c Release      # production delay, cancel/refund, move orders
for t in tests/*/; do dotnet run --project "$t" -c Release; done   # baseline unchanged, no NEW fails
dotnet run --project server/SimServer.csproj -c Release -- --selftest
```

Baseline (no NEW failures): AutopilotTest 3, CollisionTest 4, ContentTest 2, FactionsTest 4, FogTest 1,
ShieldTest 1. Confirm a v37 client refuses a v38 server.

**Live sign-off** (`scripts/run-server.sh --local --autostart &` + windowed `scripts/run-client.sh --local`;
shorten `ConstructorProductionSeconds` + the outpost `build-time-seconds` for a fast loop): as commander,
BUILD tab → Outpost → BUILD → a **PRODUCING · OUTPOST mm:ss** row with a working ✕ CANCEL (refund) appears;
let it finish → a drone launches from the garrison. F3, select it, right-click empty space → gold
destination diamond (same as miners); right-click a Regolith rock → it flies, aligns, and only as it
sinks in does the sphere appear, go opaque, hide the drone, then the outpost pops and the sphere fades
out. CommandSidebar shows "OUTPOST · <sector>" and "GARRISON · <sector>", not "GARRISON 0N".

## Key files
- **Server:** `server/Sim/Simulation.Constructors.cs` (Producing/MoveTo states, production timer,
  cancel/refund, order accept), `server/Net/Protocol.cs` (`MsgConstructorState`/`MsgConstructorCancel`,
  builds empty-frame latch), `server/Net/ClientHub.cs`, `shared/Net/Wire.cs` (v38).
- **Client:** `client/scripts/GameNetClient.cs`, `client/scripts/WorldRenderer.cs` (state stream +
  sphere lifecycle + `_baseList` typeId), `client/scripts/BuildSphere.cs` (opaque core + fade),
  `client/scripts/ui/BuildTab.cs` (production panel), `client/scripts/ui/CommandSidebar.cs` (naming).
- **Tests:** `tests/ConstructorTest`.
