# Distance-based HUD contact-marker cap — plan + current state

User-requested feature ("set a max number of HUD contact markers, ideally based on distance").
Final phase of the approved main-thread optimization plan for the Godot client. Branch `stress_test`.

## Status: CODE IMPLEMENTED (compiles clean) — verification + commit NOT yet done

Plan mode was activated mid-execution, right before runtime verification. The two source
edits are already on disk (uncommitted) and the Release build succeeded with 0 warnings /
0 errors. What remains is runtime A/B measurement, screenshot evidence, and (conditionally)
one commit — all of which need plan mode to be exited.

## What is already implemented (on disk, uncommitted)

### `client/scripts/ShipController.cs`
- New static: `public static int MarkerCap = -1;` (sentinel: -1 = unset → TargetMarkers uses
  its authored defaults; 0 = uncapped; N>0 = N enemy markers / N*3/4 friendly).
- New CLI parse inside the existing `OS.GetCmdlineArgs()` loop (beside `--render-stats` /
  `--stress-fx=`): `--marker-cap=N` → `int.TryParse` (only sets when `mc >= 0`).

### `client/scripts/TargetMarkers.cs`
- New consts: `MaxEnemyMarkers = 16`, `MaxFriendlyMarkers = 12`.
- New reusable scratch: `private readonly List<(float D2, RemoteShip S)> _shipSort = new();`
  (same allocation-free idiom as `_nearRocks`; RemoteShip refs are stable nodes so caching
  them across the shared FriendlyShips()/EnemyShips() scratch is safe).
- `DrawShipsPass` reworked:
  - Anchor = `_world.Ships.LocalShip?.GlobalPosition ?? Cam.GlobalPosition` (design metric #3).
  - `capped = !f3 && cap != 0` — **F3 map stays fully uncapped**; `--marker-cap=0` disables.
  - `focusedFriendly` / `focusedShip` still resolved by scanning the FULL friendly/enemy lists
    (so a beyond-cap focused target keeps its focus tag + firing solution).
  - Capped path: copy `(D2, ship)` into `_shipSort`, `Sort` ascending by D2, draw nearest N.
  - **Focus exemption**: `DrawNearestCapped` appends the focused ship if it wasn't in the
    drawn N (`ReferenceEquals` check).
  - Uncapped path (F3, or `--marker-cap=0`, or pre-cap default when knob explicitly 0) draws
    every ship exactly as before, including F3 type captions.
- Two new private draw helpers `DrawFriendlyShip` / `DrawEnemyShip` (instance methods, not
  capturing local functions → no per-frame delegate allocation) holding the original per-ship
  draw body (glyph/bracket color, DrawEntity, F3 `DrawShipTypeLabel`).
- Tab targeting (`HandleFocusCycle`) and `Hud.ShipsInLocalSector` are untouched — cap is
  draw-only.

## Remaining steps (require exiting plan mode)

1. **Restart the stress server** (the prior one was SIGKILLed, exit 137):
   `SIM_MAX_RECORDS=200 pwsh scripts/run-server.ps1 -Local --autostart --stress-fighters 100`
   in background; wait for `:8090` LISTEN. (100 fighters spawn FRIENDLY to the client →
   exercises the friendly cap of 12.)
2. **Build note**: Release DLLs were already copied into `client/.godot/mono/temp/bin/Debug/`
   (Godot loads Debug). Re-verify `[build-config] managed=RELEASE` in the client log each run.
3. **A/B in one build via the knob** (no stashing):
   - Capped (default, no flag → 16/12): `<godot> --path client/ --host localhost:8090 --autofly
     --stress-fx=full -- --ui-shot=<scratch>/cap_capped.png --ui-shot-delay=22`
   - Uncapped: same but `--marker-cap=0` (before `--`) and `cap_uncapped.png`.
   - 2–3 runs each; compare **median `mk_draw`** (expect 1.3 → ≤0.6 capped w/ ~100 friendlies);
     watch `mk_proc` and that no other bucket regresses >0.5 ms.
   - No `timeout` binary on this macOS — run Godot in background and poll for the PNG (the
     `--ui-shot` path should auto-quit), then reap the process.
4. **Screenshot evidence**: read both PNGs — capped must show ≤12 friendly glyphs (+ focus
   exemption), uncapped shows ~100.
5. **Code-review confirmation** (harness fleet is all-friendly): enemy-cap path by symmetry;
   F3-uncapped and beyond-cap focus exemption by reading `DrawShipsPass` / `DrawNearestCapped`.
6. **Smoke**: one run WITHOUT `--stress-fx`/`--render-stats`; grep `SCRIPT ERROR|Exception|
   Unhandled` clean; HUD looks normal in the shot.
7. **Commit** (ONLY if cap works in screenshots, `mk_draw` doesn't increase, no bucket regresses
   >0.5 ms, smoke clean): one coherent commit of both files, message describing caps / distance
   metric / focus exemption / F3-uncapped / `--marker-cap` knob + measured `mk_draw` delta, with
   the required `Co-Authored-By` + `Claude-Session` trailer. On failure: `git restore` both files
   (discard, never commit) and report why.
8. **Cleanup**: `kill $(lsof -tnP -iTCP:8090 -sTCP:LISTEN)`; `dotnet build
   client/stellarallegiance.csproj -c Debug` to restore Debug bits.

## Current disk state to be aware of
- `client/scripts/ShipController.cs`, `client/scripts/TargetMarkers.cs` — modified, uncommitted.
- `client/.godot/mono/temp/bin/Debug/*.dll` — currently the RELEASE build (copied for perf runs);
  step 8 restores the Debug build.
- No screenshots or perf data captured yet — verification is entirely pending.
