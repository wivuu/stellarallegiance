# 08 — Build Order

Execute tasks **in this order**. Do not begin a task until the previous task's acceptance
test (in `09`) passes. Each task names the documents it depends on. Keep tasks in small
commits. If a task reveals a gap in the spec, log it in `99` and proceed with the most
reasonable assumption.

---

### Task 0 — Environment & skeleton
**Refs:** `02`
- Install toolchain, scaffold `module/` via `spacetime dev --template basic-cs`, create the
  Godot `client/` project (.NET build), set the repo layout from `02`.
- Get the stock template publishing to a local `spacetime start` server.
- Add the SpacetimeDB C# client SDK to the Godot project; make `ConnectionManager` connect
  to the local DB and log "connected".
- **Gate:** `09` T0.

### Task 1 — Schema & bindings
**Refs:** `03`
- Replace `module/Lib.cs` tables with the full schema from `03` (all tables + enums).
- Implement `Init` to insert the `Match` singleton, two `Base` rows, and the asteroid field.
- Publish; regenerate client bindings into `client/module_bindings/`.
- **Gate:** `09` T1.

### Task 2 — World rendering from subscriptions
**Refs:** `05`, `07`
- Subscribe to the world tables in `ConnectionManager`.
- `WorldRenderer` instances `Base.tscn` and asteroid meshes from the rows that arrive.
- No ships yet. Confirm the static world renders identically on two simultaneously-connected
  clients.
- **Gate:** `09` T2.

### Task 3 — Shared flight model
**Refs:** `06`
- Implement `shared/FlightModel.cs` (pure, fixed-`dt`, deterministic). Copy into `module/`
  and `client/`.
- Write a tiny standalone test (console or unit) integrating a known input sequence and
  asserting the same output on both copies.
- **Gate:** `09` T3.

### Task 4 — Local flight (prediction only, single player)
**Refs:** `04`, `05`, `06`, `07`
- Implement `SpawnShip` + `ApplyInput` reducers and the `SimTick` integration step (ships
  only; no weapons yet).
- Implement `ShipController` + `PredictionController`; spawn a Scout and fly it.
- Tune `FlightModel` constants until flying around the asteroids feels good.
- **Gate:** `09` T4 (includes the subjective "is it fun" check).

### Task 5 — Reconciliation correctness
**Refs:** `07`
- Verify prediction matches authority: instrument and confirm reconciliation rarely fires
  under good conditions, and recovers cleanly when you inject artificial divergence.
- **Gate:** `09` T5.

### Task 6 — Two clients, remote interpolation
**Refs:** `05`, `07`
- Run two clients. Implement `RemoteShipInterpolator`. Each client sees the other's ship move
  smoothly with the 100 ms interpolation delay.
- **Gate:** `09` T6.

### Task 7 — Both ship classes + spawn menu
**Refs:** `03`, `05`
- Add the Fighter class (stats from `03`), distinct mesh, and the HUD spawn menu with two
  buttons. Confirm both classes fly with their distinct feel.
- **Gate:** `09` T7.

### Task 8 — Weapons, damage, death
**Refs:** `03`, `04`
- Implement projectile spawning, advancement, culling, and hit resolution in `SimTick`.
- Render projectiles; handle ship death (despawn + respawn menu).
- **Gate:** `09` T8.

### Task 9 — Base damage & win condition
**Refs:** `03`, `04`, `05`
- Projectiles damage enemy bases; base at 0 health sets `Match.Phase = Ended` + `Winner`.
- HUD shows the match-end banner. A full match can be played start to finish.
- **Gate:** `09` T9.

### Task 10 — Maincloud deployment & two-machine test
**Refs:** `02`
- Publish the module to Maincloud; point two clients on two machines at it; play a full match.
- **Gate:** `09` T10. This is the prototype's definition of done.

---

## Stop conditions
The prototype is **complete** at Task 10. Do **not** proceed to commander view, multiple
sectors, mining, or tech paths. Those are the next milestone and out of scope here. If time
remains, spend it tuning flight feel and writing a short `RETRO.md` noting what the next
milestone should change about this architecture.
