# Ship launch cinematic + stricter docking

## Context

Two related combat-feel fixes for the base launch/dock loop:

1. **Launch has no cinematic weight.** A ship reappears already fully framed in the player's
   chosen camera mode with zero flourish (`CameraRig.cs` explicitly snaps to the chosen mode on
   a fresh ship node — "you spawn already framed, you don't watch a transition play out on
   birth"). We're adding a deliberate 1.5s establishing shot — camera ahead of the nose, looking
   back — that then tweens into the normal chase/cockpit camera, for both a base launch and a
   pod-eject.
2. **Docking is too forgiving.** `Collide.IntersectsDockDisc` (the single shared kernel used by
   both the server's dock trigger and the client/server no-bounce carve-out at the bay mouth) uses
   a lateral capture radius of `discRadius(9) + shipRadius(3) = 12` world units per entrance
   hardpoint, with **no facing/velocity requirement**. Measured against the actual `base.glb`
   hardpoint geometry (`ws ≈ 5.58`), three of the five `HP_DockingEntrance_*` discs (E0/E3/E4) sit
   only ~12-13 world units apart center-to-center — closer than the sum of their capture radii —
   so they already blend into one oversized, roughly omnidirectional capture blob instead of 5
   distinct doorway openings. We're tightening this geometrically (no heading/velocity check, per
   discussion) so a dock requires actually passing through the visible door face.

Launching from the `HP_DockingExit_0` hardpoint is **already correct** today
(`Simulation.cs:1052-1104` `PlaceAtBase`, catapults from `World.BaseExitPos`/`BaseExitDir`) — no
change needed there.

## Feature A — Launch cinematic camera

**Trigger**: a ship node counts as "just launched" (cinematic plays) for every base
spawn/respawn AND every pod-eject; it must NOT play on a reconnect ship-reclaim.

- `WorldRenderer.cs`: add a field `private ulong? _reclaimedShipId;`. In `NetPromoteLocal`
  (~1628-1638), when the existing `if (_shipNodes.TryGetValue(...) && node is RemoteShip)` branch
  actually fires (a genuine reconnect reclaim of an already-mid-flight ship — this branch never
  fires for a brand-new ShipId), set `_reclaimedShipId = shipId`. In `InsertShip`'s `local` branch
  (~1585-1596), right after `pc.Initialize(row, _defs)`: if `_reclaimedShipId == row.ShipId`, clear
  it and skip; otherwise `pc.SetMeta("Launched", true)`. This is race-free — `MsgYouAre` is
  re-issued on every controlled-ship flip (`ClientHub.cs:953-962`), not just reconnect, so the flag
  must be scoped to the inner "found a stale RemoteShip" branch, not "NetPromoteLocal was called."

**Ship dimensions (length/width) — client-only, no protocol change**:

- `GlbLoader.cs`: add `public static Vector3 MeshWorldSize(Node3D root) => MeshAabb(root).Size;`
  exposing the existing private AABB walk.
- `ShipModelLoader.Build` (`ShipModelLoader.cs:58-74`): right after `root.AddChild(hull)`, measure
  `root` (not `hull` standalone — measuring `hull` alone misses the Scout placeholder's
  root-level `RotationDegrees`, which `MeshAabb`'s recursion only picks up via a parent's
  transform) and stash `root.SetMeta("ModelLength", size.Z)` / `root.SetMeta("ModelWidth", size.X)`
  (ship-forward is local +Z, so X is width). This covers the GLB and procedural-placeholder paths
  identically with no changes to `LoadHull`/`NormalizeLongestAxis`/`BuildPlaceholderMesh`, and
  covers pods for free (pod already resolves its own `model-name`/`model-length` via
  `DefId(cls, isPod)`).

**`CameraRig.cs`** — new constants (`LaunchCamHoldSec=1.5f`, `LaunchAheadLengthMult=4f`,
`LaunchRightWidthMult=2f`, a proposed `LaunchUpLengthMult=0.5f` for a mild vertical lift — no
vertical offset was specified, this roughly matches `ChaseOffset`'s own height/length ratio and is
a pure art-tuning knob — and `LaunchBlendOutSec=0.6f`, a softer/longer release than the 0.3s
mode-toggle dolly since this is a cinematic beat, not a UI response) and fields
(`_launchCamT`, `_launchBlendT`, `_launchFromPose`, cached `_launchLen`/`_launchWidth`).

In the fresh-ship-node branch (`CameraRig.cs:181-186`), still seed `_blend` to the target mode as
today (so the eventual blend-out lands exactly on the player's chosen framing), and additionally:
if `ship.HasMeta("Launched")`, resolve length/width off the `"ShipModel"` child's meta (mirroring
`ResolveCockpit`'s node lookup, with a defensive fallback matching `CockpitFallback`'s pattern) and
start `_launchCamT = LaunchCamHoldSec`.

Each frame while `_launchCamT > 0`: compute the launch pose from the ship's transform — camera at
ship-local `(width*2, len*0.5, len*4)`, looking at ship-local nose `(0,0,len*0.5)`, basis built via
`Basis.LookingAt(dir, shipUp)` (the ship's own up vector, **not** world up, matching this file's
existing "no world up in space" rule — and deliberately **not** applying `FaceForward`, since the
launch cam looks backward at the nose, the opposite facing from chase/cockpit) — and render that
pose directly, skipping the normal chase/cockpit offset and afterburner rumble entirely (it's an
external shot). When the hold expires, freeze that pose as `_launchFromPose` and blend to the
normally-computed chase/cockpit pose over `LaunchBlendOutSec` via `Transform3D.InterpolateWith`
with the same smoothstep easing already used for the mode-toggle blend.

**One real bug to fix while wiring this in**: `FirstPersonActive` (read by
`PredictionController.ApplyViewMode` to hide the own hull) is currently set unconditionally from
`_blend >= 0.98f`. Since `_blend` snaps to `1` immediately for a player whose preference is
first-person, on frame 1 of a launch the hull would hide while the external launch cam is trying to
show it. Gate it: `FirstPersonActive = !launchActive && _blend >= 0.98f`.

Confirmed by design review, no extra code needed: dying mid-hold falls through to the existing
`ship == null` → death-cam branch untouched (stale launch-cam fields are simply overwritten, not
incremented, on the next respawn); a combat-ship kill that flips `LocalShip` straight to a pod in
the same tick correctly re-triggers a **fresh** full-length cinematic for the pod (its own
"Launched" meta, its own dimensions) — back-to-back cinematics on death-into-pod is expected, not a
bug. Player input (V toggle / scroll wheel) stays live through the cinematic; it just has no visible
effect until the blend-out picks up whatever mode was most recently chosen.

## Feature B — Dock trigger geometric tightening

Single lever, no heading/velocity check: `CollisionConfig.DockDiscRadius` (currently `9f`,
`shared/Collision/CollisionConfig.cs:15`), the sole source of truth shared by the server dock
trigger (`Simulation.cs:694-722`) and the client/server no-bounce carve-out at the bay mouth
(`Collide.ResolveStatics`/`Touches`) — both call the same `Collide.IntersectsDockDisc`.

Change in lockstep:
1. `shared/Collision/CollisionConfig.cs:15` — new `DockDiscRadius` value `R`.
2. `tools/collision-hull/bake.py:76` — `WORLD_DOCK_DISC_RADIUS` mirrored to the same `R` (feeds
   `corridor_r = max(WORLD_DOCK_DISC_RADIUS/ws, ship_r+clearance)`, so it must change *before*
   re-baking).
3. Re-bake `base.glb` in place via the `base-collision` skill (`tools/collision-hull/bake.py
   --kind base`).
4. `client/scripts/BaseModelLoader.cs` — `DebugConeRadius` mirrored to `R` too (currently-disabled
   debug viz, but the file's own comment requires it stay in sync).
5. `server/Assets/SelfTest.cs` — no code change; its corridor/spawn-clearance assertions are
   structural (disc count, unit normals, corridor not blocked), guaranteed by the bake's
   `ship_r+clearance` floor at any resolved radius. Just re-run `--selftest` after rebaking.

**Picking `R`**: there's a real tension the numbers surface — un-blobbing the E0/E3/E4 cluster
(spaced ~12-13 world units apart) wants a much smaller radius, but a full-speed Scout (160 u/s, no
afterburner cap, `Dt=0.05s` @ 20Hz) needs the along-axis capture window (`discRadius+shipRadius`)
to be at least ~8 units to not tunnel through in one tick — i.e. `discRadius ≳ 5-6` as a practical
floor with margin. **Approach**: use the `base-collision`/`collision-hull-generator` `--show`
visualizer plus the `hardpoints` skill's GLB dump to see the actual visible door footprint (the
rough hardpoint-spacing math above is only a proxy — the real doors may be one continuous wide bay
mouth, tolerating more overlap than the raw spacing suggests), and pick `R` in the ~6-9 range,
first. Re-bake, re-run `--selftest`, and smoke-test an actual dock via `--autofly` from a few
angles/entrances. **Fallback only if that can't both un-blob and clear the tunneling floor**:
decouple lateral radius from along-axis depth by adding a second constant and widening
`IntersectsDockDisc`'s signature (touches `Collide.cs:304` and its two call sites, plus
`bake.py`'s `corridor_r` derivation) — flagged as a bigger, riskier change to fall back to only if
needed, not to build preemptively.

## Critical files

- `client/scripts/CameraRig.cs` — launch-cam state machine
- `client/scripts/WorldRenderer.cs` — `Launched` meta wiring (`InsertShip`, `NetPromoteLocal`)
- `client/scripts/ShipModelLoader.cs` / `client/scripts/GlbLoader.cs` — length/width metadata
- `shared/Collision/CollisionConfig.cs` / `shared/Collision/Collide.cs` — dock disc radius/kernel
- `tools/collision-hull/bake.py` — corridor bake, mirrored radius constant
- `client/scripts/BaseModelLoader.cs` — mirrored debug-cone radius

## Verification

- Boot the server with `--selftest` after any `DockDiscRadius`/bake change — must still pass the
  dock-disc/corridor/spawn-clearance assertions.
- Use `--autofly` (or the `verify` skill) to drive a real client: confirm the 1.5s launch cinematic
  plays on initial spawn, on a respawn-after-dock, and on a pod-eject (not on a reconnect — restart
  the client mid-flight and confirm no cinematic replays), that it ends framed correctly in whichever
  of first-person/third-person was last selected with no pop, and that the own hull stays visible
  throughout an FP-preference launch.
  ‎ Capture `--view-demo=<dir>` style screenshots (existing harness in `CameraRig.RunDemo`) for a
  before/after comparison if useful.
- Manually dock a few times from different angles/entrances post-rebake: confirm it no longer
  triggers from well outside the visible door, still triggers reliably at normal approach speed,
  and a full-speed pass doesn't tunnel through without triggering.
