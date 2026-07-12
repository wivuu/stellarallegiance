# Mining config cleanup + generalized client motion smoothing

## Context

Four issues raised against the `mining` branch:

1. **`he3-fraction` is a dead knob.** `AssignOre` computes `round(he3-fraction × n)` then clamps into `[he3-per-sector-min, he3-per-sector-max]` — stock pins min==max==4, so the fraction never affects anything. Decision (user-confirmed): **remove it** (and the per-map `he3-fraction-mult`); He3 counts become purely count-driven.
2. **`special-per-sector` is a seeding-time concern, not a mining mechanic.** Decision: move ALL rock-class seeding knobs from the `mining:` block to the existing `seeding:` block (which already owns asteroid field shape and base placement), including the runtime fields (`WorldMiningTuning` → `WorldSeedingTuning`).
3. **Home (garrison) sectors currently get a special rock like any sector.** Add a `home-special-chance` knob (float, **default 0** — no specials in home sectors); a deterministic per-sector roll gates whether a garrison sector receives its `special-per-sector` specials. Map-authored `special-count` always wins.
4. **Miner drones look jerky on the client.** Root cause confirmed: miners share the remote-ship snapshot stream, and beyond `SIM_FULLRATE_RADIUS`=600u they fall to the coarse AOI tier (~500 ms between updates); the client extrapolates only 120 ms by finite-differencing positions, ignoring the velocity + angular velocity already on the wire (Protocol.cs:174-188). Fix both ends, and (user-confirmed) generalize the client smoothing into a reusable component for any server-controlled entity.

No wire/protocol changes anywhere — all moved knobs are server-side-only, and the client consumes fields already streamed. No proto bump.

## Verified anchors

- `World.AssignOre` — server/Sim/World.cs:408-536; He3 count at :459-466, special count at :485-489. Knob readers are confined to here + the MapLoader home stamp.
- Home-He3 stamp seam — server/Content/MapLoader.cs:218-243 (`ApplyTo`); `MapLoader` has **no seed access**, so the home-special roll must live in `AssignOre` (WorldSectorConfig.Garrison is available there via `sc`).
- Rock ids are monotonic from 1 and overlap sector ids in the `OreMix(Seed, id)` domain → the per-sector home-special roll needs a **salted** seed to stay disjoint from per-rock sub-RNGs.
- Client smoothing engine — client/scripts/RemoteShip.cs (constants :22-73, `Push` :226-284, `_Process`/extrapolation :286-376, `SafeRot` :210-222). Fed by WorldRenderer.cs:1858 `OnAuthoritative(row, ServerTick)`.
- AOI tiering — server/Net/ClientHub.cs knobs :21-57, tick gating :923-924, `BuildSnapshotFor` :1606-1710; mid tier (:1668-1683) already iterates the viewer's sector grid with a `d2 <= r2sq` filter; radar-seen full-rate exemption pattern at :1633-1648. `ShipSim.IsMiner` exists.
- JSON schemas are **generated**: `dotnet run --project server/SimServer.csproj -- --gen-schemas` → `schemas/world.schema.json` + `schemas/map.schema.json`. Regenerate, never hand-edit.
- No stock map authors `he3-min/he3-max/he3-fraction-mult` — reshaping the per-sector override is drift-free for shipped content.

## Step 1 — Config reshape + home-special-chance (server, one atomic commit)

The repo auto-commits mid-session: keep all of Step 1 (including test updates) in one buildable unit — MiningTest 7d's drift guard asserts the runtime fields directly and goes red otherwise.

### Knob/field moves (old → new)

| Old (`mining:` / `WorldMiningTuning`) | New (`seeding:` / `WorldSeedingTuning`) |
|---|---|
| `he3-fraction` / `He3Fraction` | **deleted** |
| `he3-per-sector-min/-max` / `He3PerSectorMin/Max` | `he3-per-sector` / `He3PerSector = 4` (single count) |
| `he3-per-home-sector` / `He3PerHomeSector` | same names, moved; `= 2` |
| `special-per-sector` / `SpecialPerSector` | same names, moved; `= 1` |
| `special-rock-radius-mult` / `SpecialRockRadiusMult` | same names, moved; `= 3f` |
| — | **new** `home-special-chance` / `HomeSpecialChance = 0f` |

`mining:` keeps mechanics/economy: `max-miners-per-team`, `harvest-rate-per-second`, `credits-per-ore-unit`, `offload-delay-seconds`, `ore-capacity-min/max`, `shrink-floor-frac`, `miner-standoff`.

Per-sector: `WorldSectorConfig.He3Min/He3Max/He3FractionMult` (shared/Defs.cs:310-314) → single `int? He3Count`; map YAML `he3-min/he3-max/he3-fraction-mult` (`MapSectorDef`, MapLoader.cs:56-69) → `he3-count`. `special-count` / `ore-richness-mult` unchanged.

### Edits

1. **shared/Defs.cs** — move/add fields per table above (copy over the "initializers must match stock world.yaml" doc-comment invariant to `WorldSeedingTuning`); reshape `WorldSectorConfig`.
2. **server/Content/WorldLoader.cs** — `WorldSeedingDef` +5 nullable props (XML docs feed the generated schema), `WorldMiningDef` −6; move the `Project()` mappings.
3. **server/Content/MapLoader.cs** — `MapSectorDef.He3Count`; home stamp becomes:
   `int? he3 = s.He3Count; if (garrison is not null && he3 is null) he3 = world.Seeding.He3PerHomeSector;` → `He3Count = he3`.
4. **server/Sim/World.cs `AssignOre`** — count rule simplifies to
   `int he3Count = Math.Clamp(sc?.He3Count ?? _seed.He3PerSector, 0, n);`
   and the special block (replacing :485) gains the home gate:
   ```csharp
   int specialCount = sc?.SpecialCount ?? _seed.SpecialPerSector;
   if (sc?.SpecialCount is null && sc?.Garrison is not null && _seed.HomeSpecialChance < 1f)
   {
       // Salted per-sector sub-RNG: sector ids overlap the rock-id space, so the raw
       // OreMix(Seed, sectorId) stream could collide with a rock's own sub-RNG.
       var hr = new DetRng(OreMix(Seed ^ 0x484F4D45_53504543UL, sectorId));
       if (hr.NextDouble() >= _seed.HomeSpecialChance)
           specialCount = 0;
   }
   specialCount = Math.Clamp(specialCount, 0, Math.Max(0, n - he3Count));
   ```
   Shared world-gen RNG stream untouched (test-2 canary). Update the header comments (:404-407, :454-458, :482-484).
5. **server/Content/core/world.yaml** — move/rename knobs, add `home-special-chance: 0.0`, rewrite the `seeding:`/`mining:` comment blocks (~lines 120-160) to reflect the new split.
6. **tests/MiningTest/Program.cs** — see below.
7. **Regenerate schemas**: `dotnet run --project server/SimServer.csproj -- --gen-schemas`; commit the diff. Note in the map schema description that `he3-count` replaces `he3-min/he3-max/he3-fraction-mult`.

### MiningTest changes

- Delete `ExpectedHe3` helper (assertions become `Math.Min(count, n)`).
- `FieldConfig`: drop `he3FractionMult/he3Min/he3Max` params → `int? he3Count = null` + optional `WorldSeedingTuning` (default stock). ⚠ `FieldConfig`'s single sector is a **garrison** — after `home-special-chance: 0` lands, single-sector special tests must author `special-count` explicitly (bypasses the roll) or set `HomeSpecialChance = 1f`.
- Test 2 (canary) — retarget: `off` = seeding `{He3PerSector=0, SpecialPerSector=0}`, `loud` = `{He3PerSector=9999, SpecialPerSector=5, HomeSpecialChance=1f}` + loud ore-capacity; now also pins that the home roll never perturbs layout.
- Test 3 — expected `Math.Min(4, n)`. Test 4 — per-sector `he3Count: 5` beats world `He3PerSector = 1`. **Test 5 (FractionMult) — delete.**
- Tests 6/8-28 — mechanical: `He3Fraction/Min/Max` args → a seeding instance with `He3PerSector`; they only used min/max to pin He3 density.
- Test 7b/7c — author `special-count` explicitly where a garrison sector must have specials.
- Test 7d — map sector 2 authors `He3Count = 5`; drift guard → `Seeding.He3PerHomeSector==2 && Seeding.He3PerSector==4 && Seeding.HomeSpecialChance==0f`.
- **New test 7e (home-special-chance)** via real `MapLoader.ApplyTo` (7d pattern): (a) chance 0 → garrison sectors 0 specials, contested sector keeps 1; (b) chance 1 → garrisons get specials, layout otherwise identical; (c) authored `special-count: 2` on a garrison wins at chance 0; (d) two same-seed builds at chance 0.5 are identical (determinism).

## Step 2 — Client: reusable `MotionInterpolator` + RemoteShip refactor

**New file `client/scripts/MotionInterpolator.cs`** (plain class, not a Node — flat dir matches convention). Owns everything RemoteShip's smoothing does today, plus velocity-aware math:

- API: `Tunables` struct (`FloorDelayMs=100, MaxDelayMs=800, GapDelayFactor=1.5, GapEmaAlpha=0.3, ClockOffsetAlpha=0.05, MaxExtrapolateMs=250, ErrorDecayRate≈10/s, SnapDistance≈100u`, `Default` preset); `bool Push(uint serverTick, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVelLocal, bool hasVel, double nowWallMs)`; `void Evaluate(double nowWallMs, out Vector3 pos, out Quaternion rot)`; `Reset()`; `HasSamples`; `LatestVelocity`.
- Move in verbatim from RemoteShip.cs: server-tick→ms timestamping, out-of-order rejection, clock-offset EMA, gap EMA (with >4s outlier reject), adaptive delay `clamp(gapEma·1.5, floor, max)`, `IsFinite` corrupt-sample guards, `SafeRot`.
- **Between bracketing samples**: cubic Hermite on position using wire velocities as tangents (`h00/h10/h01/h11`, tangents scaled by segment seconds), each tangent magnitude-clamped to `3·|Δpos|` to guard f16-quantization overshoot on near-stationary ships; fall back to Lerp when a sample lacks velocity. Rotation stays Slerp. At full-rate 50 ms gaps Hermite degrades to near-linear — players/PIGs unchanged in feel.
- **Past the newest sample**: velocity-based dead-reckon `last.Pos + last.Vel·over` (bounded by `MaxExtrapolateMs=250`); rotation advanced by the ship-LOCAL angular velocity (right-composed, matching FlightModel convention), magnitude-clamped. Finite-difference remains the no-vel fallback.
- **Error-correction blending (no snap)**: on `Push`, re-evaluate the raw curve at the last rendered time; store pos/rot deltas; `Evaluate` adds them and decays exponentially (`ErrorDecayRate`) toward zero; beyond `SnapDistance` snap and clear. This kills the extrapolation-then-correction stutter.
- Entity-agnostic on purpose: `MissileView` (WorldRenderer.cs:1147, pos+vel) can adopt it later — wiring missiles is **out of scope**.

**client/scripts/RemoteShip.cs** becomes a consumer: delete the sample buffer, smoothing constants, `SafeRot`, and the interp/extrapolate bodies of `Push`/`_Process`; keep Health/Shield/IsMining latest-value assigns, `_velTarget`+`Velocity` EMA (**lead-reticle behavior preserved unchanged**), nameplate/engine-glow/mining-roll visuals. Feed `_interp.Push(serverTick, pos, rot, vel, new Vector3(row.AngVelX, row.AngVelY, row.AngVelZ), true, Time.GetTicksMsec())`; apply `_interp.Evaluate(...)` each frame. No changes to WorldRenderer/GameNetClient call sites.

## Step 3 — Server: miner mid-rate exemption

server/Net/ClientHub.cs, mid tier of `BuildSnapshotFor` (~:1680): relax the distance gate —
```csharp
if (d2 <= r2sq || (MinerMidRate && ships[i].IsMiner))
    picks.Add((d2, i));
```
plus one env knob beside the others (:29-33): `SIM_MINER_MIDRATE` (default on). Miners in the viewer's sector then update every `MidEveryTicks` (3 ticks ≈ 160 ms) instead of ~500 ms; inside 600u they stay full-rate; cross-sector miners stay coarse (handled by the new extrapolation). No new tick stripe (`SIM_MINER_EVERY` rejected). Bandwidth: ≤8 miners × ~56 B × ~6.7 Hz — negligible.

## Verification

Server:
```
dotnet build wivuullegiance.slnx
dotnet run --project tests/MiningTest/MiningTest.csproj -c Release     # ALL MINING TESTS PASSED
dotnet run --project tests/ContentTest/ContentTest.csproj -c Release   # world.yaml reshape (note 6 pre-existing content-drift failures on master)
dotnet run --project tests/StrategyTest/StrategyTest.csproj -c Release # miner brains still find He3
dotnet run --project server/SimServer.csproj -- --gen-schemas && git diff schemas/
```
Client (no dotnet coverage — use the `verify` skill, headless server + `--autofly` client):
1. Miner beyond 600u in the local sector moves smoothly (movie; pre-change it stepped at ~500 ms).
2. Player/PIG dogfight motion unchanged (Hermite degrades to linear at full rate).
3. Home sector shows no oversized special rock; adjacent contested sector shows one.
4. Sweep `ErrorDecayRate`/`SnapDistance` if fast enemies rubber-band.

## Risks

- **Determinism**: the salted sub-RNG for the home roll is mandatory (rock ids overlap sector ids in the OreMix domain); pinned by test 2 + new 7e(d).
- **Atomicity**: Defs field moves + MiningTest drift-guard rewrite must land together (auto-commit hook).
- **Third-party maps** authoring `he3-min/he3-max` break on strict YAML parse — acceptable pre-release; schema description notes the replacement.
- **Stock-value invariant**: `WorldSeedingTuning` initializers must match the new world.yaml values (same documented invariant `WorldMiningTuning` carries today).
