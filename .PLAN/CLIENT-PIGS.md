# Client-Side AI Drone Host (remove server-side pig AI)

## Context

**Why:** The server-side pig AI runs inside the single-threaded SpacetimeDB WASM module. Profiling the code shows the AI *brain* is already cheap (5 Hz `PigBrainTick` target selection + per-tick `PigExecute` re-steer); the real per-tick costs are physics (FlightModel integration, projectile×ship checks, pairwise collisions), which stay server-side regardless. So the goal of this refactor is **not** less server CPU per drone — it's (a) removing AI compute from the module entirely so it scales horizontally across client processes, (b) making drones first-class input-driven ships that use the exact same `ShipInput` path as players, and (c) letting any number of drone-host processes field opposing squads (`--team 0` vs `--team 1`).

**Key trap designed around:** N drones × 20 Hz individual `ApplyInput` calls = N×20 transactions/sec — worse than today's in-process AI. Solution: one **batched** `ApplyDroneInputs` reducer call per host per tick carrying all of that host's drone inputs.

**User decisions:** Open spawn (any identity) + server-side caps; no squad waves; per-drone lifecycle = launch → fight → podded → pod flies home → relaunch as scout or fighter; pods stay (server ejects them on death via the existing player path).

**Verified facts:** Godot client never reads `Pig`/`PigSquad`/`PigDecision` tables — rendering keys only off `Ship.IsPig` (keep that flag). RLS filters only restrict `ChatMessage`, so a drone client sees all ships/asteroids. Generated bindings (`client/module_bindings/`) have no Godot dependency — a console app can compile-include them. `PigsEnabled` on Match is toggled by an existing dev command (Lib.cs:860) and will gate drone spawning.

---

## Part 1 — Server module changes (`module/spacetimedb/`)

### 1a. Delete server AI

- **Delete `PigAI.cs` entirely** (Pig, PigBrainTimer, PigDecision tables + all brain/steering/lifecycle code).
- **Lib.cs:**
  - Remove `PigSquad` table (L204–211).
  - Remove the `else if (ship.IsPig)` input routing in SimTick Pass A (L1195–1203) — drone ships now read `ShipInput` like player ships (the very next branch).
  - Remove the `else if (s.IsPig) KillPig(...)` branch in the death pass (~L1461–1463) — dying drones fall through to the player path (`KillShip` → pod ejection).
  - Remove the `PigBrainTimer` schedule insert from Init, and pig-only helpers that become unused (`AnyTeamedPlayerOnline` if unused elsewhere, `DespawnAllPigs` references).
- **Ships.cs:** remove the two `FreePigPodSlot` calls (L197–199 in KillShip, L260–261 in DockShip).
- **Keep:** `Ship.IsPig` (client renders drones magenta off it; `SpawnDrone` sets it) and `Projectile.FromPig` (drones-erode-bases rule). Optional later rename to `IsDrone` — out of scope.

### 1b. New `module/spacetimedb/Drones.cs`

```csharp
[SpacetimeDB.Table(Accessor = "DroneShip", Public = true)]
public partial struct DroneShip
{
    [PrimaryKey] public ulong ShipId;          // live drone OR its pod after death
    [SpacetimeDB.Index.BTree] public Identity Owner;  // the drone-host identity
}

[SpacetimeDB.Type]
public partial struct DroneInput   // mirrors ApplyInput's args
{
    public ulong ShipId; public uint Tick;
    public float Thrust, StrafeX, StrafeY, Yaw, Pitch, Roll;
    public bool Firing, Boost, Coast;
}
```

Reducers:
- **`SpawnDrone(byte team, ShipClass shipClass)`** — guards (logged, not thrown, matching SpawnShipInternal style): Match exists + `Phase == Active` + `PigsEnabled`; team is 0/1; per-owner cap (`MaxDronesPerOwner = 8`) and global cap (`MaxDronesTotal = 32`) counted via `DroneShip.Owner.Filter` / `Iter`. Spawn at the team base with the small fan offset (lift the base-lookup + placement from old `SpawnPig` L426–477 / `SpawnShipInternal`), `IsPig = true`, then insert `DroneShip { ShipId, Owner = ctx.Sender }`.
- **`DespawnDrone(ulong shipId)`** — verify `DroneShip.Owner == ctx.Sender`, delete Ship + its ShipInput rows + DroneShip row.
- **`ApplyDroneInputs(List<DroneInput> inputs)`** — for each entry: `DroneShip.ShipId.Find` and check `Owner == ctx.Sender`, then upsert the `ShipInput` row. **Extract the upsert body of `ApplyInput` (Lib.cs L1083–1118) into a shared helper** `UpsertShipInput(ctx, shipId, tick, …)` and call it from both reducers.

### 1c. Ownership transfer through the pod cycle

- **KillShip / pod ejection (Ships.cs):** where the dying ship spawns its pod, add: if `DroneShip` has a row for the dying `ShipId`, delete it and insert one for the pod's new `ShipId` (same Owner). Skip the `Player.ShipId` update for drone ships (they have no Player).
- **DockShip + pod-destroyed paths (Ships.cs):** when a pod resolves (docked or destroyed), delete its `DroneShip` row if present. The host observes the row deletion and schedules a relaunch.

### 1d. Cleanup hooks (Lib.cs)

- **`ClientDisconnected`** (L741): despawn every drone owned by `ctx.Sender` (`DroneShip.Owner.Filter`) — delete ships, inputs, rows.
- **Match end / reset** (Phase→Ended transition and the reset at L1074): call a `DespawnAllDrones(ctx)` helper so drones don't linger between matches.

---

## Part 2 — Drone host console app (`tools/drone-host/`)

### Project
`tools/drone-host/DroneHost.csproj` — net8.0 console exe:
- `PackageReference SpacetimeDB.ClientSDK 2.3.0` (same as client)
- `ProjectReference ../../shared/Shared.csproj` (Vec3/Quat math)
- `<Compile Include="../../client/module_bindings/**/*.cs" />` (single source of generated bindings)

### Files
- **`Program.cs`** — arg parsing: `--count N` (default 3), `--team 0|1` (default 0), `--server URL` (default `ws://localhost:3001`), `--db NAME` (default `stellar-allegiance`). Build `DbConnection` (mirror `client/scripts/ConnectionManager.cs:61-93`: Builder → OnConnect → `SubscribeToAllTables`). Main loop on **one thread** (the SDK connection is not thread-safe): `FrameTick()` + fixed-step scheduler — 20 Hz steer/send, 5 Hz decide — with a short sleep; drones are cooperatively multiplexed objects, not threads.
- **`TickClock.cs`** — server-tick estimate: on each Match row update, record `(Match.Tick, Stopwatch timestamp)`; `EstimatedTick = lastTick + elapsed × 20 Hz + lead (≈2–3 ticks)`. Precision is non-critical: SimTick's latest-before-tick fallback (Lib.cs L1207–1227) replays the most recent input.
- **`DroneBrain.cs`** — per-drone state machine, **ported from PigAI.cs** with `ctx.Db` swapped for the client cache (`conn.Db.Ship.Iter()` etc.), plain `MathF` (determinism not required — drones are server-integrated, never predicted):
  - Decide @5 Hz: target selection / threat score (old `PigDecide` L553–705, `PigThreatScore` L961–987), patrol & attack-point modes.
  - Steer @20 Hz: chase with lead/juke/wobble + fire gate (`PigChaseInput` L742–821), steer-to (`PigSteerTo`), patrol (`PigPatrolFromCenter` L829–837), asteroid avoidance (`PigAvoidAsteroids` L918–953 — v1 can use a simple nearest-rocks scan over the cached Asteroid table for the drone's sector instead of porting the grid).
  - Pod mode: fly home to own base (old `PodThink` L849–869); engaged when the brain's DroneShip row re-points to a pod ShipId (`Ship.IsPod`).
- **`DroneManager.cs`** — reconciles desired vs actual: while Match is `Active` + `PigsEnabled` and owned-drone count < `--count`, call `SpawnDrone(team, class)` (alternate Scout/Fighter); map `DroneShip` rows where `Owner == LocalIdentity` to brains via row callbacks; on row deletion (pod docked/destroyed) schedule a relaunch after a short delay; each 20 Hz step, collect every brain's `DroneInput` and send **one** `ApplyDroneInputs` call. Console-log state transitions (spawned / target / podded / docked / relaunch).

---

## Part 3 — Build, publish, bindings

1. `dotnet build` the module; `scripts/publish-local.sh` (schema change — dropped Pig/PigBrainTimer/PigDecision/PigSquad, added DroneShip — may need `--delete-data` on the local DB).
2. `spacetime generate` → `client/module_bindings/` (removes Pig* types, adds DroneShip/DroneInput/new reducers). Godot client needs no script changes (verified: no Pig-table reads) — just rebuilds.
3. `dotnet build tools/drone-host`.
4. `graphify update .` after the code changes.

**Order of implementation:** server module first (1a→1d), publish + regen bindings, then the drone host (Part 2), porting brain code from the deleted PigAI.cs (recover via `git show` once deleted).

## Part 4 — Verification

1. Local server up (`spacetime start`), publish, run the Godot client; QuickJoin/ready a human player to start the match (matches only start when teamed players ready up), toggle the pigs/drones dev command so `PigsEnabled = true`.
2. `dotnet run --project tools/drone-host -- --count 5 --team 1` (opposing the human), then optionally a second host `--team 0` for drone-vs-drone.
3. Observe in the Godot client: magenta drones launch from base, chase/fire, eject pods on death, pods fly home, relaunch.
4. Cross-check with `spacetime sql` (Ship rows with IsPig, DroneShip rows) and `spacetime logs -f` for reducer guard messages; kill the drone host and confirm `ClientDisconnected` despawns its drones.
5. `dotnet test tests/FlightModelTest` — note 2 pre-existing failures on master (golden + boost) are not regressions.

## Risks / gotchas

- **Drone-host Player rows:** `ClientConnected` auto-creates a lobby Player row for the host identity. Verify it doesn't block `MaybeStartMatch`/ready checks (it should be ignored as teamless — confirm during implementation).
- **Reaction latency:** drone aim/fire now lags by network RTT like a player; expect slightly softer drones than the in-process AI. Acceptable.
- **Host crash ≠ disconnect timeout:** drones coast on last input until the websocket drops and `ClientDisconnected` fires.
- **Batched input size:** `List<DroneInput>` of ~8 entries per call at 20 Hz is small; if caps grow, consider sending at 10 Hz with `Coast`.
- **Trust:** any client can now field up to 8 drones (by design, "open + cap"). The cap + `PigsEnabled` gate is the only brake; revisit before any public deployment.
