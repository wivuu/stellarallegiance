# 99 — Open Questions & Decision Log

Append here whenever a decision exceeds the spec, an API turns out different from what these
docs assumed, or a deferred choice needs to be made. Format: date, context, decision/assumption,
and whether a human should review.

## Pre-seeded items the agent will likely hit

1. **SpacetimeDB 2.0 scheduled-reducer mechanism.** `04` assumes `SimTick` is driven by a
   scheduled reducer at 20 Hz. Confirm the exact 2.0 scheduling API (scheduled table + reducer
   wiring) from the functions reference and record the concrete pattern used here.

2. **Shared math types.** `06` flags that Godot's `Vector3`/`Quaternion` may not match the
   module's math types. Decision needed: use a self-contained math struct in `shared/` (most
   robust) vs. per-side adapters. Record which was chosen and why.

3. **`ShipInput` table access control.** `03` keeps it public for prototype simplicity.
   Decide post-prototype whether to restrict writes to the owning identity.

4. **Asteroid collision response.** `04` step 6 leaves ship-vs-asteroid collision optional for
   the first pass. Record whether it was included, deferred, or stubbed.

5. **Input send when unchanged.** `07` allows sending `ApplyInput` at 20 Hz even when input is
   unchanged. If reducer-call volume becomes a free-tier concern, switch to send-on-change +
   periodic keepalive and record the change.

6. **Client SDK package name/version.** `02` references adding the SpacetimeDB C# client SDK
   via NuGet. Record the exact package and version actually used, since this moves between
   releases.

7. **Maincloud free-tier limits.** Confirm current free-tier reducer-call / connection limits
   before the T10 two-machine test, and note headroom for the prototype's call volume.

## Decision log

| Date | Context | Decision / Assumption | Needs human review? |
|------|---------|------------------------|---------------------|
| 2026-06-01 | #2 shared math types (T3) | Used a **self-contained `Vec3`/`Quat`** in `shared/FlightModel.cs` (not Godot or System.Numerics types) so the module and client copies are byte-identical and engine-independent. All in namespace `StellarAllegiance.Shared`; `StatsFor(byte)` so the file depends on no game enum. Canonical copy is `shared/FlightModel.cs`; `shared/sync.sh` copies it verbatim into `module/spacetimedb/` and `client/scripts/`. | No |
| 2026-06-01 | Transcendental determinism (T3) | `Integrate` uses `Math.Sin/Cos/Sqrt`. Same source on both sides, and the 1e-5 test passes, but `sin/cos` *could* differ between the wasi-wasm module runtime and the Godot/.NET client runtime. Not observed yet; revisit if T5 reconciliation shows steady drift (consider a shared polynomial approx). | Revisit at T5 |
| 2026-06-01 | Angular drag stat (T3) | `.PLAN/03` stats table omits angular drag. Chose `AngularDrag` = 2.5 (Scout) / 2.0 (Fighter) as tunable placeholders; tune in T4. | No |
| 2026-06-01 | Local DB ownership | A stray `spacetime sql` auto-`login` overwrote `.stdb-config` with a non-owner identity → publish 403. Fixed by restarting the (ephemeral) server and republishing fresh. Avoid running CLI subcommands that trigger a fresh login against the mounted config. | No |
| 2026-06-01 | Ship angular velocity (T4) | The flight model integrates+damps angular velocity, but `.PLAN/03`'s `Ship` table had no angular field. **Added `AngVelX/Y/Z` floats to `Ship`** so rotational momentum persists between SimTicks and the client can reconcile against it. Schema change → republished `--delete-data`, regenerated bindings. | No |
| 2026-06-01 | SpawnShip phase guard (T4) | `.PLAN/04` says reject spawn unless `Phase == Active`, but Active needs both teams populated — impossible to fly solo for the T4 single-player gate. **Relaxed to allow Lobby or Active; only `Ended` blocks spawning.** Revisit if the two-team flow needs stricter gating. | Maybe |
| 2026-06-01 | #4 asteroid collision (T4) | **Deferred** — ships currently pass through asteroids. Build order defers collision response; revisit in/after T8. | No |
| 2026-06-01 | Prediction/server tick aliasing (T4) | Client advances one predicted step per `clientTick`; server advances one per `SimTick`. On localhost with near-zero latency, occasional (2–5 over a few seconds) reconciliations still fire from phase/step aliasing — not rubber-banding. `PredictionController.ReconcileCount` instruments this; harden + measure properly at T5. | Revisit at T5 |
| 2026-06-01 | Spawn inside base mesh (T4) | Ships spawn at the exact base center, inside the 45-unit base sphere (back-face culling makes it see-through from within, so the view is fine). Minor visual polish; could add a small launch offset later. | No |
| 2026-06-01 | `--autofly` dev flag (T4) | `ShipController` honors a `--autofly` command-line arg that auto-spawns a Scout and flies a fixed input — used for headless verification of the ApplyInput→SimTick→reconcile loop. Harmless dev affordance, left in place. | No |
| 2026-06-01 | Tuned flight constants (T4) | Using `.PLAN/03` placeholders: Scout thrust 45 / maxspeed 70 / lindrag 1.2 → terminal ≈35 u/s (observed). Mechanically responsive via prediction. **Subjective "is it fun" gate still needs a human at the keyboard** (WASD thrust/strafe, Space/Shift up-down, arrows aim, Q/E roll). | Yes (playtest) |
