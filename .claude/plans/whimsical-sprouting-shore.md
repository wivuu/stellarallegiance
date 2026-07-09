# Nav-light coloring: base entrance/exit + ship wing lights

## Context

Every stock ship and base carries `HP_Light_*` hardpoint nodes that the client renders as
blinking `BaseBeacon`s. Today **every** beacon is tinted by team only (blue for team 0, amber
otherwise) — there is no notion of *what* a light marks. The goal is real-craft nav-light
semantics:

- **Bases** — lights next to a **docking entrance glow green**, lights next to a **docking
  exit glow red**, and any other base light is **white**. This must generalize to future base
  models with arbitrary light/dock layouts (no per-model hardcoding).
- **Ships** — the **starboard (right) wing light is green, the port (left) is red**, like an
  aircraft's position lights; all other ship lights are **white**.
- Ship lights must remain **visibly smaller** than base lights.

This is a **client-only visual change**. No server, protocol, def-schema, or GLB rebake is
required — the geometry we need (Light + DockingEntrance/DockingExit hardpoints, and each
light's X offset) is already present in the streamed defs and the baked GLB nodes.

## Current behavior (what we're changing)

- Beacon color is a hardcoded team literal in two places:
  - `client/scripts/ShipModelLoader.cs:181` — ship beacons, in `AttachEngineGlow(...)` (lines 179-197).
  - `client/scripts/BaseModelLoader.cs:202` — base beacons, in `MakeBeacon(...)` (built from the
    `HP_Light_*` GLB nodes gathered at lines 82-95).
- The shared visual is `BaseBeacon : Node3D` (`BaseModelLoader.cs:225-320`); its `Color` field
  is set by the caller before `AddChild`, and its size knobs are `MoteSize`/`Range`/`Intensity`.
  Docking hardpoints currently render as invisible `Marker3D`s only (no light).

## Design

### Nav palette (new shared constants)

Add three world-light colors (nav green / nav red / nav white) as `public static readonly Color`
consts on `BaseBeacon` (so both loaders reference one source, mirroring how `BaseBeacon` is
already the shared visual). These are gameplay/world emissive colors, **not** UI `DesignTokens`.

```
NavGreen = (0.15, 1.0,  0.35)   // starboard / docking entrance
NavRed   = (1.0,  0.18, 0.18)   // port / docking exit
NavWhite = (1.0,  0.96, 0.9)    // everything else (slightly warm)
```

Team tint is dropped for these lights (universal nav colors; friend/foe still reads from the
hull HUD tint, as it already does for the GLB-materialed base/ship hulls).

### Base lights — associate each light with the nearest dock feature (`BaseModelLoader.cs`)

In `Build(...)`, alongside the existing `HP_Light_*` gather (line 82), also gather the dock
hardpoints from the same hull, mapped into the same root frame:

```csharp
var entrances = GlbLoader.FindHardpoints(hull, $"HP_{HardpointKind.DockingEntrance}_");
var exits     = GlbLoader.FindHardpoints(hull, $"HP_{HardpointKind.DockingExit}_");
```

For each light position, pick a color via a new helper `DockLightColor(pos, entrances, exits)`:

- Measure distance in the **horizontal footprint** — the plane perpendicular to the base's
  up-axis (**+Y**, which is also `HP_DockingExit`'s forward on the stock base). i.e. compare
  using `(x,z)` distance, ignoring `y`. *Rationale: the stock base stacks each dock's lights in
  a vertical column sharing that dock's X/Z channel; `HP_DockingEntrance_4` (Z=−5.7) sits close
  to the exit-light column in full 3D and would mis-green it, but footprint distance classifies
  all 12 stock lights correctly (6 green entrance column @Z=−7.91, 6 red exit column @Z=−4.6).*
- Nearest feature within a proximity cutoff → its color: entrance ⇒ `NavGreen`, exit ⇒ `NavRed`.
- No dock within the cutoff (or the base has no dock hardpoints) ⇒ `NavWhite`. This is what
  keeps unrelated lights on future bases white without hardcoding.
- Cutoff: a generous fraction of the base radius (e.g. `def.Radius * 0.5` in world units, or a
  fixed local-unit slack) so lights that clearly belong to a bay classify, and stray hull lights
  don't. Tune during verification.

Apply the same association in the def-fallback branch (procedural sphere, lines 89-95) using the
`HardpointDef` entrances/exits so it degrades consistently. Pass the chosen color into
`MakeBeacon` (add a `Color` param) instead of the team literal.

### Ship wing lights — color by side (`ShipModelLoader.cs`)

In the Light loop (lines 184-196), replace the team `beaconColor` with a per-light color:

- Compute the hull's max `|OffX|` across its Light hardpoints. A light is a **wing light** when
  `|OffX| >= wingFrac * maxAbsX` (e.g. `wingFrac = 0.45`) **and** `maxAbsX` is non-trivial. This
  selects the outboard lights (fighter/bomber wingtips, bomber aft-wing pair, scout wings) and
  leaves nose/tail/centerline lights (small `|OffX|`) as `NavWhite`.
- Wing light color by side: **starboard ⇒ `NavGreen`, port ⇒ `NavRed`.** Per the game's
  forward=+Z / up=+Y convention in Godot's right-handed frame, starboard = **−X**, port = **+X**
  (right = forward × up = Z × Y = −X). **This must be confirmed visually** — a mirrored GLB
  export would flip it; if so, swap the sign test in one place.
- Keep ship beacons smaller than base beacons: retain the existing ship sizing
  (`MoteSize = len*0.09`, `Range = len*0.6`, `Intensity = 0.4` — already far under the base
  defaults `MoteSize 2.4 / Range 12 / Intensity 1`). Nudge down if verification shows parity.

Ships have no docking hardpoints, so the base dock-association logic does not apply to them.

### Scope notes

- Blink behavior is unchanged (real wing nav lights are steady, but changing that is out of
  scope; leaving beacons blinking keeps the diff tight). Flag as an optional follow-up.
- No new streamed field on `HardpointDef` is needed — everything derives from existing geometry.

## Files to modify

- `client/scripts/BaseModelLoader.cs` — gather dock hardpoints; add `DockLightColor(...)` helper
  (footprint nearest-feature + cutoff); thread color into `MakeBeacon`; add nav-color consts to
  `BaseBeacon`; apply to both the GLB-light and def-fallback branches.
- `client/scripts/ShipModelLoader.cs` — replace team `beaconColor` with side/white selection in
  the Light loop (`AttachEngineGlow`, ~lines 179-197).

## Verification

1. Build the client and drive it headlessly with the **`verify` skill** (real server + Godot
   client, screenshot/movie capture). A live base is present via
   `scripts/run-server.sh --local --autostart` + autofly.
2. **Base check** (capture the garrison base from outside the dock): the entrance-bay light
   column reads **green**, the exit-bay column reads **red**, and confirm no stray white/other
   lights are mis-colored. Specifically confirm `HP_DockingEntrance_4` did not bleed the exit
   column green (the reason for footprint distance).
3. **Ship check** (external/chase view of own + a remote ship): starboard wing **green**, port
   wing **red**, nose/tail lights **white**. If green/red are swapped, flip the `OffX` sign test.
4. Confirm ship lights are clearly smaller than base lights in the same frame.
5. Fog-off byte-identity and existing `tests/ContentTest` are unaffected (no def/geometry change);
   no dotnet test covers Godot visuals, so the `verify` capture is the acceptance evidence.

## Open items to resolve during implementation (via verification, not user input)

- Starboard = −X vs +X (mirrored-GLB risk) — confirm from the capture, flip if needed.
- Exact cutoff radius for base white-vs-colored and `wingFrac` for ships — tune to the captures.
