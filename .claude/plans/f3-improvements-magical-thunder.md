# F3 Improvements — hide combat HUD & allow F3 while docked

## Context

**F3** toggles the **`SectorOverview`** (`client/scripts/SectorOverview.cs`) — an orbiting
tactical-map camera around the local sector (`SectorOverview.Active` is the global flag;
`SectorOverview.ActiveCamera` is the overview camera). It is not a separate scene: it just
swaps to a second `Camera3D` pointed at the already-rendered sector, plus a blue grid, yellow
altitude stems, and a hint label.

Two problems today:

1. **The flight HUD stays up in F3.** The combat overlays were deliberately built to *reproject
   through the overview camera* and remain visible, so the ship-centric reticule, hull/shield
   ring, weapons panel, velocity marker, and top-left telemetry all clutter the map. We want
   those ship-combat readouts hidden — while keeping the tactical map aids (entity brackets,
   nameplates, altitude stems, minimap click-to-retarget).

2. **F3 does nothing while docked.** When docked, `WorldRenderer.LocalShip == null` and the
   opaque full-screen hangar (`ShipLoadout`, a `ColorRect { Void }` covering the viewport)
   occludes the 3D scene. The F3 key *is* still polled, but the map renders behind the hangar so
   nothing is visible. We want to peek at the sector map from the hangar.

The established idiom for this kind of global UI-state gate already exists: overlays AND
`!ZoomView.Active` into their per-frame `Visible`, and `inputFree` checks read
`!SectorOverview.Active && !ShipLoadout.Active` etc. We follow that pattern.

## Feature 1 — Hide ship-combat readouts in F3

Each combat overlay re-asserts its own `Visible` every frame in `_Process`, so setting `.Visible`
externally won't stick — the fix is to AND `!SectorOverview.Active` into each gate, exactly like
the existing `!ZoomView.Active`.

- **`client/scripts/SystemRing.cs`** (`_Process`, ~line 51) — hull ring + cyan SHLD arc + boost.
  `Visible = _world.LocalShip != null && !ZoomView.Active && !SectorOverview.Active;`
- **`client/scripts/WeaponsPanel.cs`** (`_Process`, ~line 63) — append `&& !SectorOverview.Active`.
- **`client/scripts/VelocityIndicator.cs`** (`_Process`, ~line 59) — append `&& !SectorOverview.Active`.
- **`client/scripts/TargetMarkers.cs`** — do **NOT** hide the whole node; it also draws the entity
  brackets/glyphs/edge-arrows/ghosts we want to keep on the map. Instead, in `_Draw()`, wrap only
  the **ship firing-line reticule block** (`TargetMarkers.cs:482-520`: the `muzzle`/lead
  computation, `DrawLeadIndicator`, and `DrawAimReticle`) in `if (!SectorOverview.Active) { ... }`.
  Everything above it (`DrawEntity` for bases/friendlies/enemies, `DrawGhosts`, `DrawFocusTag`,
  `DrawLockArc`) stays. Also skip `DrawIncomingWarning(view)` (line 524) under the same guard — a
  flashing threat banner is a combat readout, not a map aid.
- **`client/scripts/Hud.cs`** — hide the top-left telemetry label `_label` (ping/reconcile readout,
  set visible in `Hud._Process`) while F3 is up: AND `!SectorOverview.Active` into its `.Visible`.
  Leave `_sectorShips` / `_credits` (match info) and `LensFlare` / `Minimap` / `ViewModeIndicator`
  alone — the minimap is intentionally kept so F3's click-to-retarget-sector still works.

Nameplates (via `PredictionController`/`RemoteShip`/`Nameplate.cs`) are unaffected and continue to
label ships on the map — intended.

## Feature 2 — Allow F3 while docked

**2a. Un-occlude the map by hiding the hangar while F3 is open.** `ShipLoadout` currently has no
`_Process`. Add one that mirrors the overlay idiom:

```csharp
public override void _Process(double delta)
{
    Visible = !SectorOverview.Active; // peek at the F3 sector map from the hangar
}
```

- `ShipLoadout.Active` (the input gate) **stays true** while hidden, so flight input remains
  neutralized — correct, we don't want ship control from the hangar.
- The Hud's auto-open loop (`Hud.cs:268-279`) sees the hangar instance is still valid
  (`hangarUp == true`) and only re-sets `OpenedForSpawn = true`; it does **not** recreate it, so
  our hidden state sticks. On F3 close, `_Process` flips `Visible` back to `true`.
- A hidden Control still runs `_Process` in Godot, so the toggle keeps working both directions.
- Mouse cursor stays `Visible` on close (SectorOverview only recaptures when `LocalShip != null`,
  which is null while docked) — correct for the hangar UI.

**2b. Let `Open()` proceed with no local ship.** `SectorOverview.Open()` already falls back to
`_world.ViewSectorCenter` when `LocalShip == null` (line 217) and `Close()` is null-ship-aware, so
the only blocker is the `radius <= 0f` early-return (line 210), where `radius =
_world.LocalSectorRadius`. While docked at your home base you remain subscribed to that sector, so
this is expected to be `> 0` and F3 should just work once 2a lands. **Verify this first** (see
below). If `LocalSectorRadius` is `0` while docked, relax the guard to fall back to
`_world.ViewSectorRadius` (and `SetViewSector(null)` still targets the local sector), keeping the
`return` only when *both* are `<= 0`.

## Critical files

- `client/scripts/SectorOverview.cs` — F3 owner; `Open()` guard (feature 2b, only if needed).
- `client/scripts/ui/ShipLoadout.cs` — add `_Process` (feature 2a). Already modified in git status.
- `client/scripts/TargetMarkers.cs` — guard the reticule block (feature 1).
- `client/scripts/SystemRing.cs`, `WeaponsPanel.cs`, `VelocityIndicator.cs`, `Hud.cs` — `Visible`
  gates (feature 1).

## Verification

The dotnet test suites don't cover the Godot client (per project notes), so smoke-test in-app:

1. **F3 combat-HUD hide (flying):** launch with `client/run-client.sh` (or `--autofly` against a
   local `--server`), spawn a ship, press F3. Confirm the reticule/lead crosshair, hull+shield
   ring, weapons panel, velocity marker, and top-left ping/reconcile telemetry disappear, while
   entity brackets, nameplates, altitude stems, and the minimap remain. Press F3 again → the full
   flight HUD returns. Confirm fog-off / normal flight is visually unchanged with F3 closed.
2. **F3 while docked:** dock (or die) to return to the hangar, press F3. Confirm the hangar overlay
   hides and the orbiting sector map (grid + stems + your base) is visible and drag/zoom works;
   press F3 again → the hangar returns unchanged and LAUNCH still works.
3. **Regression:** verify `LocalSectorRadius > 0` while docked (step 2 opening at all confirms it);
   if F3 no-ops in the hangar, apply feature 2b. Check the minimap click-to-retarget still works in
   F3 (feature 1 keeps the minimap).
