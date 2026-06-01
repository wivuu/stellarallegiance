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
