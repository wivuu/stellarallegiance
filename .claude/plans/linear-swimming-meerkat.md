# Flight right-click orders (own-ship autopilot)

## Context

Today, ordering your own ship to a target by clicking only works inside the **F3 sector
overview** (`SectorOverview` right-click → focus/waypoint + `EngageAutopilot`). During normal
flight there is no click-to-order at all: targeting is Tab-cycle only, and once you press Esc to
free the cursor the only mouse actions are left-click (recapture) and a second Esc (escape menu).

We want a lightweight, in-cockpit version of the F3 order gesture: **while flying, not in F3, and
after pressing Esc once to free the cursor, a right-click orders your own ship to fly to whatever
was clicked** — a ship / base / asteroid under the cursor, or a sector node on the minimap. This is
own-ship-only (autopilot), *not* the commander fan-out to teammates (that stays F3-exclusive).

Per the scope decision: **empty-space right-clicks are ignored** — only an entity or a minimap
sector node issues an order (no grid-plane waypoint drop in flight).

## Approach

`SectorOverview` is the right home for the handler even though the gesture happens outside F3,
because it already holds every dependency: the flight chase camera (`_chaseCam` =
`../Camera3D`), the minimap ref (`_minimap`), `WorldRenderer`, `ShipController`, and the reusable
recipes. Its `_Process` already runs while inactive (it polls the F3 toggle), so the node is live in
flight. `ShipController` is a poor fit — it has no camera/minimap/pick logic and would duplicate all
of it. No new node.

Reused as-is (no signature change):
- `SectorOverview.ApplyOwnShipRightClick(picked: true, encoded, point)` — for a real pick it does
  exactly `TargetMarkers.SetFocus(encoded)` + `ClearWaypoint()` + `_shipController.EngageAutopilot()`.
  The camera-dependent grid branch is only hit when `picked == false`, which the flight path never
  passes. (`SectorOverview.cs:873`)
- `SectorOverview.OrderOwnShipToSector(sector)` — minimap recipe: cross-sector NAV waypoint +
  `EngageAutopilot`. (`SectorOverview.cs:899`)
- `Minimap.TryClickSector(point, out sector)` — viewport-space hit-test, already used by F3.
  (`Minimap.cs:57`)
- `TargetMarkers` focus/waypoint statics + the existing in-flight HUD (NAV diamond, focus bracket,
  AUTOPILOT banner) render the feedback for free — no new drawing.

### Changes in `client/scripts/SectorOverview.cs`

1. **Thread the camera through `TryPickEntity`** (currently hardcodes the overview cam `_cam`,
   line 931). Change signature to `TryPickEntity(Camera3D cam, Vector2 point, out ulong encoded)`
   and replace every `_cam.` inside with `cam.`. Update the one F3 caller (line 801) to pass
   `_cam`. This is the only refactor to existing F3 code; behavior there is unchanged.

2. **Add a flight-context guard** (new property):
   ```
   private bool FlightCommandContext =>
       _world.LocalShip != null
       && Input.MouseMode == Input.MouseModeEnum.Visible   // cursor freed via Esc
       && !Chat.Capturing && !ShipLoadout.Active
       && !SettingsDialog.Active && !ZoomView.Active;
   ```
   (`!Active` and `!EscapeMenu.Active` are enforced at the call site in `_Input`.)

3. **Add `HandleFlightRightClick(Vector2 point)`**: minimap first (parity with F3 precedence), else
   pick an entity through the chase camera; own ship is excluded as a target:
   ```
   _minimap ??= GetNodeOrNull<Minimap>("../Hud/Minimap");
   if (_minimap != null && _minimap.TryClickSector(point, out uint sector)) {
       OrderOwnShipToSector(sector);          // NOT SwitchView — flight orders, doesn't retarget F3
       return;
   }
   ulong ownId = _world.LocalShip?.ShipId ?? 0;
   if (TryPickEntity(_chaseCam, point, out ulong encoded) && encoded != ownId)
       ApplyOwnShipRightClick(picked: true, encoded, point);   // focus + engage; empty ignored
   ```

4. **Extend `_Input`** (line 605). Replace the blanket `if (!Active || EscapeMenu.Active) return;`
   with:
   ```
   if (EscapeMenu.Active) return;
   if (!Active) {
       if (FlightCommandContext
           && @event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } mb) {
           HandleFlightRightClick(mb.Position);
           GetViewport().SetInputAsHandled();
       }
       return;
   }
   // ... existing active-map switch unchanged ...
   ```
   Acting on `Pressed` (no drag/click distinction) is safe here — RMB while the cursor is free is
   otherwise unused in flight (`ShipController` handles only left-click recapture + Esc, and
   `ReadInput` gates secondary fire behind `look == Captured`, so there is no collision).

No changes to `ShipController.cs` or `TargetMarkers.cs` — the existing static seams and
`EngageAutopilot()` (no-op unless launched) already do everything.

## Notes / edge cases
- Cursor stays free after ordering, so you can click multiple targets in a row; left-click still
  recaptures and hands back to manual flight (existing `ShipController` behavior). Autopilot
  disengages on manual override / T, unchanged.
- A right-click in the small minimap-panel corner that misses a node falls through to the entity
  pick; with empty clicks ignored it simply does nothing (no stray waypoint). Acceptable.
- Own ship is pickable (`TryLocalShip` resolves in flight since `ViewSector == LocalSector`) but is
  filtered out by `encoded != ownId`, so you never autopilot toward yourself.

## Verification
The dotnet suites don't cover the Godot client, so verify in the running client (manual, or via the
`verify` skill driving Godot headlessly):
1. Build/run the client against a host, pick a team, launch a ship.
2. Press **Esc once** to free the cursor (do not open the escape menu).
3. Right-click a teammate / enemy / friendly base / asteroid → the focus bracket appears on it and
   the **AUTOPILOT** banner engages; the ship flies to it.
4. Right-click an empty patch of space → nothing happens.
5. Right-click a **minimap sector node** → cross-sector autopilot engages (NAV waypoint, multi-hops
   gates and warps).
6. Left-click to recapture the cursor → manual mouse-look resumes; confirm right-click while
   captured still does NOT order (only fires secondary as before).
7. Confirm F3 right-click ordering still works unchanged (the `TryPickEntity` camera refactor).
