# Target Health Indicator (focused-target HP arc)

## Context

The imported Claude Design "Game HUD" spec (project `28bf0d21-…`, file `Game HUD.dc.html`,
read via the `claude_design` MCP `DesignSync`/`get_file`) draws a **health indicator around the
focused target**: a bottom-left quarter arc that drains and shifts green→amber→red as the target
takes damage, sitting alongside the existing MISSILE-LOCK banner, LEAD crosshair, target bracket
and label. In the design's `mark(c)` view-model this is the `showHp` block — a dim track quarter
(6 o'clock→9 o'clock) plus a colored fill proportional to `c.hp`.

The game's combat HUD already has the bracket, lock-progress arc, MISSILE LOCK banner, LEAD
crosshair and "▣ TARGET" label — all in `client/scripts/TargetMarkers.cs`. **What's missing is the
health arc, and the data behind it.** Target ship health/shields never reach the client today:
`RemoteShip` reads pos/rot/vel/class/flags off each snapshot row and drops `Health`/`Shield`, even
though the wire `Ship` row already carries them (`NetTypes.cs:59-60`, decoded in
`GameNetClient.cs`). So no protocol change is needed — we only need to *consume* fields that are
already streamed.

Outcome: when you Tab-focus an enemy, a hull HP arc (plus a thin cyan shield band on shielded
hulls) wraps the target bracket and reads its condition at a glance — matching the design spec and
staying consistent with the local ship's `SystemRing` gauge.

**Decisions (confirmed with user):**
- Arc content = **hull arc + a thin outer shield band** (like `SystemRing`), not hull-only.
- Focus label = **left as-is** (`▣ TARGET` + range in `u`); this change adds only the arc.

## Part 1 — Plumb target health onto `RemoteShip`

File: `client/scripts/RemoteShip.cs`

Add four read-only properties next to the existing `Class`/`IsPig`/`IsPod`/`Velocity`
(`RemoteShip.cs:71-93`):

```csharp
public float Health { get; private set; }
public float Shield { get; private set; }
public float MaxHealth { get; private set; }   // 0 until the def resolves
public float MaxShield { get; private set; }    // 0 = hull carries no shield
```

- **Maxes** — resolve once in `Initialize` (`RemoteShip.cs:145`), the same place `_maxSpeed`
  is resolved. Mirror how the local ship gets them in `PredictionController.cs:139-147`
  (`def.MaxHull`, `def.ShieldCapacity`). Use the `DefRegistry` accessor that exposes hull/shield
  capacity for the class (`TryGetShipDef`, per `GameNetClient.cs:1143-1144`); if the existing
  `defs.TryGetStats(...)` struct already carries them, reuse that call instead of a second lookup.
  A missing def just leaves the harmless `0` default (no baked client tuning — see the
  `client-no-baked-tuning-fallback` convention): the arc simply won't draw until a def lands.
- **Current values** — set `Health = row.Health; Shield = row.Shield;` in `Push`
  (`RemoteShip.cs:182`), where pos/vel are already unpacked from the row. Latest-value assignment
  is fine (no interpolation needed for a HUD arc); the stale-frame guard at `Push` top already
  drops out-of-order packets.

## Part 2 — Draw the arc in `TargetMarkers.cs`

File: `client/scripts/TargetMarkers.cs`

**Geometry (matches the spec's `mark(c)` HP block).** The design uses `pol(deg)` with 0°=top and
clockwise degrees; the HP quarter runs `hpStart=180` (6 o'clock) → `hpEnd=270` (9 o'clock), filled
from `hpStart` proportional to hull fraction. Godot `DrawArc` uses 0°=+X (right), positive =
clockwise (screen-Y down), so **design-deg → Godot-angle = deg − 90**:
- Track (full quarter): `DrawArc(sp, r, deg2rad(90), deg2rad(180), 24, track, w, true)`
- Hull fill (from 6 o'clock): `DrawArc(sp, r, deg2rad(90), deg2rad(90 + 90*hullFrac), 24, col, w, true)`

Add `DrawTargetHealthArc(RemoteShip ship)` near `DrawLockArc` (`TargetMarkers.cs:566`). It should:
1. Skip if `ship.MaxHealth <= 0` (def not yet loaded) or the target is behind the camera /
   off-screen (reuse the `Cam.IsPositionBehind` + viewport-rect guard from `DrawFocusTag`,
   `TargetMarkers.cs:880-885`).
2. Compute `hullFrac = clamp(Health/MaxHealth)` and, for shielded hulls (`MaxShield > 0`),
   `shieldFrac = clamp(Shield/MaxShield)`. Only draw the indicator when the target is **damaged**
   (`hullFrac < 1` or `shieldFrac < 1`), so a pristine target stays uncluttered (the spec hides it
   at full hull).
3. **Hull arc** at radius `FocusHalf + 6f` (just outside the bracket; the lock ring sits at
   `FocusHalf + 7f` and is a full ring, so the bottom-left quarter reads distinctly). Dim track =
   `DesignTokens.BorderLo`; fill color = the **tiered** ramp (`Ok`/`Warn`/`Danger`) to match the
   design's `#4dffa6/#ffb347/#ff5a6a` and the local gauge — reuse the tiered helper pattern from
   `SystemRing.cs:158-161` (add a small local `static Color HullColor(float)` rather than reusing
   `TargetMarkers.HealthColor` at :792, whose lerp ramp is intentionally kept for the base bar).
4. **Shield band** for `MaxShield > 0`: a thinner cyan (`DesignTokens.TeamAccent`) arc on the same
   quarter, one band outside the hull arc (radius `FocusHalf + 11f`, width ~2px vs the hull ~3px),
   filled from the 6 o'clock end — the same "solid outer band" idiom as `SystemRing.SolidArc`
   (`SystemRing.cs:127-136`). Cyan-as-chrome is correct here (shield charge), consistent with
   `SystemRing`.

**Wire it in.** In `_Draw`, the focused enemy is already resolved and its tag + lock arc drawn at
`TargetMarkers.cs:476-480`. Add the call right there:

```csharp
if (focusedShip != null)
{
    DrawFocusTag(view, focusedShip, local);
    DrawLockArc(focusedShip);
    DrawTargetHealthArc(focusedShip);   // NEW
}
```

Redraw is already driven every frame by `QueueRedraw()` in `_Process` (`:149`), so the arc tracks
damage live. No new tokens, fonts, or protocol fields.

## Notes / non-goals

- **No protocol bump.** `Ship.Health`/`Shield` are already on the wire and decoded; this is a
  client-side consume only. (`missiles-chaff-seams` memory: a protocol bump would need an
  `--autofly` smoke — not triggered here.)
- Pods are excluded from the enemy target set (`RemoteShip.IsPod`), so they never focus and never
  draw an arc — no special-casing needed.
- Focused **bases** keep their existing `DrawBaseHealthBar` (`TargetMarkers.cs:701`); this arc is
  ship-only (`DrawTargetHealthArc` takes a `RemoteShip`).
- `TargetMarkers` is a live-HUD overlay, not a reusable `client/scripts/ui/` component, so it is
  not part of the `UiShowcase` gallery — verification is in the running client, not F9.

## Verification

1. Build: `godot --headless --import` (cold import cache) then a normal client build; confirm no
   C# errors.
2. Need an enemy to focus and damage. Start a headless server with AI drones (per the
   `headless-sim-testing` memory: hold a `--server --anonymous` connection so the sim ticks), or
   run a second client. Connect a client, fly within radar range of a hostile PIG.
3. **Tab** to focus a hostile. Fire on it and confirm:
   - the bottom-left quarter hull arc appears once it takes damage and **drains toward 9 o'clock**
     as hull drops, shifting green→amber→red across the 0.5 / 0.25 thresholds;
   - on a shielded hull, the thin cyan band outside the hull arc drops first, then recovers as
     shields regen (the `shields-damage-system` seam);
   - a full-health target shows **no** arc; the arc hides again when the target de-focuses or
     leaves the screen.
4. Cross-check against the local `SystemRing` gauge for the same ship class so the target and
   local readouts agree on color tiers.
5. Optional one-frame capture for the record: focus a damaged target, then compare the arc's
   geometry/colors against the design spec's HP block.
