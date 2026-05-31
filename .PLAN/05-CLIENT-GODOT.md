# 05 — Godot Client

Godot 4 (.NET build), C#. The client renders the world, reads input, predicts the local
ship, interpolates remote entities, and calls reducers. It is a thin presentation layer over
the SpacetimeDB subscription state.

## Scene tree

```
Main (Node3D)                      ← root, holds ConnectionManager + WorldRenderer
├── ConnectionManager (Node)       ← autoload-style singleton; owns the DB connection
├── WorldRenderer (Node3D)         ← maps DB rows → scene nodes (spawn/despawn)
│   ├── Ships (Node3D)             ← parent for all ship nodes
│   ├── Bases (Node3D)
│   ├── Asteroids (Node3D)
│   └── Projectiles (Node3D)
├── CameraRig (Node3D)             ← follows the local ship (chase cam)
│   └── Camera3D
└── HUD (CanvasLayer)              ← health, speed, spawn menu, match-end banner
```

`Ship.tscn`, `Base.tscn` are instanced per-row by `WorldRenderer`. For the prototype, ships
are a primitive mesh (Scout = small cone, Fighter = larger box/wedge), bases are a large
sphere or station-ish CSG shape, asteroids are icospheres scaled by `Radius`.

## Scripts and responsibilities

### `ConnectionManager.cs`
- Connects to the DB (`ws://localhost:3000`, database `stellar-allegiance`).
- Registers subscription queries (see `07` for the exact set).
- Exposes the connection object and the local `Identity` to other scripts.
- Raises C# events on row insert/update/delete that `WorldRenderer` listens to.
- Handles reconnect logging. On connect, calls `SetName`.

### `WorldRenderer.cs`
- Subscribes to ConnectionManager's row events.
- On `Ship` insert → instance `Ship.tscn`, tag it with `ShipId`, register it.
  On update → hand the new transform to that node's interpolator/predictor.
  On delete → free the node (play a small destruction puff, optional).
- Same insert/update/delete handling for `Base`, `Asteroid` (insert-only), `Projectile`.
- Decides, per ship, whether it is the **local** ship (`Owner == localIdentity`) and attaches
  `PredictionController` to it; otherwise attaches `RemoteShipInterpolator`.

### `ShipController.cs` (local input)
- Reads input each `_Process`/`_PhysicsProcess` frame (keyboard/mouse or gamepad):
  thrust, strafe, yaw/pitch/roll, fire.
- At the **input tick rate** (not every frame), samples the current input and:
  - applies it to the local prediction (via `PredictionController`),
  - calls `ApplyInput(...)` with the current client tick.
- Maintains the client sim tick counter, advanced at the same 20 Hz cadence as the server.

### `PredictionController.cs` (local ship only)
- Holds a ring buffer of `(tick, input, resultingState)` for recent ticks.
- Each input tick: integrates the local ship forward using the shared `FlightModel` with the
  fixed `dt`, storing the predicted state per tick.
- On authoritative `Ship` update from the server (carrying `LastInputTick`):
  - Compare predicted state at `LastInputTick` to the authoritative state.
  - If within tolerance → do nothing (prediction was right).
  - If diverged → snap the ship to the authoritative state, then **re-simulate** all buffered
    inputs after `LastInputTick` to catch back up to the present (standard rollback
    reconciliation). See `07` for tolerances.
- Renders the ship at the reconciled predicted position, smoothing visual snaps over a few
  frames to avoid popping.

### `RemoteShipInterpolator.cs` (other players' ships)
- Buffers the last two authoritative transforms with their tick timestamps.
- Renders at `renderTime = now - interpolationDelay` by interpolating position and slerping
  rotation between the two buffered samples. Never predicts remote ships forward in the
  prototype (keep it simple; extrapolation can come later).

### `WorldRenderer` for projectiles
- Projectiles are short-lived and numerous-ish. Interpolate them like remote ships, or for
  the prototype simply lerp toward the latest authoritative position. Free on row delete.

## HUD (minimal)
- Local ship health bar and current speed readout.
- A spawn menu shown when the player has no live ship: two buttons → `SpawnShip(Scout)` /
  `SpawnShip(Fighter)`.
- A match-end banner driven by `Match.Phase == Ended` / `Match.Winner`.

## Camera
- Chase camera behind the local ship; smoothly follows position and orientation. When the
  local ship is dead, park the camera at the team base looking outward.

## What the client must never do
- Never write game state locally and treat it as truth. The only mutations are reducer calls.
- Never integrate remote ships with player input — it doesn't have their input.
- Never use a variable-`delta` integration for the *predicted* path; it must match the
  server's fixed `dt` or reconciliation will fight you every tick.
