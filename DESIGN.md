# Stellar Allegiance — UI Design System

One source of truth for the client's **bracket / retro-futurism** look, imported from the
Claude Design "Stellar Allegiance — System" component library. **All in-game UI should be built
from these tokens and components** — never re-hardcode colors, fonts, or sizes inline.

- **Code:** `client/scripts/ui/` (namespace `StellarAllegiance.Ui`)
- **Live gallery:** `client/scenes/UiShowcase.tscn` — press **F9** in-game, or boot with
  `--ui-showcase`. This is the visual contract; if you add or change a component, render it here.
- **Source spec:** Claude Design project `28bf0d21-5959-4554-8bfc-a1f92113ea28`
  ("Stellar Allegiance UI design"). Read it with the `claude_design` MCP (`DesignSync`,
  `get_file` on `Stellar Allegiance - System.dc.html`) before extending the system.

## Foundations

### Palette (`DesignTokens`)

| Token | Hex | Use |
|-------|-----|-----|
| `Void` | `#05070F` | background / base |
| `Panel` | `#0B1320` | opaque surface |
| `PanelHi` | `#16243A` | raised surface |
| `PanelFill` | `rgba(8,14,24,.60)` | translucent panel body |
| `PanelSolid` | `rgba(8,14,24,.88)` | near-opaque panel body — surfaces that must stay readable over the live 3D scene |
| `Well` | `#05070F` (opaque) | recessed data well |
| `BorderHi` / `BorderLo` | `rgba(120,190,255,.25/.16)` | hairline borders |
| `BorderMid` | `rgba(120,190,255,.40)` | emphasised hairline — framed panels / rails read over the sector |
| `TeamAccent` | `#37E0FF` | **structural chrome only** — brackets, primary buttons, gauges, diamonds |
| `Secondary` | `#FF9D4D` | highlight / credits |
| `TextHi` / `Text2` / `TextDim` | `#CFE6F5` / `#7FA6C8` / `#5A7390` | primary / secondary / dim text |
| `Ok` / `Warn` / `Danger` / `Data` | `#4DFFA6` / `#FFB347` / `#FF5A6A` / `#9FD6FF` | status + mono telemetry |
| `Faction0` / `Faction1` | blue `(.30,.55,1)` / red `(1,.40,.34)` | **team identity** |

**Rule: `TeamAccent` (cyan) is chrome, not team color.** Team identity is always
`Faction0`/`Faction1`. Never recolor blips, rosters, or trails to cyan. `TeamAccent` is a fixed
static chrome accent — it is never tinted toward the local team's faction color.

### Type scale & fonts (`UiFonts`, `UiKit.TextStyle`)

- **Saira** — UI / headings / labels. **JetBrains Mono** — telemetry, numbers, coordinates.
- Both are **variable TTFs** in `client/assets/fonts/`; weights are realized as `FontVariation`
  (no per-weight files). The caps "Label" style bakes in `LabelLetterSpacing`.
- Styles: `Display` 34/bold · `Hero` 26/bold · `Title` 22/bold · `Body` 15 · `Data` 14 mono ·
  `Label` 13 caps+spacing · `Caption` 11 · `Micro` 9.
- `Hero` is the oversized modal headline between `Title` and `Display`; `Caption` is the dense
  sub-`Label` tier (meta rows, slot sublines) and `Micro` the smallest legible tier (unit
  suffixes, sub-captions). Mono readouts that need those sizes take the size token directly
  (`DesignTokens.CaptionSize`) on top of `TextStyle.Data`.
- Build labels with `UiKit.MakeLabel(text, TextStyle, color?)` — don't set font overrides by hand.
- `UiKit.Tint(alpha)` / `UiKit.TintFull` are the named ends of the "dim a card with `Modulate`"
  idiom; `UiKit.BlankStockLabel(button)` hides the stock `Button` glyph on buttons that paint
  their own content. Use them instead of raw `Colors.White` / `Colors.Transparent`.

## Components

Hybrid pattern: **static factories** (`UiKit`) for stock controls + styling; **Control
subclasses** for anything needing custom `_Draw` or per-frame state.

- **Buttons** — `ChamferButton` (variants `Primary`/`Secondary`/`Ghost`/`Danger`/`Icon`, plus
  `Disabled`; optional `AccentOverride` for faction-colored buttons). Bakes in the UI click SFX
  and hover glow. Use `UiKit.MakeButton(text, onPressed, variant)`.
- **Controls** (`UiKit`) — `MakeSliderRow`, `MakeToggle`, `MakeCheckbox`, `MakeSegmented`,
  `MakeStepper`, `MakeSelect`.
- **KeybindRow** — a rebindable-control row (action label + binding button that captures a new
  key/mouse/gamepad event) for the settings CONTROLS tab; reads/writes via `InputBindings`.
- **Surfaces** — `BracketPanel` (corner brackets, high-priority frames), `HairlinePanel`
  (1px border + optional clipped tab header), `InsetWell`, `DiamondDivider`.
- **Data & feedback** — `RadialGauge`, `SegmentedBar`, `StatusPill` (optional pulse), `AlertBox`,
  `StatReadout`, `DataTable`, `ToastHost`.
- **Connect feedback** — `LinkRadar` (rotating dashed radar ring with centred link %),
  `ProgressSweepBar` (continuous fill + sweeping highlight while indeterminate).
- **Game elements** — `LoadoutSlot`, `ContactChip`, `ResourceReadout`, `RadarFrame`, `GunnerStrip`
  (the top-centre HUD strip a crew gunner sees while riding a captain's turret station — built from
  `RosterCells`; `SetMock(…)` renders it standalone in the gallery).
- **Crew gunner HUD** — a gunner has NO HUD of its own beyond the `GunnerStrip`. A turret seat is a
  pilot's seat minus the controls, so it reuses the pilot's flight HUD verbatim: one aim reticle
  (`MarkerDraw.AimReticle`, drawn by `TargetMarkers` on the turret's real firing line — never a
  second "desired aim" mark), the `SystemRing` centred on it reading the RIDDEN hull, the
  `VelocityIndicator` prograde marker, the `WeaponsPanel` cut to one primary row for the seat's gun,
  and the usual nameplates/brackets/Tab cycle. All of them resolve their subject through
  `HudSubject`, which answers "whose hull, whose firing line" once for both seats. The only
  gunner-specific cue is the reticle's `Warn` tint while `TurretController.Clamped` (the mount is
  pinned against its firing arc). Do not add gunner-only chrome without a reason the pilot's
  equivalent cannot carry.
- **TurretBarrelView** — the 3D gun at a MANNED turret station (`Node3D`, not a `Control`): a short
  barrel on a low mount, sized off the hull's model length and tinted with the faction colour
  (`DesignTokens.Faction`, never the cyan chrome accent), swung onto the gunner's live aim. An
  unmanned station shows nothing at all.
- **Crew / turret stations** — `TurretStationRow` (a captain's ▶ TURRET STATIONS row: ◣ tile,
  seat id + MANNED/OPEN, gun, gunner — selectable into the arsenal frame), `CrewManifestRow`
  (a gunner's read-only ▶ TURRET MANIFEST row: ◆ pip tile, seat id + gun, YOU / name / OPEN), and
  `CrewCard` (the CommandSidebar's CREWED SHIPS · TAKE A TURRET card — hull header, `n/N MANNED` /
  `IN FLIGHT`, one row per station with the `＋ JOIN` / `✕ LEAVE` tag). Station state is always
  server-owned (`CrewStore`); these never paint an optimistic seat.
- **Roster primitives** (`RosterCells`) — the small builders every pilot-roster surface composes:
  `Mono`/`Lbl` cells, `Cell` (proportional column width), `Badge`, `Diamond`, `RowPanel`
  (hairline row, faction tint + 2px bar for "me"), `HeaderPanel` (4% accent wash), `TabStyle`,
  `BarPanel`, `PaddedRow`/`Margins`, `Spacer`, `Hairline`, `EmptyNote`, plus the `.With(…)` fluent
  helper (`UiControlExt`). The Lobby roster and both Scoreboard modes are built from these, so the
  header and its rows must always be passed the SAME column ratios or the columns won't line up.
- **Scoreboard** — the two-mode match board (`Scoreboard`): a centred `BracketPanel` LIVE board over
  the running sector (F5, read-only, `MouseFilter.Ignore` everywhere and it never touches
  `Input.MouseMode`), and a full-screen POST-MATCH result screen with sortable columns, team-filter
  cards, the team-summary comparison bars and the Top Gun `AlertBox`. `--ui-open=scoreboard-live` /
  `--ui-open=scoreboard-post` raises a mode for a `--ui-shot` capture of the live game UI.
- **Game Lobby server-notice strip** — a standing `AlertBox` (Warn tone) in a chrome `BarPanel`, inserted
  between the status bar and the body (`Lobby.BuildNoticeStrip`), hidden unless the server has a pending
  auto-update notice to show. No new component — existing parts only.
- **Backgrounds** — `NebulaBackground` (animated warm/blue gas-cloud fill from the `Nebula.dc.html`
  spec; a single `canvas_item` shader — drifting screen-blended clouds, Void vignette, star-dot
  grid, scanlines; `Intensity` 0..1). Use it behind full-screen menu overlays whose backdrop is
  **not** the live 3D space scene (e.g. the server browser); overlays shown over the running game
  keep letting the real space show through instead.
- **Draw helpers** — `UiDraw.Chamfer`/`ChamferPoints`/`TabPoints`/`CornerBrackets`/`Hairline`/`Diamond`.
- **Mouse cursor** — `UiCursor.Apply()` (called once at boot) replaces the OS arrow and I-beam
  with `assets/ui/cursor*.png`: a compact angular Void-filled, cyan-outlined pointer plus a
  matching I-beam. Art is generated by `tools/cursor-gen/gen_cursor.py` — always
  regenerate there (then `godot --headless --import`) instead of editing the PNGs; the base
  accent is used deliberately (chrome is never faction-tinted).

## Wiring & gotchas

- **Theme is applied per top-level overlay**, not globally: call `UiTheme.Apply(control)` on each
  full-screen overlay's root (Lobby, Scoreboard, ConnectLinkModal, ServerLobbyOverlay, Chat). A Theme can't
  live on a `CanvasLayer`, and wrapping the Hud in one extra Control would break the
  `GetNode("../../GameNetClient")` relative lookups in Lobby/Chat — don't do that.
- **`ChamferButton` draws on top of the stock Button**, so it blanks both the stock styleboxes
  and the stock label (transparent font colors) and paints its own chamfer + label.
- **Chamfers are geometry** (an explicit polygon), not a StyleBoxFlat corner radius — keep
  `CornerRadius = 0`. Hairline styleboxes set `AntiAliasing = false` so 1px edges stay crisp.
- **Custom-draw nodes read fonts from `UiFonts`** (not the cascaded Theme) so they render
  standalone and survive a cold import cache.
- **Per-frame redraw discipline:** call `QueueRedraw()` only on change in gauges/bars/button-glow.
- **Fonts need import:** `.import` sidecars are gitignored (same convention as the GLBs); run
  `godot --headless --import` after pulling new fonts. `UiFonts` falls back to the engine font if
  the import cache is cold.

## Game Launcher port (Avalonia)

The Game Launcher (`launcher/`, see `launcher/README.md`) is a separate Avalonia app that must look like
the game. It does **not** get its own copy of the design system:

- **Tokens are linked, not copied.** `launcher/Core` compiles THIS repo's `client/scripts/ui/DesignTokens.cs`
  via `<Compile Include … Link>` together with a small `Godot.Color` shim
  (`launcher/Core/Theme/GodotColorShim.cs`). Consequence for this file's owner: **`DesignTokens.cs` must
  stay limited to `Color.FromHtml`, `new Color(…)` and consts** — anything Godot-only breaks the launcher
  build (loudly; extend the shim or move the value). `tests/LauncherTest` guards it.
- **Text styles** are mirrored as data (`launcher/Core/Theme/TextStyleTable.cs`) and drift-tested against the
  `TextStyle` switch in `UiKit.cs`. `Sa.Text(…)` is the launcher's `UiKit.MakeLabel`.
- **Controls are ports with the same numbers** (`launcher/App/Controls/`): `ChamferButton`, `BracketPanel`,
  `HairlinePanel`, `DiamondDivider`, `ProgressSweepBar`, `StatusPill`, `AlertBox`, plus `Backdrop`
  (pre-rendered nebula from `tools/launcher-art` + device-pixel scanlines/star dots). When a component's
  drawing rules change here, change its port too and compare `--launcher-showcase` with `UiShowcase`.
- **Two forced differences.** Fonts are **static instances** (`tools/font-instancer`: Avalonia ignores
  variable-font axes, and Saira's default `wght` is 100/Thin). Symbols are **vector icons** (`SaIcon`):
  Saira has none of ◆ ● ✓ ▸ ✕ ○ ⚠ ⚙ — the game gets them from Godot's fallback fonts, Avalonia would fall
  back to whatever the OS has.
- A lint in `tests/LauncherTest` rejects colour literals anywhere in `launcher/App` — same rule as above.
- Screenshots without a display: `StellarLauncher --launcher-showcase --launcher-shot=out.png`, or
  `--launcher-fake=<state>` for any window state (`launcher/App/Views/FakeViews.cs`).

## Adding / changing a component

1. Read the source spec via the `claude_design` MCP first (don't guess at the visual language).
2. Add tokens to `DesignTokens`, not inline literals.
3. New types go in `StellarAllegiance.Ui`, no `[GlobalClass]`/`class_name`.
4. Add it to `UiShowcase.cs` and verify with `godot --path client res://scenes/UiShowcase.tscn
   -- --ui-shot=/tmp/ui.png` (one-frame capture), comparing against the spec.
