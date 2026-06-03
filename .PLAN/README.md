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

**Tune prediction lead for WAN** — The prototype measured ~115–125 ms RTT on
Maincloud with ±25 ms jitter. At `TargetLead=3` (150 ms), jitter spikes
occasionally land an input late and cause a reconcile (visible as turning
jerk). Bumping to 4–5 would widen the margin. The `STDB_LEAD` env var already
exists; this is a playtest-and-commit task. May also want adaptive lead based
on measured RTT.

**Spawn offset** — Ships currently spawn at the exact base center, inside the
45-unit base sphere. A small launch vector outward (e.g. base radius + ship
radius along the base→center direction) would look and feel better.

**Enemy shot masking** — Own muzzle flashes are already client-predicted, but
enemy shots pop in ~1 RTT late. A short forward-extrapolation on spawn (place
the projectile where it would be now given its velocity and the interpolation
delay) would mask the pop without full prediction.

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

**Commander / RTS map view** — The defining Allegiance feature. A 2D top-down
strategic overlay showing the sector (or all sectors), friendly ship positions,
base status, and resource flow. Could be a second camera mode (toggle key), a
separate viewport, or a separate scene. The commander would issue waypoints
and investment orders. Architecturally this is a new subscription scope
(commander sees everything on their team) and a new set of UI scenes. Biggest
open question: does the commander control ships directly, or just issue
suggestions/orders that pilots can ignore?

**Multiple sectors + alephs** — The current prototype is one sector. Allegiance
has multiple sectors connected by aleph warp points. This is a significant
architecture change: each sector would be a logical partition of the world
state. Ships warp between sectors (delete from one, insert in another).
Subscription scoping becomes per-sector (clients only subscribe to the sector
they're in) for performance. The `Asteroid`, `Base`, and sector geometry
become per-sector. Needs a `Sector` table and `Aleph` table at minimum.

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
