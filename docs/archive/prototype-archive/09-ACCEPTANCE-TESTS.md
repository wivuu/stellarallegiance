# 09 — Acceptance Tests

Each gate is a concrete, checkable condition. A task is "done" only when its gate passes.
Where a test is subjective (flight feel), it still must be explicitly signed off, not skipped.

---

### T0 — Environment & skeleton
- [ ] `spacetime --version` reports 2.0.x and `dotnet --version` reports 8.0.x.
- [ ] The stock `basic-cs` module publishes to a local `spacetime start` server without error.
- [ ] The Godot `client/` project compiles in the .NET build of Godot.
- [ ] Launching the client logs a successful connection to the local DB.

### T1 — Schema & bindings
- [ ] `spacetime sql "SELECT * FROM Match"` returns exactly one row, Tick=0, Phase=Lobby.
- [ ] Two `Base` rows exist at distinct positions; the expected number of `Asteroid` rows exist.
- [ ] `client/module_bindings/` regenerated and the client project references the new types.

### T2 — World rendering
- [ ] Two clients connect simultaneously; both render the two bases and the full asteroid
      field at identical positions.
- [ ] No ship nodes exist yet.

### T3 — Shared flight model
- [ ] `FlightModel.cs` is identical in `module/` and `client/` (diff is empty).
- [ ] A test integrating a fixed input sequence for N ticks yields identical final state
      (within 1e-5) on both copies.
- [ ] Integration uses the fixed `dt`, confirmed by code review (no `delta` in the path).

### T4 — Local flight
- [ ] Calling `SpawnShip(Scout)` creates a `Ship` row and a controllable ship appears at the
      team base.
- [ ] The ship responds to thrust/strafe/rotation input with no perceptible input latency
      (prediction working).
- [ ] **Subjective sign-off:** flying the Scout around the asteroid field for two minutes is
      enjoyable — momentum and drag feel right, not floaty and not arcade-stiff. Record the
      tuned constants used.

### T5 — Reconciliation
- [ ] Under normal local conditions, reconciliation corrections are rare/imperceptible
      (instrument a counter).
- [ ] When artificial divergence is injected (e.g. nudge the server state), the client snaps
      and re-simulates to recover within a few frames without sustained rubber-banding.

### T6 — Two clients, remote interpolation
- [ ] With two clients each flying a ship, each client sees the other's ship move smoothly.
- [ ] Remote motion shows the intended ~100 ms interpolation delay, no teleporting or stutter
      under good network conditions.

### T7 — Both ship classes
- [ ] HUD spawn menu offers Scout and Fighter; both spawn and fly.
- [ ] The two classes feel distinct per the `03` stats (Scout faster/looser, Fighter
      slower/heavier).

### T8 — Weapons, damage, death
- [ ] Holding fire spawns projectiles that render and travel; they cull after their lifespan.
- [ ] Projectiles hitting an enemy ship reduce its health; at 0 health the ship despawns on
      all clients and the owner sees the respawn menu.
- [ ] Friendly fire does not damage teammates.

### T9 — Base damage & win condition
- [ ] Projectiles damage the enemy base; reaching 0 health sets `Match.Phase = Ended` and the
      correct `Winner`.
- [ ] All clients display a match-end banner naming the winning team.
- [ ] A complete match can be played from spawn to a decided result.

### T10 — Maincloud / two-machine (definition of done)
- [ ] Module published to Maincloud; two clients on two separate machines connect to it.
- [ ] A full match is played end to end across the internet with acceptable feel (flight
      responsive via prediction, remote ships smooth via interpolation).
- [ ] No desyncs observed: both machines agree on ship positions, health, and the final result.

---

## Global non-negotiables (check throughout)
- [ ] The client never mutates shared state except via reducer calls.
- [ ] Server is authoritative for all observable state; client predicts only the local ship.
- [ ] Flight integration is fixed-timestep and identical on both sides.
- [ ] Scope is held to the two-ship prototype — no commander view, sectors, mining, or tech.
