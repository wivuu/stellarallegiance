# Post-Prototype — Completed Features

Items completed after the T0–T10 prototype, before the sectors milestone.

---

## Polish

**Mouse-look aiming** — Mouse-relative aiming (true Allegiance style) added in
`ShipController`: captured-cursor motion accumulated in `_Input`, scaled by
sensitivity and clamped to -1..1 yaw/pitch axes (summed with arrow keys).
Cursor captured while flying — Esc frees, click recaptures — released for spawn
menu. `STDB_MOUSE_SENS` (default 0.08), `STDB_MOUSE_INVERT=1` flips pitch.
Left mouse fires while captured.

**Spawn offset** — Ships now launch outward from base (base radius + ship radius
along base→center direction) instead of spawning inside the base sphere.

**Enemy shot masking** — Projectiles are pure fire-and-forget: client
extrapolates the single spawn line for the projectile's whole life and ignores
server per-tick position updates. Enemy/remote shots get a one-time forward
spawn offset (`ProjectileView._renderLeadSec`) derived from measured one-way
latency so the bolt appears where it really is now. `STDB_SHOT_MASK_MS` pin
available.

**Enemy target markers** — `TargetMarkers` overlay draws a marker for every
enemy ship: corner-bracket reticle on screen, edge-clamped arrow off screen
(including behind camera via mirrored unproject). Tab cycles focus. Blue aim
reticle on the real muzzle firing line; green lead circle for focused target
using relative-velocity intercept. `RemoteShip` eases velocity (~60 ms time
constant) to smooth 18.7 Hz steps.

**Weapon spread** — Per-weapon cone scatter (`ScoutSpread` ≈ 0.34°,
`FighterSpread` ≈ 2.0°) via `FlightModel.SpreadDirection`. Fully deterministic
(keyed by `ShipId, fireTick`), so server and client compute identical vectors.

## Major Features

**PIGS** — AI combat drones reusing the full ship path (same `Ship` table,
`FlightModel`, fire control, collision, rendering). `Pig` table holds slots
(5/side, alternating Scout/Fighter) that outlive drones; 30 s respawn cooldown,
staggered spawns. State-machine brain (Idle/Seek/Attack) with threat-scored
targeting, proportional steering, asteroid avoidance. Drones exist only while
≥1 player ship is alive; despawn when empty. PIG fire deals zero base damage
(`Projectile.FromPig`). Distinct magenta HUD markers + darker metallic
in-world material.
