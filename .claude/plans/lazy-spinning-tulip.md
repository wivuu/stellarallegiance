# Miner polish & fixes

## Context

The mining/economy system shipped on the `mining` branch (proto 32). Playtesting surfaced seven
rough edges across the F3 tactical map, the mining-beam VFX, the miner drone's server steering,
client motion smoothing, and asteroid-type generation. This plan fixes all seven. Two shaping
decisions from the user:

- **Asteroid types are map-defined, and a sector is overwhelmingly common rock.** The rare types are
  a tiny minority: a fixed **He-3 count per sector (default 4)** and **at most 1 "special" rock
  (Silicon / Carbonaceous / Uranium) per sector** — unless a map YAML overrides either count. **All
  remaining rocks in the sector are common Regolith / Ice**, so the field reads as mostly plain rock
  sprinkled with a handful of He-3 and one special. (Today ~100% of non-He3 rocks are forced into
  one of the three "special" classes — nothing is common.)
- **The mining beam gets the exact target rock over the wire** (small protocol bump) instead of the
  client guessing the nearest He-3 rock.

---

## Issue 1 + 2 — F3 shows fewer targets than the cockpit HUD (esp. miners), and pre-launch F3 feels different

**Root cause (the big one).** `client/scripts/TargetMarkers.cs:732-734`:
```csharp
var local = _world.LocalShip;
if (local == null) return;
```
Everything after this line — **friendly ships (the miner!), enemy ships, focus tags** — is skipped
whenever `LocalShip == null`, i.e. while docked in the hangar / pre-launch. So the pre-launch F3
map draws only alephs, probes, minefields, ghosts, and a focused rock — no ships at all. This is
the single cause of both "miner doesn't show in F3" (observed pre-launch) and "F3 doesn't show the
same targets" (hangar vs launched).

**Fix.**
- Move the **friendly-ship** and **enemy-ship** draw loops (`TargetMarkers.cs:736-758`) and their
  focus-tag blocks **above** the `local == null` return so they render in every state. They only
  need `_world.FriendlyShips()` / `EnemyShips()` (which already include miners as ordinary team
  ships) and `Cam` — not `local`. Keep the genuinely ship-centric readouts (aim reticle, lead
  crosshair, incoming-missile banner — already gated by `!SectorOverview.Active` at line 787) after
  the gate.
- For the **focus tag range** text, guard the `local`-relative range so it degrades gracefully when
  `local == null` (show the tag without a range, or range from `ViewSectorCenter`).

**Pan-feel difference (the "pans more slowly" half of #2).** `Pan()` (`SectorOverview.cs:522`)
computes world-units-per-pixel as `wpp = 2·_dist·tan(fov/2)/viewH` — pan speed scales with `_dist`.
On `Open()` (`SectorOverview.cs:240-262`) pre-launch sets `_target = ViewSectorCenter` and
`_dist = min(700, radius)`. On a large home/hub sector, capping at 700 leaves the camera *zoomed in*
relative to the sector, so a drag covers little ground → "slow." Launched, `_target` is the ship and
you're effectively framing a smaller working area.
  - **Fix:** when pre-launch (`LocalShip == null`), frame the whole sector the way `SwitchView`'s
    non-local branch already does (`SectorOverview.cs:415-416`): `_target = ViewSectorCenter`,
    `_dist = Clamp(r*1.5, MinDist, r*3)`. Zoom-to-fit makes pan coverage proportional to the sector,
    matching the launched feel. Reuse that exact expression so the two paths stay identical.

**Files:** `client/scripts/TargetMarkers.cs` (move ship draws above the gate),
`client/scripts/SectorOverview.cs` (`Open()` framing).

---

## Issue 3 — Show rock-type labels on the F3 map (like the in-ship view)

**Today** (`TargetMarkers.cs:645-685`): rock labels are drawn only for the **nearest 3 rocks to the
local ship** (surface distance under `clamp(3·radius, 80, 400)`) plus the focused rock — and the
whole pass is gated on `_world.LocalShip is { } rockShip`, so pre-launch F3 shows none. The label
text builder `RockLabel` / `RockClassName` (`TargetMarkers.cs:1384-1406`) already produces
"Helium-3 340/1200", "Uranium", etc.

**Fix.** In the rock-label pass, when `SectorOverview.Active`, switch to an **F3 labelling mode**:
- Anchor proximity/selection to the **camera** (`Cam`) or `ViewSectorCenter` instead of `LocalShip`,
  so it works pre-launch and covers the sector rather than just around the ship.
- Relax the nearest-3 cap for the map. To avoid flooding a dense field, prefer labelling **all He-3
  and special rocks** (the interesting ones) always, and cap plain Regolith/Ice labels (e.g. only
  when zoomed in past a `_dist` threshold, or nearest-N to the camera focus). Reuse the existing
  on-screen/behind-camera cull and `RockLabel()` text unchanged.

**Files:** `client/scripts/TargetMarkers.cs` (rock-label pass ~645-685).

---

## Issue 4 — Mining beam stops short of the rock / points at the wrong rock

**Two causes.**
1. **Endpoint is the surface, not the center.** `MiningBeam.cs:85`:
   `impact = rockCenter - dir * rockRadius`. The beam deliberately ends on the surface, so it reads
   as "stopping short," and a stale/over-estimated `CurrentRadius` makes it worse.
2. **Wrong rock.** `WorldRenderer.UpdateMiningBeams` (`WorldRenderer.cs:2310`) guesses via
   `NearestHe3Rock` (`:2319/2328/2354-2372`) because the server never says which rock is being mined.

**Fix.**
- **Stream the exact target rock (protocol bump).** The miner already tracks its target rock
  server-side (`MinerState` in `server/Sim/Simulation.Mining.cs`). Add a compact per-miner
  `shipId → rockId` for actively-mining miners — either a small dedicated low-rate message
  (mirroring the other mining side-channels like `MsgRockUpdate=22`) or a conditional field on the
  mining ship's snapshot row. Consume it in `UpdateMiningBeams` to select the precise rock instead
  of `NearestHe3Rock`. Bump the protocol version; wire both encode (server) and decode (client
  `WorldRenderer`/`NetTypes`).
- **Elongate to center.** In `MiningBeam.UpdateBeam` set the **beam** endpoint to `rockCenter`
  (let the cylinder clip into the mesh) while keeping the **debris** emitter at the surface point
  (`rockCenter - dir*rockRadius`) so chips still spawn on the face. This is a 1-line split of the
  existing `impact` into `beamEnd = rockCenter` vs `debrisAt = surface`.
- **Origin from the muzzle.** Optionally pass the ship's mining hardpoint offset as `from` instead
  of `rs.GlobalPosition` (`WorldRenderer.cs:2328`) so the beam leaves the nose, not the pivot.

**Files:** `server/Sim/Simulation.Mining.cs` + wire (new field/message), `shared/` protocol
constant, `client/scripts/NetTypes.cs`, `client/scripts/WorldRenderer.cs` (`UpdateMiningBeams`),
`client/scripts/MiningBeam.cs` (`UpdateBeam`).

---

## Issue 5 — Miner doesn't face the rock center while mining

**Causes** (`server/Sim/Simulation.Mining.cs`, `MinerExecute` Harvesting case `745-763`):
- Slow-hull P-controller: `AutoSteer.FaceAndRoll` (`shared/AutoSteer.cs:314-315`) shrinks the
  corrective stick as alignment improves, so a ~18°/s miner hull carries steady-state pointing
  error and lags.
- **Avoidance fights the target rock.** When drift exceeds `hold + 8` it switches to `Approach`,
  which runs `AutoSteer.ApproachPoint(..., avoid = PigAvoidAsteroids)` (`:679, :759`). The rock it's
  mining is itself an asteroid the avoider pushes *away* from → nose swings off-center.

**Fix.**
- **Exclude the mined rock from avoidance.** While approaching/harvesting its target, pass an
  "ignore this rock id" into the avoidance scan (or zero the avoid weight for the target rock) so
  the miner steers straight at it. Smallest change: add an optional exclude-id to the avoid helper
  used at `Simulation.Mining.cs:679/681-685`.
- **Reduce Approach↔Harvest oscillation.** Widen the hysteresis band (or hold station with a small
  translational correction) so small drift keeps using `FaceAndRoll` on center rather than dropping
  into avoid-deflected `Approach`.
- Optionally bump the miner's turn gain **while harvesting** so `FaceAndRoll` converges tighter
  (determinism-guarded by the PIG/Autopilot suites — keep the change inside the miner branch).

**Files:** `server/Sim/Simulation.Mining.cs` (Harvesting/Approach), possibly a small `AutoSteer`
avoid-exclude parameter in `shared/AutoSteer.cs`.

---

## Issue 6 — Miner flight looks jerky vs. the player ship

**Cause.** The miner is a `RemoteShip` rendered by **delayed snapshot interpolation with no
extrapolation** (`client/scripts/RemoteShip.cs:280-356`); when `renderT` passes the newest sample it
**hard-holds then snaps** (`:351-355`). The player's own ship is a `PredictionController` (predicted
every frame, zero-latency) — a different path. The gap widens when the miner is in a low-rate AOI
tier (interp buffer eases toward the 800 ms cap, `:322`).

**Fix (client smoothing, conservative).**
- Replace the hard hold-latest tail with a **bounded dead-reckoning extrapolation**: continue along
  the last segment's velocity for a short clamped horizon (e.g. ≤1 interp frame) before holding, so
  the hold-then-snap becomes a smooth glide. Scope the horizon small so enemy ships don't rubber-band.
- Complementary server lever: raise **miner AOI priority** so a miner near a player streams at full
  rate (fewer sparse samples to interpolate). Check `server` AOI tiering (`SIM_*_RADIUS/*_EVERY`).
- Note: fixing Issue 5's steering oscillation also reduces jerk **at the source** (smoother
  server-side motion → smoother samples).

**Files:** `client/scripts/RemoteShip.cs` (interp tail), optionally server AOI tier for miners.

---

## Issue 7 — Too many special asteroids; add common Regolith/Ice; map-defined counts

**Today:** `server/Sim/World.cs` `AssignOre` (`388-485`) picks He-3 as ~12% of a sector's rocks
clamped to [2,8] (`:431-452`), then forces **every** remaining rock into Carbonaceous/Silicon/
Uranium via `hash[i] % 3` (`:474`). There is no common tier — `regolith`/`ice` don't exist.

**Fix.**
- **Add common classes to the enum (append-only, wire-safe).** `shared/Defs.cs:268`:
  `enum RockClass : byte { Carbonaceous=0, Silicon=1, Uranium=2, Helium3=3, Regolith=4, Ice=5 }`.
  Mirror on the client (`client/scripts/NetTypes.cs:156` is a raw byte — no enum change needed there,
  but add the names). Add `Regolith`/`Ice` to `RockClassName` (`TargetMarkers.cs:1384-1390`).
- **Rewrite `AssignOre` selection to counts, not fractions.** A sector is common rock by default;
  only a small fixed set of rocks are rare:
  1. **He-3:** a fixed count per sector, **default 4**, overridable per-sector by map YAML
     (replaces the fraction/min/max path).
  2. **Special:** up to **1** rock per sector (default), overridable per-sector by map YAML; it gets
     one of Silicon/Carbonaceous/Uranium (by the per-rock hash). Skip if the sector has too few
     rocks.
  3. **Every other rock in the sector → Regolith or Ice** (common) — this is the overwhelming
     majority, split by the per-rock hash (Ice vs Regolith is cosmetic today). Keep the per-rock
     sub-RNG so **layout is not perturbed** (the "canary").
- **Config knobs.** Replace/extend the `mining:` block in `server/Content/core/world.yaml`
  (`:145-156`) — `he3-per-sector: 4`, `special-per-sector: 1` — carried on `WorldMiningTuning`
  (`shared/Defs.cs:585`) and `schemas/world.schema.json`. Add per-sector overrides on `SectorConfig`
  (`shared/Defs.cs:303-305`, replacing the He3 fraction/min/max fields) with matching entries in
  the **map YAML schema** (`schemas/map.schema.json`) and `MapLoader` (see the `new-map` skill).
- **Determinism / golden:** changing class assignment changes `RockOre` classes, so **regenerate the
  `tests/MiningTest` pinned-seed layout golden**. Verify He-3 offload/credits paths are unaffected
  (only He-3 harvests; Regolith/Ice/specials stay capacity-0 cosmetic, same as the old specials).

**Files:** `shared/Defs.cs` (enum + tuning + SectorConfig), `server/Sim/World.cs` (`AssignOre`),
`server/Content/core/world.yaml`, `schemas/world.schema.json`, `schemas/map.schema.json`,
`server/.../MapLoader.cs`, `client/scripts/TargetMarkers.cs` (`RockClassName`), `tests/MiningTest`
(golden).

---

## Cross-cutting notes

- **Protocol bump** is needed once (Issue 4's rock-id stream). Bump the version constant, keep encode/
  decode symmetric, and smoke the bump with `--autofly` (the dotnet suites don't cover the Godot
  client).
- **Determinism suites** (`MiningTest`, `AutopilotTest`, PIG suites) guard the server steering and
  layout — keep steering changes inside the miner branch and regenerate only the intended golden.
- Reuse existing helpers throughout: `RockLabel`/`RockClassName`, `FriendlyShips`/`EnemyShips`,
  `SwitchView`'s framing expression, `AutoSteer.FaceAndRoll`, the `MsgRockUpdate` side-channel pattern.

## Verification

1. **Build + tests:** `dotnet build`; run `tests/MiningTest`, `tests/AutopilotTest`, PIG suites,
   `tests/FlightModelTest`/`ContentTest` (watch for the known pre-existing content-drift failures,
   not new regressions). Regenerate + re-run the `MiningTest` layout golden.
2. **Rock distribution:** boot the server, `spacetime`-free sim; inspect a sector's assigned classes
   (log or a debug dump) — expect ~4 He-3, ≤1 special, the rest Regolith/Ice. Try a map YAML that
   overrides He-3/special counts and confirm it takes effect.
3. **F3 parity (Issues 1-3):** run the client with `--autofly`. Pre-launch (in hangar) open F3 —
   confirm friendly ships **including the miner**, enemy ships, and rock-type labels now render, and
   that pan feel matches the launched view. Launch and re-open F3 — same target set as the cockpit
   HUD. Use the `verify` skill to capture screenshots (hangar-F3 vs launched-F3 vs cockpit).
4. **Beam (Issue 4):** watch a miner harvest — beam originates at the nose, reaches the **correct**
   rock, and clips into its center; debris still sprays off the surface. Force a case where two He-3
   rocks are close to confirm the streamed id (not the nearest guess) drives the beam.
5. **Facing (Issue 5):** confirm the miner holds nose-on-center while harvesting and no longer swings
   off due to avoidance of its own rock.
6. **Smoothing (Issue 6):** observe the miner near your ship — motion should read smooth (no
   hold-then-snap stutter) against the predicted own-ship.
