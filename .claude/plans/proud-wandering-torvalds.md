# Smooth sector-transition (warp flash + eased asteroid fade)

## Context

When the local player warps through an aleph gate into another sector — most noticeably a **large** sector visited for the first time — there's a visible "flash" of asteroids popping out of the old field and popping into the new one.

Root cause (confirmed in code, no destruction/respawn actually happens per transition):

- Asteroids are **static nodes created once** — from the `Welcome` frame and, for fog-hidden sectors, from `MsgReveal`. They are **never deleted per transition**; a sector change only toggles per-node visibility.
- On warp, `WorldRenderer.RefreshSectorVisibility()` (`client/scripts/WorldRenderer.cs:1238`) cross-fades the old sector's rocks out and the new sector's in over only **`FadeDur = 0.2f`** (`:923`), **linearly**, at the exact instant the camera hard-snaps across the warp discontinuity (`pc.OnAuthoritative(newRow, warped)`, `:1547`).
- For a large sector entered for the first time, its rocks don't exist yet — they arrive in one `MsgReveal` batch (`client/scripts/GameNetClient.cs:1179-1181` → `InsertAsteroid` → `SetNodeSectorFading` fade-in, `:974`) and all instantiate + fade in together over that same 0.2s, adding an instantiation hitch.

**Intended outcome:** a smooth "jump" transition. Chosen approach (user-selected): **Both** — a brief full-screen warp flash that masks the swap (and any first-reveal instantiation hitch), plus a longer, eased asteroid cross-fade behind it so anything visible at the flash edges still reads as smooth.

## Approach

### Part A — Warp-flash overlay (masks the swap)

New design-system component `client/scripts/ui/WarpFlash.cs`:

- A `CanvasLayer` (layer ~140 — above the gameplay HUD, **below** the `ConnectLayer`/modals at 150 so it never covers connection dialogs) containing a full-rect `ColorRect`.
- Size it with `SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect)` and set `MouseFilter = Ignore` so it never blocks input (per the SubViewport/overlay gotcha — always use the preset for code-built overlays).
- Color from the design tokens: a near-white/`DesignTokens.Void` core biased toward `DesignTokens.TeamAccent` (cyan, already faction-tinted at runtime) so the flash reads as the game's "jump" chrome, not a random white frame. Build from tokens, not hardcoded colors (see DESIGN.md).
- `Play()`: drive the `ColorRect`'s alpha with a `CreateTween()` (same idiom as `WorldRenderer.QuietFade`, `:1651`): `0 → ~0.85` over ~0.12s (ease-in), brief hold, `→ 0` over ~0.25s (ease-out). Total ~0.4s. Starts fully transparent; re-`Play()` mid-flash just restarts the tween.
- Keep v1 a flat color bloom — no shader/streak texture (avoid over-engineering; can add later).

### Part B — Eased, longer asteroid cross-fade (behind the flash)

In `client/scripts/WorldRenderer.cs`:

- Bump `FadeDur` (`:923`) from `0.2f` to ~`0.55f`.
- Ease the ramp. `AdvanceFades` (`:984`) currently advances `f.Curr` linearly with `Mathf.MoveToward` and applies `Mathf.Lerp(1f, RestTransparencyFor(n), f.Curr)` (`:999-1000`). Keep `f.Curr` as the **linear** progress (so the `MoveToward`/retire logic at `:1001-1008` is unchanged), but apply a smoothstep to the value fed into the transparency lerp: `float shown = Ease(f.Curr);` then `DimNode(n, Mathf.Lerp(1f, RestTransparencyFor(n), shown));`. Add a tiny private helper `static float Ease(float t) => t * t * (3f - 2f * t);`.
- This also smooths the F3 sector-overview view change (which shares `RefreshSectorVisibility`) for free, with no extra work.

### Wiring (no `.tscn` edits, matches existing patterns)

- `WorldRenderer`: add `public event Action? Warped;`. Fire it in `UpdateShip` inside the existing `if (warped)` block (`client/scripts/WorldRenderer.cs:1549`), right where `_localSector`/`ApplySectorEnv`/`RefreshSectorVisibility` are already updated. **Do not** fire it from the `InsertShip` local-spawn path (`:1499`) — first spawn / respawn is not an aleph warp and already has its own camera handling (respawn flash is out of scope for v1).
- `Hud._Ready` (`client/scripts/Hud.cs:53`): construct the `WarpFlash`, `AddChild` it, and subscribe `_world.Warped += _warpFlash.Play;`. `Hud` already holds `_world` (`:64`) and already builds all its overlays in code, so this is the established pattern — no Main.tscn changes.

## Files to modify

- **New:** `client/scripts/ui/WarpFlash.cs` — the flash overlay component.
- `client/scripts/WorldRenderer.cs` — `Warped` event + fire it on warp (`~:1549`); `FadeDur` bump (`:923`); `Ease` helper + eased `DimNode` call in `AdvanceFades` (`:999-1000`).
- `client/scripts/Hud.cs` — create/own the `WarpFlash`, subscribe to `Warped` (`_Ready`, `~:53-64`).

## Reuse / references

- Tween idiom: `WorldRenderer.QuietFade` / `FadeMeshes` (`client/scripts/WorldRenderer.cs:1651-1673`).
- Full-rect code-built overlay: `SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)` (SubViewport/overlay gotcha note).
- Colors: `DesignTokens.TeamAccent` / `DesignTokens.Void` (`client/scripts/ui/DesignTokens.cs:16,30`); DESIGN.md for the design system.
- Existing fade seam being extended: `FadeNode` / `SetNodeSectorFading` / `AdvanceFades` (`WorldRenderer.cs:944,968,984`).

## Out of scope (noted, not doing)

- Pre-warming/staggering `InsertAsteroid` to eliminate the first-reveal instantiation hitch — the flash hides it; revisit only if profiling shows a real spike.
- On very high latency the `MsgReveal` batch can arrive a beat after the ~0.4s flash ends; Part B's longer eased fade-in softens late rocks. Acceptable for v1.
- Respawn-into-world flash.

## Verification

1. Build the client: `dotnet build client` (or the project's run script).
2. Run against a server with at least one large asteroid sector, e.g. the map picker / a stock map with a big field. Fly to an aleph gate and warp.
   - Headless sim testing needs a held `--server --anonymous` connection or the loop won't tick (see headless-sim-testing note); for this visual check run the real client (`--host` before `--`, per the CLI-flags split) and fly manually, or use `--autofly` to reach a gate.
3. Confirm: on warp a brief cyan/white flash covers the screen (~0.4s); no visible pop of rocks appearing/disappearing at the flash edges; the destination field is fully present when the flash clears — including the **first** visit to a large fog-hidden sector.
4. Confirm the flash does **not** fire when merely changing the F3 sector-overview view (no local warp), and that the F3 view change itself now cross-fades more gently.
5. Confirm the flash overlay never intercepts input and never draws over the connecting/modal dialogs (warp while a modal is open, if reachable).
6. Regression: normal flight (no warp) shows no flash; leaving the server / reconnect (`Reset()`) still clears cleanly.
