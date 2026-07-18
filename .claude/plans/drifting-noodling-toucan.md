# Minimum spawn distances: asteroid↔asteroid and asteroid↔base

## Context

During initial world generation, asteroid positions are pure random draws with **no spacing checks anywhere**: `SeedAsteroidField` / `SeedAsteroidBelt` (`server/Sim/World.cs:1059-1106`) just append rocks, and the garrison placement band (0.14–0.30 of sector radius, `World.cs:333-343`) sits fully inside the field disc (fills to 0.9) — so rocks can spawn overlapping each other and directly on top of bases. This adds two tunable minimum distances, enforced at seeding time: rock↔rock and rock↔base.

Verified constraints:
- Bases are placed **before** asteroids in the `World` ctor (from a separate salted `baseRng`), so their positions are available to the asteroid loops. Gates are placed after — out of scope (not requested).
- The client does **not** re-derive layout from seed (rocks arrive over the network); server-only change.
- The MiningTest layout canary (`tests/MiningTest/Program.cs:145-199`) varies only ORE knobs (He3PerSector/SpecialPerSector/HomeSpecialChance) between its two worlds — both worlds run the same new placement logic, so byte-identical comparison still holds. He3 guarantee test already clamps to actual rock count (`expected = Math.Min(want, n)`), so dropping rocks is safe. Nothing pins absolute rock positions.
- At seeding time `_baseModels` isn't loaded yet (`LoadBaseModels()` runs at `World.cs:435`), so use the flat `CollisionConfig.BaseRadius` (90f) — which is exactly what `BaseRadiusOf` would fall back to, and all seed-time bases are garrisons (type 0) anyway.

## Design

- **Semantics: surface-to-surface.** Reject candidate when `dist(centers) < rA + rB + gap`. Rock radii span 6–55 and BaseRadius is 90, so center-to-center can't express "no interpenetration" without being hugely conservative. Use the rock's visual spawn `Radius` (not ×`AsteroidCollisionScale`) so rocks never visibly touch. Accepted limitation (document in a code comment): `SpecialRockRadiusMult` (×3) is applied post-seeding in `AssignOre`, so the ≤1-per-sector oversized special may still brush a neighbor.
- **Algorithm: rejection sampling, `MaxPlaceAttempts = 12`, then DROP the rock.** Accept-anyway would reintroduce overlap exactly in the crowded spots. Drop rate at stock density is negligible (~470 u mean spacing vs ~118 u worst-case exclusion diameter; largest map sector ≈ 256 field rocks). Fixed loop consuming RNG draws is seed-deterministic.
- **Efficiency:** per-`Seed*`-call local spatial hash (mirrors the existing `_rockGrid` pattern, `World.cs:413-421`), cell = `2*rockMax + RockMinGap`, check 3×3×3 neighbor cells — guards against modded densities ballooning counts (known cube-law hazard). Base check: linear scan of `Bases` filtered by sector (1–2 per sector).
- **Defaults are opinionated non-zero** (the request is to *set* minimum distances); `0` disables either check for map/mod authors.

## Changes

### 1. `shared/Defs.cs` — `WorldSeedingTuning` (~line 674, after `BaseYJitter`)
```csharp
public float RockMinGap = 8f;      // min surface-to-surface gap between two rocks (0 = allow overlap)
public float BaseClearance = 250f; // min gap between a rock's surface and a base's collision sphere (0 = off)
```
(Initializers ARE the stock values — must match world.yaml.)

### 2. `server/Content/WorldLoader.cs`
- `WorldSeedingDef` (~line 380): add `public double? RockMinGap { get; set; }` and `public double? BaseClearance { get; set; }` with XML docs.
- `ApplyTo` seeding block (~line 614): `t.RockMinGap = F(se.RockMinGap, t.RockMinGap);` `t.BaseClearance = F(se.BaseClearance, t.BaseClearance);`

### 3. `server/Content/core/world.yaml` — `seeding:` block (after `base-y-jitter`, ~line 143)
```yaml
rock-min-gap: 8      # min surface-to-surface gap between any two rocks (0 = allow overlap)
base-clearance: 250  # min gap between a rock's surface and a base's collision sphere (0 = off)
```

### 4. `server/Sim/World.cs` — rework loop bodies of `SeedAsteroidField` (1067-1077) and `SeedAsteroidBelt` (1094-1105)

Shared private helper + per-call grid; per rock:
```csharp
// size FIRST (the fit check needs it), then rejection-sample the position
float radius = RockRadius(ref rng, min, max, _seed.RockSizeSkew);
Vec3 pos = default; bool ok = false;
for (int attempt = 0; attempt < MaxPlaceAttempts && !ok; attempt++)
{
    /* existing 3-draw position sample (ang, rr, py) for this shape */
    ok = RockFits(pos, radius, sector, grid, placed);
}
if (!ok) continue; // drop — deterministic per seed
var (variant, rx, ry, rz) = NextShape(ref rng);
Asteroids.Add(new Rock(id++, sector, pos, radius, variant, rx, ry, rz));
/* add (pos, radius) to local grid */
```
`RockFits`: (a) if `BaseClearance > 0`: reject when `dist < CollisionConfig.BaseRadius + radius + BaseClearance` vs each same-sector base; (b) if `RockMinGap > 0`: reject when `dist < radius + rOther + RockMinGap` vs placed rocks in the 27 neighbor cells.

Note: the draw-order change (size before position) + retry draws legitimately shift layouts for a pinned seed; gates (drawn from the same shared rng afterwards) shift too. Nothing pins these; the canary compares same-logic worlds.

### 5. Regenerate schema
`schemas/world.schema.json` is generated (`additionalProperties: false`): run `dotnet run --project server -- --gen-schemas`.

### 6. Tests — `tests/MiningTest/Program.cs` (new block after the He3-guarantee test)
For a couple of seeds with stock config, assert:
- every same-sector rock pair satisfies `dist ≥ rA + rB + RockMinGap − 1e-3` (O(n²) in-test is fine at ~300 rocks/sector);
- every rock clears every same-sector base by `BaseRadius + radius + BaseClearance − 1e-3`;
- `Asteroids.Count > 0` per seeded sector (drops didn't nuke the field).
Plus a disabled-knobs world (`RockMinGap=0, BaseClearance=0`) asserting rock count equals the analytic `round(density·areaDensity·area)` sum (no drops when disabled).

## Verification

1. `dotnet run -c Release --project tests/MiningTest` — canary, seed determinism, He3 guarantees, new spacing assertions.
2. `dotnet run -c Release --project tests/FogTest` — seed-layout / dust-independence checks.
3. `tests/ContentTest`, `tests/StrategyTest`, `tests/CollisionTest` — content round-trip; nothing else pins layout. (Known pre-existing content-drift failures in ShieldTest/ContentTest/FactionsTest per baseline memory.)
4. `dotnet run --project server -- --selftest` — headless boot sanity on the stock map.
5. Dev-only spot check: boot the stock maps and log per-sector placed/dropped counts once to confirm ~0 drop rate at stock knobs (remove before finishing).
