# Remote-Ship Motion Fidelity — phased, measured (stress_test branch)

## Context

Two reported symptoms: AI miner drones look **skippy/rubberbandy** in flight, and **ramming feels wrong**. Exploration (file:line-verified) found general root causes — miners are just the ship that exposes them:

- **Cadence undersampling:** AOI tiers stream same-sector ships at 20Hz ≤600u, 6.7Hz to 1500u, 2Hz beyond — hard thresholds, no hysteresis (server/Net/ClientHub.cs:2284-2420). A same-sector miner beyond 600u never gets 20Hz; its bang-bang {0,1}-throttle trajectory (shared AutoSteer, 5Hz brain flips) undersamples badly at 6.7Hz. `SIM_MINER_MIDRATE` is an `IsMiner` special case (ClientHub.cs:35-40, :2361) — the kind of code the user explicitly forbids.
- **Fragile extrapolation:** MotionInterpolator dead-reckons past the newest sample but hard-caps at 250ms then **freezes** until the next sample (MotionInterpolator.cs:286-325) — glide-then-freeze-then-jump is the visible skip on coarse-cadence ships.
- **Ram temporal skew:** local ship predicts 3–15 ticks ahead (ShipController.cs:730-741); remote obstacles are tested at their **rendered** (100–800ms delayed) positions paired with ~60ms-eased near-current velocity — different instants (world/ShipRenderer.cs:215-240, RemoteShip.cs:189). Server resolves both ships same-tick, no lag comp (Simulation.cs:3506-3552). Mispredicted contact ⇒ reconcile ⇒ the spring-eased rubberband. Reconcile replay uses present-time obstacle poses (PredictionController.cs:622-638) — a second flaw fixed by the same change.
- **Two clocks:** remotes evaluate on raw `Time.GetTicksMsec()` (RemoteShip.cs:218); the own ship renders off a slewed fixed-dt accumulator — frame hitches show as relative jank. (No `RenderClock` exists in the repo; the memory claiming one is wrong and will be corrected.)

## Hard constraints

- **NO ship-kind conditionals in any motion path** (server cadence, interpolator, prediction, obstacles). `MinerMidRate`/`IsMiner` is deleted and subsumed by a general motion-based policy. Success check: `grep IsMiner server/Net/` empty; no Kind/IsMiner/IsPig in MotionInterpolator/ShipObstacles/PredictionController collision path.
- **Do not modify `shared/AutoSteer.cs`, `shared/FlightModel.cs`, `shared/Collision/Collide.cs`** (PIG determinism + tests/FlightModelTest goldens). Zero diff under shared/ in every phase.
- **No wire-format change / no protocol bump** — send-selection and client-side only.
- Commit-only-on-measurable-improvement (metrics + visual evidence, or clear maintainability win with zero regression); discard failures via `git restore`. One Opus subagent per phase doing code + measurement. CSharpier only on touched files.

**Precondition (coordinator, before Phase 0):** finish the paused marker-cap verification (uncommitted changes in TargetMarkers.cs/ShipController.cs — resume the paused agent to A/B, then commit or discard). Clean tree before Phase 0.

## Shared measurement protocol

Release build + dll mirror (`dotnet build -c Release` + cp Release→Debug; every run must log `[build-config] managed=RELEASE`); server `run-server.ps1 -Local --autostart` (+ scenario flags); logs/movies to session scratchpad; steady-state = last 2s-report lines after warmup; normal-play smoke (grep `SCRIPT ERROR|Exception`) per phase; tests/FlightModelTest green after every phase; kill :8090 and rebuild Debug when done.

## Phase 0 — Instrumentation + moving-fleet harness + baselines (no behavior change)

**`[interp-stats]`** — new `client/scripts/InterpStats.cs` (clone PerfBuckets' Enabled/zero-cost/2s-report pattern; report from the Hud 2s block next to ReportPerfBuckets, Hud.cs:315-322). Hooks inside MotionInterpolator `Push`(:131)/`Evaluate`(:221)/`EvaluateRaw`(:252), guarded by `InterpStats.Enabled`. Ships classified into tiers **by observed `_gapEma`** (full <75ms / mid <250ms / coarse ≥250ms — never by kind). Per tier: `n`, `gap_p50/p95`, `delay_avg`, `extrap_pct`, `freeze_pct`+`freeze_events` (hits of the 250ms cap — the skip signature), `err_p95` (|_posErr|), `snaps`, `acc_p95/max` (jerk proxy: second difference of final output pose, excluded 2 frames post-Reset/snap), `hitch_frames` (wall dt >25ms), `worst=<shipId>`. **Jerk gating rule:** `acc_p95` is a gate only in `--fixed-fps` movie runs (uniform dt); advisory live. If two identical B2 runs differ >±15% on it, demote to advisory everywhere.

**`[predict-stats]`** (own-ship ram line, same block): reconciles-in-window + max error (fields exist, Hud.cs:473) + `local_hits` — capture the discarded contact `out` in PredictionController.ResolveCollisions (:147).

**Gating:** new `--interp-stats` flag in ShipController's arg loop (:215) — independent of PerfBuckets so it runs without stress-fx.

**Moving-fleet harness (general scripted movers):** `SIM_STRESS_MOVE`/`--stress-move` (server/Program.cs beside :101-103) → `StressMoveEnabled`. New server-only `bool Scripted` on ShipSim (set only by stress seeding, never on wire, never read by Step). `SeedStressFighters` (Simulation.Stress.cs:27-133): under the flag, split count into groups at 300u/1000u/2200u from the anchor base (hits all cadence tiers for a base-parked client). `InputFor` (Simulation.cs:1795): `if (s.Scripted) return ScriptedInput(s, tick)` — pure function of (tick, ShipId): even ids smooth circles (Thrust=1, Yaw=0.35), odd ids bang-bang (Yaw=±1 flip every 20 ticks, id phase offset) — reproduces the AutoSteer *character* without touching AutoSteer.

**Baselines (Release, `--interp-stats`, ≥90s logs):** B1 miner solo (server + one `--autofly` client; the auto-seeded free miner is the subject — metrics only). B2 moving fleet (`--stress-move --stress-fighters 24`; one live run + one `-WriteMovie MOVIE_FPS=60` run for fixed-fps acc_p95 + visual evidence; run B2 twice → run variance defines "material improvement" margins). B3 two-client ram (workflow below; `[predict-stats]` both sides, movie on observer only).

**Gate (commit test):** zero overhead disabled ([perf-buckets] rship unchanged); **self-validation**: measured gap_p50 must reproduce known server cadence (full ≈50ms, mid ≈150ms, coarse ≈500ms) — wrong numbers = broken instrumentation; smoke clean. Maintainability/measurement commit.

## Phase 1 — Server cadence generalization (kill MinerMidRate)

`server/Net/ClientHub.cs` only (+Program.cs knob aliases). **Motion-based mid-rate floor:** knobs `SIM_MOVING_SPEED_ENTER` (~2.0 u/s) / `SIM_MOVING_SPEED_EXIT` (~1.0) beside :29-33; per-ship hysteresis bit computed once per tick in the AfterStep pre-pass (:1295-1301) from `s.State.Vel`, stored in a ClientHub-side array keyed by ShipId (don't touch ShipSim). Mid branch :2359-2362 → `if (d2 <= r2sq || moving[i])`; **delete** the `MinerMidRate && IsMiner` clause and the :35-40 knob (grep-verified sole use). New `[aoi-stats]` server log ~5s (piggyback LogQueuePressure :1254-1266): records/s, snapshots/s, moving count, `_lossyDropped` delta.

**Tier-boundary hysteresis (1b):** only if B2 shows boundary thrash — widen exits ~10% (660/1650) via per-client `Dictionary<ulong, byte tier>` pruned on despawn; skip and record the decision if the client absorbs the flapping.

Full-20Hz for close movers: **not now** — parked as `SIM_MOVING_FULLRATE_RADIUS` proposal, decided at the Phase 2 gate (bandwidth math: +760 B/s per promoted mover; 20 movers ≈ +15 KB/s/client — affordable if needed).

**Gate:** B2 re-run — coarse movers' gap_p50 ~500→~150ms, freeze_events/extrap_pct down proportionally, fixed-fps acc_p95 improved beyond variance. Parked-fleet control (`--stress-fighters` w/o `--stress-move`): `[aoi-stats]` records/s within +10% of baseline, parked ships still coarse. B1: miner gap ≤ old MinerMidRate behavior (≈150ms) — subsumption proven. `_lossyDropped` not elevated. `grep IsMiner server/Net/` empty.

## Phase 2 — Client interpolator graceful degradation (kill glide-then-freeze)

`client/scripts/MotionInterpolator.cs` only. Replace the 250ms cap+freeze (:292) with **velocity-decay dead-reckoning**: `τd = Clamp(k×_gapEma, ~250ms, ~800ms)`; `pos = last.Pos + last.Vel × τd × (1 − e^(−age/τd))` — C¹, asymptotic stop, no discontinuity. Orientation: scale the integrated rot-vec angles by the same factor before the :306-309 right-composition (preserve yaw→pitch→roll order). Replace `MaxExtrapolateMs` tunable with `ExtrapDecayGapFactor`/`Min/MaxMs`; consider gap-adaptive `ErrorDecayRate` (landing-glide ≈0.3×gap clamped [100,300]ms); keep `SnapDistance=100`. Touch the 3×chord tangent clamp (:335-337) only if B2 shows between-sample overshoot spikes. Update the header comment (:26-33). InterpStats: `freeze_*` → `extrap_age_p95`.

**Gate (cadence-pinned isolation):** run with `SIM_FULLRATE_RADIUS=1 SIM_MIDRATE_RADIUS=2` (forces coarse streaming) so P2's win is proven independent of P1's cadence gains. freeze_events=0 steady-state (B1/B2); fixed-fps acc_p95 ≥50% down vs post-P1 on coarse/mid movers (beyond variance); full-rate tier within variance (full-rate ships almost never extrapolate — any change there is a bug); snaps=0 steady; err_p95 ≤1.5× post-P1 (longer extrapolation = bigger landing error, bounded); B2 movie visually clean.

## Phase 3 — Ram time-alignment in prediction (rendering untouched)

- `MotionInterpolator`: add `TryGetLatest(out serverMs, pos, rot, vel, angVelLocal)` exposing the newest sample (raw Vel, not TanVel).
- `PredictionController`: `SetShipCollisionProvider` (:124-131) becomes `Func<uint predTick, IReadOnlyList<Collide.MovingShip>>`; `ResolveCollisions` gains the tick — called from `Step` (:445) and from **reconcile replay** (:630) with each replayed entry's tick ⇒ replay becomes time-aligned for free.
- `ShipRenderer.ShipObstacles(uint targetTick)` (:215-240, sole wiring :377): per ship `dt = clamp(targetTick×50ms − latest.serverMs, 0, ~300ms)`; pos = latest.pos + latest.vel×dt, rot advanced by angvel, vel = latest.vel — pos+vel from the SAME instant. Fallback to rendered pose if TryGetLatest fails. Cap keeps stale coarse obstacles from being flung; ram targets are near ⇒ full-rate ⇒ cap rarely binds.
- Recommended: server `[ram]` log line where Pass C applies ram damage (ids, tick, closing speed) — ground truth for false-positive correlation. Log-only, no sim change.
- Phase 3 also logs `sep_at_hit` (rendered separation at local_hits tick) in InterpStats — feeds the Phase 5 design note.

**Gate (B3):** median LastReconcileError in windows containing local_hits reduced ≥50% vs baseline; reconciles-per-contact down; local_hits vs server `[ram]` lines show no increase in client-only hits; near-miss run (autofly past the parked stress line) keeps local_hits=0. Subjective A/B: one human-piloted ram each way — "hit registers where the ship is."

## Phase 4 (conditional) — Render clock unification — **RESOLVED 2026-07-23: NOT NEEDED**

The two-clocks jank this phase targeted was root-caused elsewhere: the OWN ship's render
interpolation (`_tickTimer/Dt` alpha clamp in PredictionController) stalled a frame and
double-stepped on the tick/frame beat — 0.9–2u one-frame camera kinks that dwarfed the
~0.1u wall-clock shimmer. Fixed by driving alpha from ShipController's tick accumulator
(`pc.RenderAlpha = _acc/Dt`); bolts moved to accumulated-delta time in the same pass.
Post-fix close-follow chase (--ram-test <80u, interpolator STILL on GetTicksMsec):
chased-ship camera-space kink med 0.104u / p90 0.276u — the wall-clock term is
second-order, below perception. See memory `own-ship-render-alpha`. Do not execute this
phase unless close-follow jank is still reported in real play after the RenderAlpha fix.

<details><summary>Original (superseded) sketch</summary>
Execute ONLY if post-P2 logs show acc spikes on **full-rate** ships coinciding with `hitch_frames` (two-clocks jank). Sketch: shared accumulated-delta render clock (own-ship slew term) fed to both `Push` and `Evaluate` wall-clock args — `_clockOffset` EMA adapts transparently; samples stay tick-stamped (this is NOT the arrival-time stamping the header :9-14 rejects). Risk: couples remote smoothness to own-ship corrections — gate must include a reconcile-storm run (P-key divergence injection, ShipController.cs:716-720). High-risk, discard readily.
</details>

## Phase 5 (deferred — design note only, produced with Phase 3)

Residual visible ram offset after P3 = adaptive delay (≥100ms) + prediction lead ⇒ rendered separation at predicted contact ≈ v×(delay+lead). Record post-P3 `sep_at_hit` numbers; revisit forward-rendering near ships only if human rams still read "hit before touch" AND sep_at_hit p50 > ~1.5 hull lengths. Not executed.

## Decision gates & attribution

- P0→P1: instrumentation self-validates against known cadence; jerk variance decides its gate status.
- P1↔P2 attribution: measure after each independently on identical scenarios; P2 additionally proven under pinned-coarse cadence. P1's own signature (gap_p50 shift) is unproducible by P2.
- P2 gate decides the deferred `SIM_MOVING_FULLRATE_RADIUS`.
- P3→P4 on hitch-correlation evidence only; P5 never executes here.
- Failed gate ⇒ discard tree, record numbers + hypothesis. Stop after two consecutive discarded phases.

## Two-client workflow (one Mac)

Server → Client B (target): `AUTOFLY_TEAM=0 ... --autofly --interp-stats --headless` (autofly supports headless — kills GPU/focus contention) → Client A (observer): windowed frontmost, `--autofly --interp-stats`, movie only here. B3 ram recipe: both clients same team/base (identical autofly pattern from one spawn ⇒ co-located flight, frequent genuine contacts); Phase 0 confirms contact frequency empirically, falls back to opposite-team (`AUTOFLY_TEAM=1`) + short human-piloted ram. Movie mode runs on --fixed-fps ⇒ CreateTimer wall-delays stretch — keep --ui-shot bounds modest.

## Success criteria

- **Miners smooth:** miner streams mid-rate at any same-sector distance via the general policy; freeze_events=0; coarse/mid acc_p95 within ~2× full-rate tier; B2 movie clean.
- **Rams fair:** per-contact reconcile error halved; zero phantom client-only hits; human thumbs-up.
- **General:** no kind conditionals (grep checks above); zero shared/ diff; protocol untouched; [aoi-stats] bandwidth within +10%; FlightModelTest green throughout.

## Critical files

server/Net/ClientHub.cs · client/scripts/MotionInterpolator.cs · client/scripts/PredictionController.cs · client/scripts/world/ShipRenderer.cs · server/Sim/Simulation.Stress.cs + InputFor (server/Sim/Simulation.cs:1795) · client/scripts/InterpStats.cs (new) · Hud.cs 2s block · client/scripts/ShipController.cs (flag parse)
