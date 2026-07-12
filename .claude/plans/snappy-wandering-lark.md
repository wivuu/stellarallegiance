# F3 Multi-Select (Box + Shift-Click) for Commander Orders

## Context

The F3 sector map (proto 34 commander orders) lets a commander left-click one friendly unit (`SectorOverview.SelectedId`) and right-click a target to issue a `MsgOrder`. Commanding a group currently means select → order, one unit at a time. This change adds multi-select: **default left-drag draws a selection box** that selects every friendly unit inside it, Shift+left-click toggles individual units, and one right-click issues the order to every selected unit. Left-drag's current orbit behavior moves to **Shift+left-drag**.

**Client-only, no protocol change**: the wire (`MsgOrder=12`, 34 bytes) carries one subject per frame, so the client fans out one `GameNetClient.SendOrder` call per selected unit. Server (`ClientHub.HandleOrder` → `Simulation.EnqueueCommandOrder`) validates the commander gate per-message and stores orders in a per-ship dictionary — it already handles N independent subjects. `SelectedId` has no readers outside `SectorOverview.cs` (verified), so the selection state can be refactored freely.

## Interaction rules

| Input | Behavior |
|---|---|
| **Left-drag (no shift)** | **Rubber-band selection box** (was: orbit). On release, selection = all friendly non-local ships whose screen positions fall inside the rect (replace semantics; empty box = clear selection) |
| **Shift+left-drag** | **Orbit/rotate** (was: pan) |
| Middle-drag / right-drag | Pan (unchanged) |
| Plain left-click (release < 5px) on entity | Replace selection with it (unchanged, incl. focus/waypoint side effects) |
| Plain left-click on empty grid | Deselect all + waypoint (unchanged) |
| Shift+left-click on friendly non-local ship | Toggle it in/out of the selection; does NOT touch TargetMarkers focus/waypoint. Adding displaces any non-commandable lone entry (base/rock/enemy) |
| Shift+left-click on base/rock/enemy/empty | No-op (a mis-click while building a group doesn't destroy it) |
| Right-click target with commandable selection | One `SendOrder` per selected id (attack/goto/mine inferred server-side, as today) |
| Right-click a ship that IS in the selection | Release ALL selected (targetKind 255) — symmetric with orders-go-to-all |
| Right-click empty grid | Goto-point order to each selected id |
| Entity despawns / F3 close | Per-id auto-prune / clear selection |
| Minimap click | Precedence unchanged (no box/orbit starts from a minimap press) |
| Trackpad gestures (pinch/two-finger) | Unchanged |

Out of scope: server-side aggregation of the N gold CMDR chat directive lines N orders produce (accept for now).

## Changes

All feature logic in `client/scripts/SectorOverview.cs`:

1. **Selection state** (line 36): replace the auto-property with an ordered list + derived property; update the doc comment (lines 30-35):
   ```csharp
   private static readonly List<ulong> _selection = new();
   public static ulong SelectedId => _selection.Count == 0 ? 0 : _selection[^1];
   ```
   Helpers: `SelectOnly(ulong)` (clear+add; replaces the `SelectedId = x` assignments at lines 231, 380, 564), `ToggleSelect(ulong)` (remove if present, else drop non-commandable entries and add), `CommandableSelection()` (prune dead ids, return commandable subset), `SelectMany(IEnumerable<ulong>)` (clear + add all — box-select result).
2. **Resolver** (line 238): generalize `TryResolveSelected` → `TryResolveEntity(ulong encoded, out Vector3 pos, out bool commandable)` (same body, parameterized); keep a thin `TryResolveSelected` wrapper.
3. **Drag rewire + box select** (`_Input`, lines 454-493):
   - Left press (not minimap): `_orbitDrag = mb.ShiftPressed;` and when not shift, arm box-select (`_boxSelecting = true`, anchor = press pos). Drop `_panDrag` from the left branch entirely (pan stays on middle/right).
   - Mouse motion: if `_boxSelecting` and moved past `ClickMovePx`, update the box rect + redraw overlay (before that threshold nothing is drawn, so a plain click never flashes a box).
   - Left release: if `_boxSelecting` and moved ≥ `ClickMovePx` → finalize: rect from anchor→release, iterate `_world.FriendlyShips()` in the viewed sector, `UnprojectPosition` each (skip `IsPositionBehind`), `SelectMany` of those inside the rect; hide overlay. If moved < `ClickMovePx` → existing click path `HandleMapClick(pos, engage:false, additive: mb.ShiftPressed)`.
   - Box overlay: a second passive full-rect `Control` ("SelectionBox") on `_hudLayer` like `_selMarker` (creation pattern at lines 154-160), drawing an outlined + translucently-filled rect in `DesignTokens.CmdrGold`.
4. **`HandleMapClick`** (line 555): add `bool additive = false`.
   - `!engage && additive`: if picked and it resolves commandable → `ToggleSelect(encoded); return;` else `return;` (no focus/waypoint changes).
   - `engage` branch: replace the single-subject gate (line 579) with `var subjects = CommandableSelection(); if (subjects.Count > 0)`. Resolve the click's `(kind, id)` once (existing flag-strip ternary, lines 590-593), then `foreach (var subject in subjects) _net.SendOrder(subject, …)`. Release rule: picked raw ship contained in `_selection` → send targetKind 255 to every subject.
5. **Marker rendering**: replace `_selMarkerPos`/`_selMarkerColor` (lines 74-75) with `List<(Vector2 pos, Color col)> _selMarkerDraws`. `UpdateSelectionMarker` (line 219) iterates `_selection` backwards, prunes unresolvable ids, skips behind-camera, colors `DesignTokens.CmdrGold` (commandable) / `TeamAccent`. `DrawSelectionMarker` (line 163) wraps the 4-bracket body in a `foreach` — single selection renders pixel-identically.
6. **`Close`** (line 380): `_selection.Clear()`; hide box overlay.
7. **Hint text** (line ~148): update to reflect `drag box-select · shift-drag rotate · shift-click add`.

Test — `tests/CommanderTest/Program.cs`: one new scenario using existing helpers (`BootSim`, `SpawnPlayer`, `WaitForPig`, `StepQuiet`): wait for two pigs, `EnqueueCommandOrder` a point order for each, assert `PigOrdersView()` holds both ShipIds; then targetKind 255 for both, assert empty. (Covers the sim-visible property: independent per-subject orders coexist.)

Unchanged (reference only): `client/scripts/GameNetClient.cs:414` `SendOrder`, `server/Net/ClientHub.cs:912` `HandleOrder`, `server/Sim/Simulation.Orders.cs`. Don't touch the unrelated pending `client/scripts/Lobby.cs` modification.

## Verification

1. `dotnet build` the client project — compiles.
2. `dotnet run --project tests/CommanderTest` — existing 9 scenarios + new multi-subject scenario green.
3. Manual smoke (run-server.sh + run-client.sh, as commander with pigs): F3 → left-drag a box around two friendly pigs (two gold bracket sets; camera does NOT rotate) → shift+left-drag rotates the view → right-click enemy (both attack, two gold CMDR chat lines) → right-click empty grid (both fly there) → right-click a selected pig (all released) → shift-click toggles one unit in/out → plain click collapses to single, click-away clears → middle/right-drag still pans → F3 reopen clears. Optionally the `verify` skill for headless screenshot evidence.
