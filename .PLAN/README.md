# .PLAN — Stellar Allegiance

## Prototype (COMPLETE)

The two-ship prototype (T0–T10) is **finished**. All specs, build order,
acceptance tests, and decision log from that milestone are archived in:

- `prototype-archive/` — the original 00–09 + 99 plan documents
- `docs/PROTOTYPE-ARCHITECTURE.md` — consolidated architecture reference

## Next milestone

_This directory is ready for the next set of plan documents._

### Candidate directions (not yet scoped)

Below are all ideas discussed so far, grouped roughly by effort and risk.

---

#### Polish the prototype

These require no schema/architecture changes and directly improve the feel of
what already exists.

**Mouse-look aiming** — ✅ **DONE.** Mouse-relative aiming (true Allegiance
style) added in `ShipController`: captured-cursor motion is accumulated in
`_Input`, scaled by sensitivity and clamped to the existing -1..1 yaw/pitch
axes (summed with the arrow keys, which still work as a fallback). The cursor
is captured while flying — Esc frees it, a click recaptures — and released for
the spawn menu. Sensitivity tunes via `STDB_MOUSE_SENS` (default 0.08),
`STDB_MOUSE_INVERT=1` flips pitch. Left mouse fires while captured. Remaining:
playtest-tune the default sensitivity to taste.

**Spawn offset** — ✅ **DONE.** Ships currently spawn at the exact base center, inside the
45-unit base sphere. A small launch vector outward (e.g. base radius + ship
radius along the base→center direction) would look and feel better.

**Enemy shot masking** — ✅ **DONE.** Projectiles are now pure fire-and-forget:
since they're constant-velocity, the client extrapolates the single spawn line
for the projectile's whole life and ignores the server's per-tick position
updates entirely (`Projectile.OnUpdate` is no longer subscribed). This is both
smoother — re-anchoring on each 20 Hz sample multiplied arrival jitter by
projectile speed into a visible snap (~6 u at 250 u/s, ±25 ms) — and simpler.
Exact position is not corrected; the server stays authoritative for hits via
the row delete. On top of that, enemy/remote shots (which arrive ~1 RTT late
and would pop in at the stale muzzle) get a one-time forward spawn offset
(`ProjectileView._renderLeadSec`) so the bolt appears where it really is now.
The offset is derived from measured one-way latency (≈ half `PingMs`, clamped
0–250 ms; 0 on localhost) or pinned via `STDB_SHOT_MASK_MS`. Remaining:
playtest-confirm the masking offset feels right on WAN.

**Tune prediction lead for WAN** — **SKIP FOR NOW** The prototype measured ~115–125 ms RTT on
Maincloud with ±25 ms jitter. At `TargetLead=3` (150 ms), jitter spikes
occasionally land an input late and cause a reconcile (visible as turning
jerk). Bumping to 4–5 would widen the margin. The `STDB_LEAD` env var already
exists; this is a playtest-and-commit task. May also want adaptive lead based
on measured RTT.

**Enemy target markers** — ✅ **DONE.** A new `TargetMarkers` overlay (a
full-rect `Control` created and wired by the `Hud`) draws, while flying, a marker
for every enemy ship: a corner-bracket reticle when it's on screen and an
edge-clamped arrow pointing toward it when it's off screen — including behind the
camera, where the unprojected point is mirrored about center so the arrow points
to the correct side. Tab cycles FOCUS through the visible (in-front) enemies in
stable ShipId order (wrapping past the last back to none); the focused target is
drawn larger/brighter. Because the chase camera sits above/behind the ship, screen
center is NOT where shots go, so a blue **aim reticle** is drawn on the real muzzle
firing line (ship forward +Z from the nose). When a target is focused, a
constant-velocity intercept is solved in the shooter's frame; if a forward solution
exists within weapon range (`MaxLeadTime` = projectile lifespan, 2.5 s) a green lead
circle marks the point to aim the NOSE at. Crucially the lead uses the RELATIVE
velocity (`targetVel - shooterVel`), not the target's absolute velocity: projectiles
inherit the firing ship's velocity (`mv = shotDir·ProjectileSpeed + shipVel`), so the
point you aim at is the target led by relative velocity — the shot's inherited drift
then carries it onto the target. (Aiming at the absolute meeting point would miss
whenever the shooter has lateral velocity.) The aim reticle is ranged to match
(`ProjectileSpeed·t`), so overlaying the reticle on the lead circle is a hit. Velocities
come from `PredictionController.Velocity` (local, predicted) and the authoritative
`Ship.Vel` carried on `RemoteShip.Velocity` (enemies) — read straight from the row
rather than finite-differenced from snapshots, which was noisy enough to make the
lead reticle jitter even in straight-line flight. The row velocity still arrives in
~18.7 Hz steps, so `RemoteShip` eases its exposed `Velocity` toward the latest row
value each frame (exponential, ~60 ms time constant) to tween out the steps. Screen projection uses
`GetViewportRect().Size` (not the Control's own `Size`, which a code-created Control
under a CanvasLayer doesn't reliably resolve — that misplaced the off-screen edge
arrows). The overlay is pure render — it never touches authoritative state.
Remaining: playtest-tune marker sizes / lead feel.

**Weapon spread** — ✅ **DONE.** Each weapon now scatters its shots within a cone
whose HALF-ANGLE is a tweakable per-weapon value in `shared/FlightModel.cs`
(`ScoutSpread` ≈ 0.34°, the near-pinpoint default; `FighterSpread` ≈ 2.0°; both via
`WeaponSpreadRad`). On fire, the projectile spawns at the nose along true forward but
launches along `FlightModel.SpreadDirection(fwd, spread, shipId, fireTick)`, which
perturbs the direction inside the cone. The scatter is fully DETERMINISTIC — keyed by
`(ShipId, fireTick)` and built from integer hashing + `MathDet` trig + IEEE sqrt — so
the wasm server (authoritative) and the mono client (muzzle prediction) compute the
identical vector and the player's own tracer matches the real projectile shot-for-shot
(same shared-determinism contract as the flight integrator). Lives in `FlightModel`
(not mirrored constants) so the value has a single source of truth. The aim reticle /
lead circle still mark the cone center, i.e. the mean point of impact. Remaining:
playtest-tune the per-weapon spread values.

---

#### Match lifecycle

**Match restart** — `Match.Phase = Ended` is currently terminal; the only way
to play again is `--reset` on publish. Add a `RestartMatch` reducer (or auto-
restart after a timer) that resets `Match` to Lobby, deletes all Ships/
Projectiles, resets Base health, and clears `Player.ShipId`. Minimal schema
change (maybe a `RestartDelayTicks` field on Match).

**Lobby / team selection** — Currently players auto-assign to the smaller team
on connect. A proper lobby would let players pick a team (with balance caps),
ready up, and see who else is connected before the match starts. Needs UI work
(lobby screen in Godot) and a `Ready` reducer + `Player.Ready` field.

**Disconnect cleanup** — `ClientConnected` fires for any connection including
the owner dashboard and CLI, creating phantom `Player` rows. Tighten by
checking whether the caller subsequently subscribes or calls `SetName`, or add
an explicit `JoinMatch` reducer that real clients call after connecting.

---

#### Major new features (the Allegiance roadmap)

**PIGS** - Add Allegiance AI-based opponents. PIGS are simple combat drones that spawn at bases -- configurable max PIGs per side of 5 (default).
They seek out and destroy opponents but leave bases alone for now. 
- When a pig dies, it should have a cooldown of 30 seconds before respawning.
- PIGs are controlled by a simple state machine: Idle (at base), Seek (move toward nearest enemy), Attack (fire at nearest enemy within range).
- PIGs use the same flight model and visibility rules as players, they have radar and lead prediction for aiming
- PIGs are visible to players and can be targeted and destroyed
- PIGs are highlighted differently on the HUD to distinguish them from player ships
- Keep their code cleanly separated from player logic for maintainability.
- PIGs can either be scouts or fighers, with the same stats as player ships.
- Stretch: Attempt to maneuver around obstacles such as asteroids while pursuing targets -- Are there any Godot/.net friendly pathfinding or steering libraries we can leverage for this?
- Their movement should be smooth and believable, not just direct lines to targets.

**Multiple sectors + alephs** — The current prototype is one sector. Allegiance
has multiple sectors connected by aleph warp points. This is a significant
architecture change: each sector would be a logical partition of the world
state. Ships warp between sectors (delete from one, insert in another).
Subscription scoping becomes per-sector (clients only subscribe to the sector
they're in) for performance. The `Asteroid`, `Base`, and sector geometry
become per-sector. Needs a `Sector` table and `Aleph` table at minimum.

**Commander / RTS map view** — The defining Allegiance feature. A 2D top-down
strategic overlay showing the sector (or all sectors), friendly ship positions,
base status, and resource flow. Could be a second camera mode (toggle key), a
separate viewport, or a separate scene. The commander would issue waypoints
and investment orders. Architecturally this is a new subscription scope
(commander sees everything on their team) and a new set of UI scenes. Biggest
open question: does the commander control ships directly, or just issue
suggestions/orders that pilots can ignore?

**Mining economy + constructors** — Resource asteroids that miners harvest,
bringing ore to a base. Ore funds constructors that build new ships, bases, or
tech upgrades. This is the economic engine that makes Allegiance a strategy
game, not just a deathmatch. Requires new tables (`Resource`, `Miner`,
`Constructor`, `TechTree`), new ship classes (Miner, Constructor), and
significant reducer logic for resource flow and build queues.

**Tech paths** — Teams invest resources to unlock ship upgrades, new ship
classes, or base defenses. A tech tree that creates asymmetry between teams
over the course of a match. Depends on the mining economy existing first.

---

#### Infrastructure & dev experience

**Spectator mode** — Subscribe to everything, render all ships interpolated,
no spawn allowed. Useful for debugging, demos, and casting matches. Minimal
server work (spectators are just clients that never call `SpawnShip`); the
client needs a free-fly camera and team-colored ship rendering.

**Replay system** — Since all state is SpacetimeDB rows with tick stamps, a
replay could re-subscribe to historical state (if SpacetimeDB supports time-
travel queries) or record a tick log during play and replay it client-side.
Lower priority but architecturally interesting.

**CI / automated testing** — The prototype relied on headless Godot + dev
flags (`--autofly`, `--combat-test`). A proper CI pipeline would: build the
module WASM in Docker, start a local SpacetimeDB, publish, run two headless
clients, and assert on the output (spawn, fly, shoot, kill, win). Protects
against regressions as the codebase grows.

**Matchmaking / accounts / persistence** — No accounts or persistence exist
beyond a live match. Eventually players need identities, ELO, match history.
SpacetimeDB identities are already used but are ephemeral (tied to a browser/
device key). This is far out but worth noting as a direction.
