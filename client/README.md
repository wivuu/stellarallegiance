# client/

The **Godot 4.7 (C#/.NET 10) game client**. It renders the world, takes input, and runs
client-side prediction — but it is **never** authoritative. It predicts the local ship,
interpolates remote ships, and reconciles everything against the authoritative 20 Hz snapshots
streamed by [`server/`](../server). All content (world layout, ship/asteroid catalogs, tuning
defs) is downloaded from the server at connect time; the client ships with no baked gameplay
constants.

## Layout

```
project.godot            Godot project (entry scene = scenes/Main.tscn)
stellarallegiance.csproj    C# project compiled by Godot's Mono build; references ../shared
stellarallegiance.sln       solution (client + shared)
export_presets.cfg       macOS / Windows / Linux export presets (see scripts/export-clients.ps1)
default_bus_layout.tres  audio bus layout for the spatial-audio mixer
scenes/Main.tscn         the single root scene; everything is spawned/driven from here
scripts/                 all gameplay C# (see below)
scripts/ui/              shared UI component library (the "Stellar Allegiance" design system)
scenes/UiShowcase.tscn   design-system gallery (every component on one page)
assets/                  ships/, asteroids/, bases/, audio/, fonts/ — GLBs, sound, typefaces
```

## scripts/ — the C# that runs the client

Roughly grouped by responsibility:

- **Networking & state** — `GameNetClient`, `ConnectionManager`, `ConnectLinkModal`,
  `NetTypes`, `DefRegistry` (content defs downloaded from the server).
- **Prediction & flight** — `PredictionController` (predict + reconcile against snapshots),
  `ShipController`, `ShipMath` (uses the shared deterministic `FlightModel`).
- **Rendering** — `WorldRenderer`, `RemoteShip`, `ProjectileView`, `Starscape`, `Sun`,
  `DustField`, `EngineGlow`, `TeamTrail`, `ExplosionEffect`, `HitFlash`, `AlephView`.
- **Models** — `GlbLoader`, `ShipModelLoader`, `BaseModelLoader` (load the GLBs from `assets/`).
- **UI / HUD** — `Hud`, `Lobby`, `Chat`, `Minimap`, `SectorOverview`, `TargetMarkers`,
  `ServerLobbyOverlay` — all built on the shared design system below.
- **UI design system** — `scripts/ui/` is one source of truth for the bracket / retro-futurism
  look imported from the Claude Design "Stellar Allegiance — System" spec. `DesignTokens`
  (palette / type scale), `UiFonts` (Saira + JetBrains Mono variable fonts → weighted
  `FontVariation`s), `UiTheme` (runtime `Theme`, applied per top-level overlay), `UiDraw`
  (chamfer / bracket primitives), `UiKit` (factories for stock controls), plus reusable
  Control components (`ChamferButton`, `BracketPanel`, `RadialGauge`, `StatusPill`,
  `AlertBox`, `DataTable`, `ContactChip`, `RadarFrame`, …). Team identity stays the
  blue/red faction colours; the cyan accent is structural chrome only.
- **Audio** — `SfxManager` (spatial SFX via `PlayAt`/`PlayUi`, hooked into combat/engine events).

## Running

From the repo root (not this directory):

```pwsh
scripts/run-client.ps1            # opens the public-lobby server browser
scripts/run-client.ps1 -Local     # connects straight to localhost:8090
```

`run-client.ps1` rebuilds the client C# fresh before launching so Godot can't run a stale
assembly against a rebuilt server (which would cause silent protocol skew). See the root
[README](../README.md) and [QUICKSTART](../QUICKSTART.md) for prerequisites (Godot Mono build,
.NET 10 SDK).

### Design-system gallery

Press **F9** in-game to toggle the component gallery as a live overlay, or boot straight into
it for a screenshot:

```bash
godot --headless --import --path client           # required once after pulling new fonts
godot --path client -- --ui-showcase              # opens scenes/UiShowcase.tscn
godot --path client res://scenes/UiShowcase.tscn -- --ui-shot=/tmp/ui.png   # one-frame capture
```

The fonts in `assets/fonts/` are variable TTFs (OFL); their `.import` sidecars are regenerated
by `godot --headless --import` (same convention as the GLBs), and `UiFonts` falls back to the
engine font if the import cache is cold.
