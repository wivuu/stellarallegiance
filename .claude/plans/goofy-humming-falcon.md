# Plan: Data-driven sectors — remove hardcoded core/verge, high-level "feel" knobs

## Context

A map author copied Verge's entire `environment` block onto Delamere and the two sectors still
looked completely different (Verge = thick fog, Delamere = 4 sparse poofs). Root cause: sector
geometry is **hardcoded by sector id** in the `World` constructor, not driven by the map YAML.
Specifically `radius: null` resolves through *per-sector-id* defaults — `CoreRadius=2100` for the
home sector, `VergeRadius=700` for verge (`server/Sim/World.cs:41-44`) — so identical YAML produces a
3× size difference, and the dust knobs are absolute distances that can't adapt to sector size.

Goal: make the map YAML the single source of truth for sector topology, with **one shared set of
defaults** (no `core`/`verge` special-casing) and **high-level "feel" knobs** instead of granular
controls. Teams become data-driven: each sector may host a **garrison** (a team base); the set of
garrisons determines how many teams the map supports. Dust becomes a **seed + a couple of feel
knobs**, evaluated relative to sector size so an identical block reads identically in any sector.

Exploration confirmed the hard part is small: the two-sector/role hardcoding is concentrated in the
`World` constructor (`World.cs:207-244`) plus four constants. Everything downstream — Protocol,
LobbyStatus, MapCatalog, fog-of-war (`Simulation.Vision.cs`), PIG AI, warp (`TryWarp`), and the
entire Godot client — already loops over `World.Sectors`/`Bases`/`Alephs` and is N-sector-clean.
team→sector already resolves dynamically by scanning `World.Bases` (`Simulation.cs:1010-1016`).

## Design

**Principle:** map YAML declares the sectors; the `World` ctor iterates that list; anything a sector
omits falls back to one shared default. No value is chosen by sector id.

### Target map YAML (high-level feel)

```yaml
name: Brimstone Gambit
sector-radius: 1575          # single default radius for any sector that omits `radius`
links:                       # gate topology = EDGES (which sectors connect); omitted → ring by id
  - [0, 1]
sectors:
  - id: 0
    name: Delamere
    map-pos: [-1.0, 0.0]     # 2D LAYOUT coord for the map diagram (minimap + lobby preview),
                             # normalized ~[-1,1]. Distinct from 3D geometry. Enables star/custom
                             # shapes. Omitted → client falls back to the auto ring layout.
    radius: 4725             # optional absolute; omit → map `sector-radius` (× world sector-scale)
    garrison: { team: 0 }    # optional; this sector is team 0's home base. #garrisons ⇒ #teams
    asteroids: field         # field | belt | none   (default: field)
    dust:
      amount: 0.6            # 0..1 "how dusty" — drives coverage/count/thickness/vision internally
      color: [0.22, 0.14, 0.12]
      seed: 1234             # optional; omit → derived from match seed ^ sector id
    sun:    { azimuth: 40, elevation: 22, color: [1.0,0.86,0.62], energy: 1.35, god-rays: 0.45 }
    nebula: { color-a: [...], color-b: [...], intensity: 0.08 }
```

**Layout vs connectivity are separate concerns.** `map-pos` is *where a sector node is drawn* in the
2D map diagram; `links` is *which sectors are gate-connected* (the edges, and the real aleph travel).
A star = a center sector at `[0,0]` with spokes at ring positions, plus `links` from the center to
each spoke.

Knob reduction: `dust:` goes from 9 fields (`cloud-count/radius-min/radius-max/coverage-frac/
flatten/density/color/vision-mult/seed`) to **3** (`amount`, `color`, `seed?`). `belt:` (5 granular
fields) collapses to `asteroids: field|belt|none` (+ optional `density: 0..1`). `sun`/`nebula` are
already feel-level and stay as-is.

### Dust: amount + seed, sector-relative (fixes the original bug)

Rewrite `World.SeedDustClouds` (`World.cs:463-480`) to derive everything from `amount`, the sector
radius, and the seed — so coverage is intrinsic, not absolute:
- cloud radius = `lerp(0.15, 0.5, amount) × sectorRadius` (± ~30% per-cloud jitter)
- cloud count ≈ tile the covered disc: `k · (coverageR / cloudR)²`, clamped to a perf cap
  (~120 clouds; at the cap, radius grows to keep coverage so huge sectors use fewer/bigger clouds)
- per-puff density = `lerp(0.3, 0.7, amount)`; coverage-frac ≈ 0.9; flatten ≈ 0.15 (fixed feel)
- **vision-mult = `lerp(1.0, 0.15, amount)`** — dustier intrinsically blocks more radar/vision
  (folds the old server-only `vision-mult` into the single feel knob)
- positions from `seed` (YAML) or `matchSeed ^ sectorId` when omitted

The server still **bakes concrete clouds** and streams them + color exactly as today
(`Protocol.WriteSectorEnv:464-485`), so **no wire/client change for dust**.

### Garrisons drive teams

The map declares garrisons per sector; `World` builds `Bases`/`TeamStates` from them and the team
count = distinct garrison teams. This replaces the hardcoded `Bases.Add(... team 0, HomeSector ...)`
/ `(... team 1, VergeSector ...)` at `World.cs:215-221`. Base placement uses one shared default
range (relative to sector radius), not the `(600,1200)`/`(200,500)` core/verge split.

### World ctor rewrite (`World.cs:173-244`)

Replace the two-sector body with a loop over `cfg.Sectors`:
1. resolve radius (explicit `Radius` → else `cfg.SectorRadius × sectorScale`, one default for all)
2. `Sectors.Add(new Sector(id, radius, name, env))`
3. if the sector has a garrison → `Bases.Add` + `TeamStates[team] = new TeamState()`
4. seed asteroids by declared `asteroids` kind (`field`→`SeedAsteroidField`, `belt`→
   `SeedAsteroidBelt`, `none`→skip) using one shared default knob-set
5. seed dust from `amount`+seed+radius
Then build gates from `cfg.Links` (or the default ring) via the existing `Gate` pairs
(`World.cs:235-239` logic, generalized to the link list).

Delete `CoreRadius`, `VergeRadius`, and the `HomeSector`/`VergeSector` *as-role* constants. Add a
single `DefaultSectorRadius` fallback constant and shared base-placement/asteroid defaults.

### 2D map layout (`map-pos`) — the one place that DOES touch the wire + client

Today both 2D renderers invent positions: the in-game minimap lays sectors on an auto ring
(`Minimap.cs:99-114`), and the lobby preview gives each sector a horizontal slot ("Sectors don't
share a coordinate space" — `SectorMapPreview.cs:68-81`). To support authored star/custom shapes,
carry a normalized `map-pos: [x,y]` per sector and stream it on **both** map-diagram paths:
- In-game: add `map-pos` to `WriteSectorStatic` (`Protocol.cs:420`) + `ReadSectorStatic`
  (`GameNetClient.cs:1023`); `Minimap.cs` uses it (normalized → panel coords) instead of the ring.
- Lobby preview: add x,y to `MapCatalogSector` (`MapCatalog.cs:11`) + the lobby-catalog wire
  (`Protocol.cs:~591` / client parse `GameNetClient.cs:~1437`) + `SectorMapPreview.SectorModel`
  (`SectorMapPreview.cs:16`); the preview positions nodes by `map-pos` instead of horizontal slots.
- Both renderers keep their current auto-layout as the fallback when `map-pos` is absent, so
  existing maps and older servers still render.

This is additive to the sector-static and lobby-catalog frames — bump the proto version and mirror
the exact field order on the client (per the repo's wire conventions).

### Default / "home" sector for the two runtime fallbacks + client

- `Simulation.cs:1009` (`PlaceAtBase` pre-scan default) and `ClientHub.cs:949` (spectator AOI
  anchor): replace `World.HomeSector` with the player's team garrison sector where known, else
  `World.Sectors[0].Id` (a `World.DefaultSector` helper). Neither needs a magic constant.
- Client (`WorldRenderer.cs:287`, `Starscape.cs:14`): today `HomeSector=0` drives only the
  pre-spawn / post-death overview. Change it to **the local team's garrison sector** — the client
  already receives every base with `Team`+`SectorId`, so look up "my team's base sector" and fall
  back to `Sectors[0]` / lowest id. Small, and matches "home = your garrison."

### Files to modify

- `server/Sim/World.cs` — ctor rewrite, constants, `SeedDustClouds`, gate-from-links, `DefaultSector`.
- `shared/Defs.cs` — `WorldConfig`: add `SectorRadius`, `Links`. `WorldSectorConfig`: add
  `Garrison{Team}`, `AsteroidKind`, `MapPos` (x,y). Collapse `SectorDust` to `{ Amount, Color, Seed }`.
- `server/Content/MapLoader.cs` — `MapDef`/`MapSectorDef` new fields (`SectorRadius`, `Links`,
  `Garrison`, `Asteroids`, `MapPos`, simplified `DustDef`); `ProjectEnv`/`ApplyTo` projection.
- `server/Sim/Simulation.cs:1009`, `server/Net/ClientHub.cs:949` — default-sector fallback.
- `client/scripts/WorldRenderer.cs`, `client/scripts/Starscape.cs` — home = local team garrison.
- **2D layout wire + renderers** (see section above): `server/Net/Protocol.cs`
  (`WriteSectorStatic` + lobby-catalog frame, proto bump), `server/Content/MapCatalog.cs`
  (`MapCatalogSector` +x,y), `client/scripts/GameNetClient.cs` (`ReadSectorStatic` + lobby parse),
  `client/scripts/Minimap.cs`, `client/scripts/ui/SectorMapPreview.cs`.
- `server/Content/maps/brimstone-gambit.yaml` — rewrite to the new schema, preserving today's look:
  Delamere (radius 4725, garrison team 0, field, dust amount≈0.35, `map-pos [-1,0]`) + Verge
  (radius 1575, garrison team 1, belt, dust amount≈0.9, `map-pos [1,0]`), `links: [[0,1]]`. Update
  the stale/wrong per-sector comments.
- `MapCatalog.cs:63` — `sectorLabel = Sectors[0].Name` stays (cosmetic), no change needed.

### Out of scope (flag as follow-up)

The map can now declare >2 garrisons, but the **sim is still 2-team**: `Simulation.Pig.cs:29`
(`NumTeams=2`), the win condition `Simulation.cs:1883` (`loser==0?1:0`), and lobby team validation
(`Lobby.cs:50`, `ClientHub.cs:579,937`) assume exactly teams 0/1. Full N-team play is a separate
effort. This plan keeps stock maps at ≤2 garrisons and adds a boot validation error if a map
declares more teams than the sim supports, so it fails fast rather than misbehaving.

## Verification

1. **Build + unit suites:** `dotnet build server/SimServer.csproj -c Release` and run the dotnet
   test suites (FlightModelTest, ShieldTest, etc.) — de-hardcoding must not regress them.
2. **Byte-identical stock map:** with the migrated `brimstone-gambit.yaml`, confirm the seeded
   world matches today for the parts that should not change — same asteroid field/belt, same base
   positions, same aleph pair. (Dust intentionally changes; assert cloud counts are now sensible per
   sector.) Compare against a pre-change run.
3. **The original repro:** copy one sector's full block onto another sector *of the same radius* and
   confirm they now render identically (same dust feel). Then give two sectors different `radius`
   with identical `dust.amount` and confirm both read equally "dusty" (relative, not absolute).
4. **Launch end-to-end** via `scripts/run-server.sh` + a fresh client: spawn into home, warp through
   the aleph to the other sector, confirm sun/nebula/dust/asteroids per sector and that fog reveal
   still works. Watch for the home-view defaulting to the local team's garrison sector.
4b. **Custom layout:** add a throwaway 3+-sector test map with a `star` `map-pos` layout and
   center-spoke `links`; confirm both the in-game minimap and the lobby `SectorMapPreview` draw the
   authored star (not a ring / horizontal slots), and that a map omitting `map-pos` still falls back
   to the ring. Verify old-client/new-server and new-client/old-server degrade gracefully (proto
   handshake) rather than misparsing.
5. **Fail-fast checks:** a map with a `garrison` team the sim can't support, or a `links` entry to a
   missing sector id, must throw a clear boot error (mirrors `MapLoader` fail-fast at
   `MapLoader.cs:127`).
6. **Diagnostics parity:** re-run the temporary `[DUST-DIAG]` logging approach (server seed +
   client apply) once to confirm the amount→clouds derivation matches expectations, then remove.
