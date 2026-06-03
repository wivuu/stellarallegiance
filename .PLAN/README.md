# .PLAN — Stellar Allegiance

## Prototype (COMPLETE)

The two-ship prototype (T0–T10) and post-prototype polish are **finished**.
Archives:

- `prototype-archive/00–09, 99` — original prototype plan documents
- `prototype-archive/10-POST-PROTOTYPE-DONE.md` — completed polish & PIGs
- `docs/PROTOTYPE-ARCHITECTURE.md` — consolidated architecture reference

---

## Current Milestone — Sectors & Lobby

### 1. Alephs & Sectors

The game world becomes a **network of sectors** connected by **alephs** (warp
points). A "map" is a graph: nodes are sectors, edges are aleph pairs.

**Server / schema:**
- `Sector` table — id, name, geometry seed, list of asteroid/base refs.
- `Aleph` table — id, sector A, sector B, position in each sector. Alephs
  come in linked pairs (one object per sector, pointing at its partner).
- `Ship.SectorId` — which sector a ship is currently in.
- `Base.SectorId`, `Asteroid.SectorId`, `Projectile.SectorId` — partition
  all world objects by sector.
- `WarpShip` reducer — when a ship reaches an aleph, move it to the linked
  sector (update `SectorId`, reposition at the partner aleph).
- Subscription scoping: clients subscribe to the sector they're in (and the
  map-level topology for the minimap).

**Client — minimap (always visible):**
- A small HUD overlay showing the full sector graph (nodes + edges).
- Each sector node is colored by team presence:
  - Team A color if only Team A has a base there.
  - Team B color if only Team B has a base there.
  - **Disputed** color if both teams have bases.
  - Neutral / dim if no bases.
- The player's current sector is highlighted.

### 2. Sector Map (F3)

**F3** toggles a full-screen sector map overlay rendered in front of the 3D
scene.

- Renders one sector from a top-down orthographic view.
- Mouse wheel zooms in/out.
- Shows ships, bases, asteroids, and alephs as icons/markers.
- Clicking an aleph or a sector label in the sidebar switches the view to
  that sector (read-only overview — the player's ship stays where it is).
- While the sector map is open, normal flight input is paused (or the ship
  drifts on momentum).

### 3. Game Lobby

Players who are **not on a team** are in the lobby. The lobby is also shown
when the match has **ended** or is **not yet in play**.

- Lobby UI: player list, team picker (with balance caps), ready-up button.
- `Player.Team` is nullable — null means "in lobby, no team yet."
- `JoinTeam` / `LeaveTeam` reducers with balance enforcement.
- `Ready` reducer + `Player.Ready` field; match starts when enough players
  are ready (or a countdown triggers).
- `Match.Phase` gains a `Lobby` state that is entered on game end / restart
  (replaces the current terminal `Ended` phase).
- `RestartMatch` reducer resets the world (clear ships/projectiles, reset
  bases, return players to lobby).
- Disconnect cleanup: prevent phantom `Player` rows from non-game
  connections (CLI, owner dashboard).

---

## Backlog (not yet prioritized)

Items below are future directions discussed but not scoped for this milestone.

**Tune prediction lead for WAN** — `STDB_LEAD` env var exists; playtest and
commit. May want adaptive lead based on measured RTT.

**Commander / RTS map view** — 2D strategic overlay for all sectors; commander
issues waypoints and investment orders. Depends on sectors existing first.

**Mining economy + constructors** — Resource asteroids, miners, ore flow,
build queues. The economic engine that makes Allegiance a strategy game.

**Tech paths** — Team investment tree unlocking ship upgrades, new classes,
base defenses. Depends on mining economy.

**Spectator mode** — Subscribe-only, free-fly camera, no spawn.

**Replay system** — Tick log or time-travel query playback.

**CI / automated testing** — Docker-based module build + headless client
integration tests.

**Matchmaking / accounts / persistence** — Player identities, ELO, match
history.
