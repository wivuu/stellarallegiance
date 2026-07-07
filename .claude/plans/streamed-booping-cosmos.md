# Per-Sector Environment: God Rays, Nebula, Belt Tuning & Dust Clouds

## Context

Today a map's per-sector authored surface is exactly three fields — `id`, `radius`, `name`
(`MapSectorDef` in `server/Content/MapLoader.cs:32-37`). Everything that makes a sector *feel*
different is either a global compile-time constant or client-side procedural noise:

- **Sun / god rays** — a single global static `DirectionalLight3D` + billboarded `Sun` disc
  (`client/scenes/Main.tscn:66-78`, `client/scripts/Sun.cs`) with only a 2D screen-space
  `LensFlare`. There are **no volumetric light shafts** and no per-sector sun.
- **Nebula** — `client/scripts/Starscape.cs` seeds its sky shader purely from the sector id
  (`SetSector`, `Starscape.cs:52-85`); the server sends nothing.
- **Asteroid belts** — shape is fixed in compile-time consts (`server/Sim/World.cs:55-61`),
  applied identically to every map.
- **Dust** — one global `GpuParticles3D` field, hardcoded `Amount=90`
  (`client/scripts/DustField.cs`), with zero gameplay effect.

This change introduces an optional per-sector `environment:` block in map YAML that drives all
four, shipped as **one feature with a single protocol bump**. Two decisions from the requester:

1. **God rays = Godot volumetric fog.** One `WorldEnvironment` volumetric-fog system produces
   true sun light-shafts *and* doubles as the cloud-like dust haze — dust density feeds fog density.
2. **Dust clouds are procedurally distributed.** The YAML sets *distribution + characteristics*
   (count, size range, coverage, density, color, attenuation), and the server seeds the actual
   clouds deterministically — exactly like asteroids. They **impact vision/radar** (configurable).

Belt tuning stays **server-only** (the client already receives concrete rocks). Sun, nebula, and
the seeded dust clouds are **streamed** so the client renders precisely where the sim attenuates.
Fog-off must stay byte-identical (dust attenuation lives entirely inside the fog-gated path).

---

## Design overview

```
map YAML  ─►  MapSectorDef.Environment  ─►  WorldSectorConfig.Env  ─┐
                                                                    ├─► World ctor
  server-only: belt tuning  ────────────────────────────────────────┤     ├─ SeedAsteroid* (belt overrides)
                                                                    │     └─ SeedDustClouds (immutable list)
  sim: dust attenuation  ◄── World.DustClouds ◄──────────────────────┘           │
        ClassifyTarget / IsPointVisibleToTeam / TeamStillSeesShipLive            │
                                                                                 ▼
  streamed: sun + nebula + dust-cloud list  ─► WriteSectorStatic ─► client Net.Sector
                                                                          └─► SectorEnvironment driver
                                                                                (sun light, volumetric fog,
                                                                                 FogVolume clouds, nebula)
```

---

## 1. YAML schema + config model (server)

Add an optional `environment` block; every field nullable so an omitted block = today's behavior.

**`server/Content/MapLoader.cs`** — add `Environment` to `MapSectorDef` and new DTO records:

```yaml
sectors:
  - id: 1
    radius: null
    name: Verge
    environment:
      sun:
        azimuth: 35            # degrees around Y; elevation below. Omitted → current static light.
        elevation: 18
        color: [1.0, 0.85, 0.6]
        energy: 1.3
        god-rays: 0.5          # 0..1 volumetric light-shaft strength (fog albedo/emission from sun)
      nebula:                  # all optional; omitted → keep current sector-id procedural seeding
        color-a: [0.4, 0.2, 0.6]
        color-b: [0.2, 0.3, 0.7]
        intensity: 0.08
        seed: 1234             # optional seed override
      belt:                    # SERVER-ONLY, not streamed. Omitted fields → World.cs consts.
        area-density: 2.4e-5
        inner-frac: 0.25
        outer-frac: 0.95
        flatten: 0.13
        fill-frac: 0.9         # (core/field sectors)
      dust:                    # distribution + characteristics; server seeds actual clouds
        cloud-count: 12
        radius-min: 300
        radius-max: 900
        coverage-frac: 0.85    # clouds distributed within this fraction of sector radius
        flatten: 0.15          # vertical squash (shallow, like belts)
        density: 0.7           # visual thickness + attenuation strength
        color: [0.45, 0.4, 0.55]
        vision-mult: 0.55      # effective radar/eyeball range multiplier through full dust
        base-fog: 0.03         # thin ambient volumetric fog so god-rays shaft even outside clouds
        seed: 999              # optional
```

- New records in `MapLoader.cs`: `SectorEnvDef { SunDef? Sun; NebulaDef? Nebula; BeltDef? Belt; DustDef? Dust }`
  plus `SunDef`, `NebulaDef`, `BeltDef`, `DustDef` (kebab-case, YamlDotNet via `CoreSerializer` — same
  as existing fields). Colors are `double[]?` (length-3).
- **`shared/Defs.cs`** — extend `WorldSectorConfig` (`Defs.cs:248-253`) with a mirrored
  `SectorEnvironment? Env` (new `sealed class SectorEnvironment` + nested `Sun/Nebula/Belt/Dust`
  structs holding `float`/`float?`/`Vec3?` fields). Keep the existing "NOT streamed" comment accurate:
  the *config* object isn't streamed; the sim projects the streamable parts into `World`.
- **`MapLoader.ApplyTo`** (`MapLoader.cs:95-109`) — extend the `Select` projection to copy
  `s.Environment` into `WorldSectorConfig.Env` (downcast `double`→`float`, arrays→`Vec3`).

## 2. Server sim: belt tuning + dust cloud seeding (`server/Sim/World.cs`)

- Add `readonly record struct DustCloud(uint SectorId, Vec3 Pos, float Radius, float Density)` and
  `public readonly List<DustCloud> DustClouds = new();` alongside `Asteroids` (`World.cs:86`).
- In the ctor (`World.cs:165-237`), add local resolvers mirroring `ResolveRadius`/`ResolveName`
  (`:176-190`): `ResolveEnv(id) → SectorEnvironment?`. Thread the belt overrides into the existing
  `SeedAsteroidField`/`SeedAsteroidBelt` calls (`:214-215`): give both a resolved
  belt-params struct (each field falling back to the current `World.cs:55-61` consts).
- New `SeedDustClouds(ref DetRng rng, uint sector, float sectorRadius, DustParams p, ref ulong id)`:
  distributes `p.CloudCount` clouds within `coverage-frac × radius`, `flatten`-squashed on Y,
  radius drawn in `[radius-min, radius-max]`, density = `p.Density` (mild jitter). **Use a dedicated
  RNG stream** (`new DetRng(seed ^ 0xD005_7D05_D005_7D05UL)`) so it does NOT perturb the
  asteroid/aleph sequence — same discipline as `baseRng` at `World.cs:199`. Seed both sectors after
  the aleph placement (`:221`). Clouds are **immutable after the ctor** → safe for the vision worker
  to read directly, exactly like the rock grid.
- Dust clouds are large (radius ≫ `GridCell=160`), so **do not grid-index them**; store a flat
  per-sector list and scan it (cloud counts are ~10–20/sector — cheap).

## 3. Server sim: dust attenuation of vision/radar (`server/Sim/Simulation.Vision.cs`)

The single choke point for effective range is the per-viewer `× sig` multiply in two functions
(plus one live-check). Add a dust factor there.

- New helper `float DustVisionMult(uint sector, Vec3 from, Vec3 to)`: scan `World.DustClouds` for
  `sector`, accumulate optical depth `τ = Σ density × chord(cloud, from→to) / (2·radius)` clamped to
  `[0,1]`, return `mult = 1 - τ·(1 - visionFloor)` where `visionFloor` comes from the sector's
  `dust.vision-mult` (default 1.0 = no dust). Monotonic, bounded, handles viewer-inside-cloud (near
  chord) and cloud-between (mid chord) identically.
- **`ClassifyTarget`** (`Simulation.Vision.cs:622-680`): compute `mult` once per viewer→target
  (next to the shared `SegmentBlockedByRock` scan, `:656`) and fold into `sr`, `cl`, `er`
  (`:633/636/640`) — `float sr = v.SphereRadius * sig * mult;` etc. Apply to the base-viewer sphere
  (`:672`) too.
- **`IsPointVisibleToTeam`** (`Simulation.Vision.cs:935-995`, the live sim-thread mirror): apply the
  same `mult` to its inline sphere/cone/eyeball tests (`:947-993`).
- **`TeamStillSeesShipLive`** (`:911-930`): apply `mult` to its live eyeball-radius test (`:925`).
- **Worker safety**: the worker reads `World.DustClouds` directly (immutable post-seed), matching how
  it reads the rock grid — no `CaptureVisionInput` snapshot capture needed. Do not read any mutable
  dust state.
- **Fog-off byte-identical**: all three functions are only exercised under fog (`IsPointVisibleToTeam`
  short-circuits `true` at `:937-938`; `ClassifyTarget` runs only in the fog worker). No extra gating
  needed — verify no dust math executes on the fog-off path.
- **Scope note**: leave `DiscoverRocks`/`WarpDiscoverRocks` (`:684`, `:1007`) scan ranges
  **unaffected** — dust hides ships, not the static rock map. Call this out in the PR.

## 4. Streaming: extend the per-sector static (`server/Net/Protocol.cs`)

- **`WriteSectorStatic`** (`Protocol.cs:418-423`) — after `id/radius/name`, append the streamed
  environment: `sun` (dir xyz + color rgb + energy + godrays as floats), `nebula`
  (hasOverride flag, colorA/colorB, intensity, seed), `dust` (base-fog + color + `u16` cloud count,
  then per cloud `posXYZ + radius + density`). Because Welcome (`:463-465`, `:499-505`) and
  `MsgReveal` (`:637-639`) both call `WriteSectorStatic`, both paths stay in sync automatically.
  The server pulls the cloud list for a sector from `World.DustClouds` and sun/nebula from the
  sector's resolved `SectorEnvironment`.
- **Belt tuning is NOT written** — server-only.
- **Bump the protocol version constant** (in `Protocol.cs`; current is 24) and update any client
  mirror. Per the missiles/protocol memory, a Godot-client protocol bump must be smoke-tested with
  `--autofly` since the dotnet suites don't cover the client.

## 5. Client decode + storage

- **`client/scripts/NetTypes.cs`** — extend `Net.Sector` (`:163-172`) with an `Environment` payload:
  `SunDir/SunColor/SunEnergy/GodRays`, `NebulaHasOverride/NebulaColorA/NebulaColorB/NebulaIntensity/NebulaSeed`,
  `DustBaseFog/DustColor`, and `DustCloud[] { Vector3 Pos; float Radius, Density; }`.
- **`client/scripts/GameNetClient.cs`** — extend the sector decode in `ApplyWelcome`
  (`:1000-1009`) and the `MsgReveal` path (`ApplyReveal`, ~`:1085`) to read the new fields (must
  mirror the server write order exactly).
- **`client/scripts/WorldRenderer.cs`** — `NetAddSector` (`:881-884`) stores the richer `Sector`
  into `_sectors` unchanged.

## 6. Client render: per-sector environment driver

Add a new sibling node **`SectorEnvironment` (`client/scripts/SectorEnvironment.cs`)** in
`client/scenes/Main.tscn` (next to `Starscape`/`DustField`). Its `Apply(Net.Sector s)`:

1. **Sun / god rays** — orient the scene `DirectionalLight3D` (`Main.tscn:66-75`) to `s.SunDir`,
   set `LightColor`/`LightEnergy`; retint the `Sun` disc. `Sun.cs` reads the light basis in
   `_Ready` (`Sun.cs:25-36`) — add a public `RefreshFromLight()` and call it after reorienting so
   `Sun.SkyDirection` (which `LensFlare` also consumes) stays correct.
2. **Volumetric fog** — on the shared `WorldEnvironment` Environment, enable `VolumetricFogEnabled`,
   set `VolumetricFogDensity = s.DustBaseFog`, and drive `VolumetricFogAlbedo` + emission from
   `s.GodRays` and dust color. The sun `DirectionalLight` shafting through this fog *is* the god-ray
   effect (no new shader).
3. **Dust clouds** — spawn/pool `FogVolume` nodes (ellipsoid) at each `s.DustClouds[i].Pos/Radius`,
   per-volume density = `cloud.Density`, albedo = `s.DustColor`. Localized thick fog = visible dust.
   Tag each with the `"sector"` meta via the existing `SetNodeSector` (`WorldRenderer.cs:896-919`)
   so `RefreshSectorVisibility` shows only the current sector's clouds.
4. **Nebula** — extend `Starscape.SetSector` (`Starscape.cs:52-85`) with an optional override
   (colorA/colorB/intensity/seed); when `s.NebulaHasOverride` use them, else fall back to the current
   sector-id seeding (preserves today's look for maps with no `nebula:` block).

**Wiring the transition seam**: `_starscape.SetSector(id)` is called from 5 spots
(`WorldRenderer.cs:1154, 1206, 329, 877, 1544`). Add a single `WorldRenderer.ApplySectorEnv(uint sector)`
that looks up the stored `Net.Sector` and drives BOTH `_starscape` (nebula) and the new
`SectorEnvironment` (sun/fog/dust); replace the 5 raw `_starscape.SetSector` calls with it. When a
sector has no streamed env, the driver reproduces today's behavior (procedural nebula, no fog, no
clouds, static sun) for backward compatibility.

## 7. Stock content + docs

- **`server/Content/maps/brimstone-gambit.yaml`** — add `environment:` blocks to sectors 0 and 1
  demonstrating sun/god-rays, a nebula palette, belt tuning, and dust clouds. This is the reference
  example and gives the stock map the new look.
- **`GLOSSARY.md`** — add terms: per-sector environment, god rays / volumetric fog, dust clouds
  (distribution + vision/radar attenuation), with file pointers (CLAUDE.md requires glossary updates
  for new gameplay systems).

---

## Files to modify

| Area | File(s) |
|---|---|
| YAML schema + config | `server/Content/MapLoader.cs`, `shared/Defs.cs` |
| Sim: seeding + attenuation | `server/Sim/World.cs`, `server/Sim/Simulation.Vision.cs` |
| Streaming | `server/Net/Protocol.cs` (+ version bump) |
| Client decode | `client/scripts/NetTypes.cs`, `client/scripts/GameNetClient.cs` |
| Client render | new `client/scripts/SectorEnvironment.cs`, `client/scenes/Main.tscn`, `client/scripts/WorldRenderer.cs`, `client/scripts/Starscape.cs`, `client/scripts/Sun.cs` |
| Content + docs | `server/Content/maps/brimstone-gambit.yaml`, `GLOSSARY.md` |

## Verification

1. **Unit tests** (`dotnet test` — the Godot client is not covered here):
   - `MapLoader` parse test: a map with a full `environment:` block round-trips into
     `WorldSectorConfig.Env`; an omitted block leaves `Env == null`.
   - `World` seeding determinism: same seed ⇒ identical `DustClouds` (count/pos/radius); the dust RNG
     stream does **not** shift asteroid/aleph positions (assert `Asteroids` byte-identical to a run
     with dust disabled).
   - Vision dust test (extend the `Simulation.Vision` suite): viewer + enemy with a dust cloud on the
     sightline ⇒ enemy drops off radar at a range where it was visible without dust; no dust ⇒
     unchanged; **fog-off ⇒ byte-identical** to pre-change (no dust math on that path).
2. **Client smoke** (protocol bump): run the native server headless (`--server --anonymous`, hold the
   connection so the sim ticks — see headless-sim memory) and launch the client with `--autofly`;
   confirm no decode desync, god-ray shafts render, dust clouds appear, and nebula matches the stock
   map's authored palette.
3. **Sector switching**: use F3 sector overview / warp between home and verge to confirm sun, fog,
   dust, and nebula all repaint per sector via the `ApplySectorEnv` seam.
4. **Fog toggle**: flip the fog-of-war toggle and confirm dust has zero gameplay effect with fog off
   (bytes identical), visuals still render.
