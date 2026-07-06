# Larger sectors + edge-filling asteroids, with per-sector configurable size

## Context

The playable area is defined by two hardcoded sectors — Home (`CoreRadius = 2100`) and
Verge (`VergeRadius = 700`) in `server/Sim/World.cs:43-44` — each scaled by a **single global**
`SectorScale` (currently `2.25`, authored in `server/Content/factions/world.yaml`). Asteroid
count already scales by a cube law to hold density roughly constant (`World.cs:164-166`), but
asteroid *placement* is confined to a wide-shallow slab (`±800 / ±200 / ±800 × scale`,
`World.cs:359-361`) and the verge belt to `380 × scale` — both well *inside* the `2100 × scale`
sector radius, so rocks never reach the edges.

We want three things:

1. **A larger play area** shipped by default.
2. **More asteroids at today's density**, spread outward to fill the larger area (reaching the
   sector edge, staying a shallow disc).
3. **Sector size configurable per-sector, per-map** — each sector on a map carries a **nullable
   `radius`**; when null it falls back to the built-in default × global `SectorScale`.

There is no "map" entity today (the `custom maps` commit was a roadmap README only). The
per-server-overridable `world:` YAML block *is* the map: adding a `sectors:` list under it gives
per-sector authoring, and a different server content bundle = a different map. This satisfies
"per-sector per-map" with no new map machinery.

## Design decisions (confirmed with user)

- **Size field = absolute nullable `radius`** (world units) per sector. Null → `defaultBaseRadius ×
  SectorScale` (today's behavior). Global `SectorScale` stays as the fallback multiplier for unset
  sectors and as the density-calibration anchor.
- **Asteroids reach the sector edge but stay a shallow disc** — expand the field's horizontal
  extent to ~`0.9 × sectorRadius`, keep it thin in Y (preserve the "wide and shallow playfield"
  the AI/gameplay assume). *Not* a full 3-D sphere fill.
- **Ship a bigger default now** — set Home radius to `6000` in `world.yaml` (up from ~4725
  effective today), wire up per-sector configurability, and leave Verge null (→ `1575`).

## Changes

### 1. Authored YAML model — add a nullable per-sector radius

`factions/src/Allegiance.Factions/Model/RuntimeData.cs` (`WorldConfig`, ~line 82):

- Add a small record:
  ```csharp
  /// <summary>Per-sector map override. Radius is nullable: null → default × global sector-scale.</summary>
  public record SectorConfig
  {
      public byte Id { get; set; }
      public double? Radius { get; set; }   // absolute world units; null = use built-in default
  }
  ```
- Add to `WorldConfig`: `public List<SectorConfig>? Sectors { get; set; }` (omit-when-null, like
  the other optional world knobs). `HyphenatedNamingConvention` maps `sectors:` / `radius:`
  automatically; an omitted `radius` deserializes to `null`.

No change to `Model/Core.cs` — `Sectors` rides inside the existing `WorldConfig? World`
(`Core.cs:40`), already merged via `??=`.

### 2. Runtime config — carry resolved-but-still-nullable radii

`shared/Defs.cs` (`WorldConfig`, ~line 247):

- Add:
  ```csharp
  public sealed class WorldSectorConfig { public uint Id; public float? Radius; }
  public List<WorldSectorConfig> Sectors = new();
  ```
- **Not streamed.** `Protocol.BuildDefs` (`server/Net/Protocol.cs:957`) does *not* need to write
  the sector list — the client already learns each sector's exact radius from the per-sector
  statics (`WriteSectorStatic`, `Protocol.cs:415-419`, sent in Welcome/reveal). So **no wire
  format change and no protocol version bump.** Only the *values* the client receives change.

### 3. Projection — pass the nullable radius through

`server/Content/FactionsContentProjection.cs` (`ProjectWorld`, ~line 335):

- In the non-null branch, map `w.Sectors` → `Sectors = w.Sectors?.Select(s => new WorldSectorConfig
  { Id = s.Id, Radius = s.Radius.HasValue ? (float)s.Radius.Value : (float?)null }).ToList() ?? new()`.
- Null-world fallback branch: leave `Sectors` an empty list (every sector then resolves to its
  default × scale).

### 4. World generation — resolve per-sector radius + fill outward

`server/Sim/World.cs` ctor (`~line 155-195`) and the two seeders (`~356-401`):

**a. Resolve radii (replaces the hardcoded `coreR`/`vergeR` at 162-169):**
```csharp
float ResolveRadius(uint id, float baseRadius)
{
    foreach (var sc in cfg.Sectors)
        if (sc.Id == id && sc.Radius.HasValue) return sc.Radius.Value;   // absolute override
    return baseRadius * cfg.SectorScale;                                  // null → default × scale
}
float coreR  = ResolveRadius(HomeSector,  CoreRadius);
float vergeR = ResolveRadius(VergeSector, VergeRadius);
Sectors.Add(new Sector(HomeSector,  coreR));
Sectors.Add(new Sector(VergeSector, vergeR));
```
Keep the existing two fixed sectors and their roles (base placement, field vs belt, aleph). We
only make their *size* configurable; unknown ids in the list are ignored (reserved for future).

**b. Count from the actual filled disc, at constant density** (replaces `scale3`/`AsteroidCount ×
scale3` at 164-166 and the fixed slab in `SeedAsteroidField`). Introduce disc knobs and drive
count off the disc's own dimensions so density (spacing) is invariant to sector size:
```csharp
const float FieldFillFrac = 0.9f;    // horizontal reach as fraction of sector radius (fill to ~edge)
const float FieldFlatten  = 0.11f;   // vertical half-extent as fraction of horizontal (shallow)
// RockVolDensity calibrated so today's slab (hXZ≈1800, hY≈450) reproduces the historical ~46 rocks:
//   46 / (1800 * 450 * 1800) ≈ 3.16e-8  → same spacing at any disc size.
const float RockVolDensity = 3.16e-8f;
```
`SeedAsteroidField(sector, radius, density, ...)` computes:
```csharp
float hXZ = radius * FieldFillFrac;
float hY  = hXZ * FieldFlatten;                         // shallow: hY≈0.1·hXZ (≈ today's absolute thinness)
int count = (int)MathF.Round(density * RockVolDensity * hXZ * hY * hXZ);
```
and distributes rocks across `±hXZ` in X/Z (fill outward — either broaden the existing sheared
bands to span `±hXZ`, or a simpler radial disc draw out to `hXZ`) and `±hY` in Y. This keeps the
disc character but reaches the edge. Result: at Home `radius = 6000`, `hXZ≈5400`, `hY≈594` →
**~550 rocks** (vs ~46 today) — "more asteroids, same density, filled to the edge."

**c. Verge belt reaches outward** (`SeedAsteroidBelt`, ~388): replace the fixed
`VergeBeltRadius (380) × scale` mean with a fraction of the actual `vergeR` (e.g. mean ≈
`0.75 × vergeR`, band half-width ≈ `0.2 × vergeR`) so the belt spreads from mid-sector toward the
edge; scale the belt count with `vergeR` at constant ring density (e.g. proportional to belt
circumference/area) so spacing holds.

**Determinism:** keep all draws on `DetRng` in a fixed order so a given seed reproduces the same
map (needed for reconnect/world-rebuild). Changing counts/positions changes the *generated map*
(intended); clients rebuild rocks from streamed statics and never re-seed, so there is no
client/server drift. `GridCell = 160f` is unchanged — the per-sector rock grid stays sparse and
all hot-path queries (`RockGrid`/`CellOf`) remain bounded by cell occupancy, not total count.

### 5. Ship the bigger default

`server/Content/factions/world.yaml` — add under `world:` (keep `sector-scale: 2.25` as the
fallback/anchor):
```yaml
  sectors:
    - id: 0        # home — explicit larger arena
      radius: 6000
    - id: 1        # verge — null → 700 * sector-scale (1575)
      radius: null
```
Mirror the same block in the doc copy `.PLAN/tech-tree-stock.yaml` for parity (reference only; the
loaded bundle is `server/Content/factions/`).

## What does NOT change

- **Client:** nothing. `WorldRenderer`/`Hud`/`SectorOverview`/lobby preview all size off the
  streamed per-sector radius (`ViewSectorRadius`/`LocalSectorRadius`) and consume rocks from
  Welcome/Reveal — they adapt automatically to the larger radii and rock counts.
- **Protocol / wire format:** unchanged (sector statics already stream radius; rock statics
  already stream positions). No version bump.
- **Boundary erosion, PIG patrol clamp, missile expiry:** all read `SectorRadius(...)` — they
  scale with the new radii automatically.
- **`World` ctor signature:** unchanged (`new World(seed, content.World, ...)`), so all 8 call
  sites (`server/Program.cs:126`, `SelfTest.cs`, and the `tests/*`) compile untouched.

## Considerations / call-outs

- **Cube-law cost of very large radii:** rock count grows ∝ `radius³` (both disc extents scale
  with radius). `6000` → ~550 rocks is comfortable; pushing radius to ~2.5× (≈11 800) would mean
  ~4000 rocks — playable (grid handles broad-phase; one-time Welcome send grows) but heavy. The
  `radius` value is the single knob; note this when tuning further. If desired, cap growth by
  making `hY` an absolute thickness (count ∝ `radius²`) instead of proportional.
- **AOI awareness range is absolute** (`ClientHub` `FullRateRadius=600`, `MidRateRadius=1500`,
  env-tunable `SIM_*_RADIUS`). It does *not* grow with sector size, so in a much larger sector
  distant ships stay at lower update tiers. Defaults are left unchanged to avoid a perf
  regression; operators of very large maps may raise the env knobs. No code change here.
- **Base spawn rings** (`RandomBasePos`, hardcoded 600–1200 / 200–500) are not radius-scaled, so
  bases sit more centrally in a bigger sector — acceptable (arguably good). Optional follow-up:
  scale these by `radius` if bases should track outward.

## Implementation delegation

- **Sonnet** (mechanical): steps 1–3 and 5 — the YAML model record + `WorldConfig.Sectors`
  (`RuntimeData.cs`), the `shared/Defs.cs` `WorldSectorConfig` list, the `ProjectWorld`
  passthrough (`FactionsContentProjection.cs`), and the `world.yaml` / `.PLAN` edits. These are
  additive plumbing with a clear pattern to mirror.
- **Opus** (careful): step 4's `World.cs` seeding math — radius resolution, the density-calibrated
  edge-filling disc, and the verge-belt outward spread — plus reconciling test expectations.

## Verification

1. **Build + tests:** `dotnet build` then run the sim test suites — `tests/FogTest`,
   `tests/MissileTest`, `tests/MineTest`, `tests/ShieldTest`, `tests/StrategyTest`,
   `tests/RescueTest`. They assert *relative* counts (`Sectors.Count`, `Asteroids.Count`) against
   the generated world, so they should pass unchanged. Watch FogTest's reveal-streaming
   assertions: Home at ~550 rocks stays under `RevealMaxRocks = 512`? — if a single sector's rocks
   exceed 512 they stream across multiple frames; confirm the reveal-progress asserts still hold
   (they check `nextSector >= 1`, not a single-frame drain).
2. **Server self-test:** `server` with `--selftest` / `--pregen-assets` to confirm world-gen and
   asset loading succeed at the new scale.
3. **Headless sim smoke:** launch `--server`, connect `--anonymous --autofly` (hold the connection
   so the sim ticks per the headless-sim note), confirm the world builds, ships fly, and rocks
   populate out to the edge without errors.
4. **Visual check in the Godot client:** connect to the local server, open the sector overview
   (minimap/boundary ring should render the larger radius) and fly toward the boundary — confirm
   asteroids now extend outward toward the edge as a shallow disc, and the out-of-bounds erosion
   warning triggers at the new radius. Toggle a `radius: null` sector to confirm it falls back to
   `default × sector-scale`.
5. **Per-map override sanity:** temporarily set `id: 1` `radius: 3000` in `world.yaml`, restart the
   server, and confirm the Verge sector renders at 3000 and its belt fills outward — proving the
   nullable per-sector attribute is honored.
