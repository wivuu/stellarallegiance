# Control settings & mappings

## Context

The roadmap item **[M] Control settings and mappings** (`.PLAN/README.md`, Stage 3) asks that
players configure keybindings, input devices, and control schemes from **Settings → Controls**.

Today there is nothing to configure: the client uses **zero Godot InputMap actions**. Every
gameplay control is a hardcoded `Input.IsPhysicalKeyPressed(Key.X)` poll or a raw `InputEvent`
pattern-match, spread across ~15 files. `project.godot` has no `[input]` section at all. The only
"controls" settings that exist are the mouse **sensitivity** slider and **invert-Y** toggle on the
`SettingsDialog` CONTROLS tab. There is no joypad/gamepad support anywhere.

**Decisions (confirmed with the user):**
1. **Migrate to Godot InputMap** as the indirection layer (native serialization + analog joypad).
2. **Support gamepad/joystick** in addition to keyboard + mouse.
3. **Rebindable set = flight + combat + scope/view only.** Menu/system keys (Esc, F3 map, F4
   hangar, F9 showcase, base-select 1–9, spawn hotkeys 1/2/3) stay as today's hardcoded
   pattern-matches — rebinding them risks breaking the delicate modal input-gating.
4. **Per-action rebind + RESTORE DEFAULTS** (no named preset schemes).

Outcome: a player opens Settings → Controls, sees the flight/combat actions with their current
bindings, clicks a row to capture a new key / mouse button / joypad button-or-axis, and it persists
across sessions and applies live. Sensitivity + invert-Y stay on the same page.

## Architecture

Introduce a client-only keybinding indirection. **No server, wire, or sim change** — bindings are
purely how the client reads local input; `AutoInput()` (headless/autofly) bypasses input entirely,
so determinism and the dotnet test suites are unaffected.

- **Defaults live in `project.godot` `[input]`** — a new section authored with every rebindable
  action and its default event(s). Keyboard defaults use `physical_keycode` (not `keycode`) so WASD
  stays positional on AZERTY/etc., matching today's `IsPhysicalKeyPressed`. Each action also gets a
  sensible default **joypad** binding (see action table). The engine loads these into `InputMap` at
  boot; `InputMap.LoadFromProjectSettings()` is the reset-to-defaults primitive.
- **Overrides persist in `UserPrefs`** — a new `[bindings]` section in `user://settings.cfg`, one
  key per action storing a `Godot.Collections.Array` of compactly-encoded events, written **only
  when an action differs from its project default**. Mirrors the existing write-through-on-set +
  `Changed` event pattern (`UserPrefs.cs`).
- **A new `InputBindings` static helper** (`client/scripts/InputBindings.cs`) owns the action
  catalog, encode/decode, apply-at-boot, rebind, and reset. It is the single place the UI and the
  boot seam talk to.

### Rebindable action catalog

Paired flight axes use `Input.GetAxis(negativeAction, positiveAction)` (analog joypad for free);
signs preserved from today's `ShipController.Axis(pos, neg)` conventions.

| Category | Actions | Default key | Default joypad (tunable starter) |
|---|---|---|---|
| Flight | `thrust_forward` / `thrust_back` | W / S | R2 trigger / L2 trigger |
| Flight | `strafe_right` / `strafe_left` | A / D | D-pad →/← |
| Flight | `strafe_up` / `strafe_down` | X / Z | — |
| Flight | `yaw_left` / `yaw_right` | Left / Right | Left-stick X− / X+ |
| Flight | `pitch_up` / `pitch_down` | Up / Down | Left-stick Y− / Y+ |
| Flight | `roll_right` / `roll_left` | E / Q | Right-stick X+ / X− |
| Combat | `fire_primary` | Space | R1 shoulder |
| Combat | `fire_secondary` | F | L1 shoulder |
| Combat | `afterburner` | Shift | A / cross |
| Combat | `drop_chaff` | C | B / circle |
| Combat | `drop_mine` | B | X / square |
| Combat | `drop_probe` | G | Y / triangle |
| Combat | `cycle_target` | Tab | Right-stick click |
| View | `toggle_view` | V | D-pad ↑ |
| View | `scope_zoom_in` / `scope_zoom_out` | = / - | D-pad ↑/↓ |

Joypad defaults are a starter mapping, easy to retune in `project.godot` later.

**Mouse look is NOT an InputMap action** and stays bespoke: relative-motion accumulation +
sensitivity/invert live in `ShipController._Input`/`ReadInput` as today, driven by the existing
`UserPrefs.MouseSensMultiplier` / `MouseInvertY`. Likewise the **LMB/RMB fire convenience** stays a
special case gated on mouse-capture (`look`) so a menu click never fires — only the keyboard/joypad
half of `fire_primary`/`fire_secondary` becomes rebindable.

## Implementation

### 1. Default bindings — single-sourced in C# (not `project.godot`)
**Refinement during implementation:** rather than hand-author fragile `physical_keycode`
magic-number literals in `project.godot` `[input]`, the defaults are built in `InputBindings.cs`
from the `Key`/`JoyButton`/`JoyAxis` enums (`BuildDefaults`) and registered into the `InputMap` at
runtime (`InputMap.AddAction` + `ActionAddEvent`) with a 0.2 deadzone. Keyboard events set
`PhysicalKeycode` (layout-independent, matching the old `IsPhysicalKeyPressed`). `ResetAll`
re-registers from this table instead of `LoadFromProjectSettings`. This keeps defaults in one
type-checked place and matches the code-first style of the project — `project.godot` is untouched.

### 2. `client/scripts/InputBindings.cs` (new, static, mirrors `UserPrefs` style)
- **Catalog**: ordered list of `(action, displayName, category)` records driving both the UI and
  which actions get persisted. Group = Flight / Combat / View.
- **`Apply()`**: boot-time — for each action with a `[bindings]` override, `InputMap.ActionEraseEvents(action)` then add decoded events; actions with no override keep project defaults.
- **`Rebind(action, InputEvent ev)`**: replace the primary same-device event on the action (erase existing key/mouse OR joypad event of that class, add `ev`), persist encoded events to `UserPrefs`, fire `UserPrefs.Changed`. Return any conflicting action so the UI can warn/clear.
- **`ResetToDefault(action)` / `ResetAll()`**: clear the override(s); `ResetAll` calls `InputMap.LoadFromProjectSettings()` then re-persists nothing (defaults = no override rows).
- **`Describe(action)` → short label** ("W", "SPACE", "MOUSE 4", "PAD A", "PAD LS→") for the UI.
- **Encode/decode** for ConfigFile: `"k:<physical_keycode>"`, `"m:<button>"`, `"jb:<button>"`, `"ja:<axis>:<±1>"` → `InputEventKey{PhysicalKeycode}` / `InputEventMouseButton` / `InputEventJoypadButton` / `InputEventJoypadMotion`.
- **`SnapshotOverrides()` / `RestoreOverrides(snap)`**: for the dialog's Cancel/revert.

### 3. `client/scripts/UserPrefs.cs` — add a `[bindings]` section
Add `BindingsSection` const + `GetBindingRaw(action)` / `SetBindingRaw(action, Godot.Collections.Array)` / `ClearBinding(action)` / `ClearAllBindings()` following the exact
`SetValue → Cfg.Save(Path) → Changed?.Invoke()` idiom already used for audio/sens. `InputBindings`
is the only caller; keep event encoding in `InputBindings`, not here.

### 4. Boot seam — call `InputBindings.Apply()` once at startup
Add `InputBindings.Apply();` right beside `UserPrefs.ApplyAudioPrefs();` in
`client/scripts/SfxManager.cs` `_Ready` (the existing "apply persisted prefs at boot" main-thread
seam). Bindings must be in `InputMap` before the first frame reads input.

### 5. Migrate the call sites (flight + combat + scope/view only)
- **`client/scripts/ShipController.cs` `ReadInput`** (L523–573): replace each `Axis(Key.pos, Key.neg)` with `Input.GetAxis("<neg_action>", "<pos_action>")` preserving current signs; `Boost` (L343) → `Input.IsActionPressed("afterburner")`; `Firing`/`Firing2` → `Input.IsActionPressed("fire_primary"/"fire_secondary") || (look && mouse-button)` (keep the LMB/RMB special case verbatim); `DropChaff/Mine/Probe` → `IsActionPressed("drop_*")`. Delete the now-unused private `Axis(Key,Key)` helper. Yaw/Pitch keep the `+ _stickYaw/_stickPitch` mouse addition.
- **`client/scripts/Hud.cs`** cosmetic empty-rack echo (L323–351): swap the same `Key.F/C/B/G` reads to the matching actions so it stays in lock-step with `ShipController`.
- **`client/scripts/TargetMarkers.cs`** Tab cycle (L194): `event.IsActionPressed("cycle_target")` (keep the `!Chat.Capturing` gate; Chat still owns Tab while typing).
- **`client/scripts/CameraRig.cs`** V toggle (L102): `event.IsActionPressed("toggle_view")`.
- **`client/scripts/ZoomView.cs`** `=`/`-` (L150/158): `event.IsActionPressed("scope_zoom_in"/"scope_zoom_out")`.
- Keep the event-driven handlers as `_Input`/`event.IsActionPressed(...)` (not polling) so the existing modal `!Chat.Capturing && !SectorOverview.Active && … && !SettingsDialog.Active` gates are untouched.
- **Left hardcoded (out of scope):** all Esc handling, `SectorOverview` F3 + its own `=/-` pan/zoom, `Hud` F9/F4, `ShipLoadout` 1–9, `ShipController` spawn hotkeys 1/2/3, `Chat` Enter/Tab.

### 6. UI — extend the CONTROLS tab
- **New component `client/scripts/ui/KeybindRow.cs`** (per DESIGN.md: tokens only, no `[GlobalClass]`, rendered in `UiShowcase`): a row = `UiKit.MakeLabel(display, Label)` on the left, expanding spacer, and a `ChamferButton` (Ghost variant) showing `InputBindings.Describe(action)`. Clicking arms capture: button text → "PRESS…", raises a `CaptureRequested` signal/callback.
- **`client/scripts/ui/SettingsDialog.cs` `BuildControlsPage()`** (L217): keep the sensitivity slider + invert-Y row, then add a `HairlinePanel`-grouped list of `KeybindRow`s under "FLIGHT" / "COMBAT" / "VIEW" headers (`DiamondDivider` between groups). The page is already inside the body `ScrollContainer`, so it scrolls.
- **Capture handling in the dialog**: a `_capturingRow` field; while set, the dialog's `_Input` routes the next `InputEventKey` (non-modifier) / `InputEventMouseButton` / `InputEventJoypadButton` / `InputEventJoypadMotion` (past deadzone) to `InputBindings.Rebind`, consumes it (`GetViewport().SetInputAsHandled()`), refreshes the row label, and exits capture. **Esc while capturing cancels the capture, not the dialog** — check `_capturingRow` before the existing Esc→`Cancel()` in `_Input` (L63). On conflict, clear the other action's binding and refresh its row too.
- **Snapshot/revert + defaults** (mirror the existing pattern, L51–61 / L314–335): snapshot `InputBindings.SnapshotOverrides()` in `_Ready`; `Cancel()` calls `RestoreOverrides` + refreshes rows; `RestoreDefaults()` additionally calls `InputBindings.ResetAll()` and refreshes all rows (alongside the existing audio/sens resets).
- **`client/scripts/ui/UiShowcase.cs`**: add a `KeybindRow` sample (armed + idle) so the F9 gallery covers it, per the DESIGN.md "render new components in the showcase" rule.

## Files

- New: `client/scripts/InputBindings.cs`, `client/scripts/ui/KeybindRow.cs`
- Edit: `client/project.godot` (add `[input]`), `client/scripts/UserPrefs.cs`, `client/scripts/SfxManager.cs`, `client/scripts/ShipController.cs`, `client/scripts/Hud.cs`, `client/scripts/TargetMarkers.cs`, `client/scripts/CameraRig.cs`, `client/scripts/ZoomView.cs`, `client/scripts/ui/SettingsDialog.cs`, `client/scripts/ui/UiShowcase.cs`
- Docs: note the new `KeybindRow` in `DESIGN.md` component list.

## Verification

1. **Build**: `dotnet build client` — no errors.
2. **Component render**: launch `--ui-showcase` (or F9 in-game); confirm `KeybindRow` renders in idle + "PRESS…" states within the design system.
3. **End-to-end (`verify` skill)**: headless server + Godot client. Open Settings → Controls, rebind e.g. `fire_primary` to a new key, confirm the row label updates and `user://settings.cfg` gains a `[bindings]` `fire_primary` row; relaunch and confirm it persists; RESTORE DEFAULTS clears it and the file row disappears. Fly with default bindings and confirm thrust/turn/fire still respond (defaults path).
4. **Gamepad**: with a controller attached, confirm a joypad button and a stick axis can be captured and drive flight (analog turn via `GetAxis`). Note: full joypad testing needs physical hardware — headless verifies the keyboard path + persistence + InputMap application.
5. **No regressions**: server `dotnet test` suites unchanged (client-only change; no wire/sim/def touch); Esc two-step, F3/F4/F9, base-select, and chat Tab still behave (untouched hardcoded handlers).

## Out of scope / deferred
- Named preset control schemes (Default/Southpaw/…) — per-action rebind + RESTORE DEFAULTS only.
- Rebinding menu/system keys (Esc, F3, F4, F9, spawn/base-select digits, chat).
- Rebindable mouse-look axis / mouse sensitivity curve (sensitivity + invert-Y already covered).
- Per-map / per-ship binding profiles.
