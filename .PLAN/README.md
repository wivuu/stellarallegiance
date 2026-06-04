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

**Status — core mechanics IMPLEMENTED (June 2026):**
- `Sector` + `Aleph` tables; `SectorId` on `Ship` / `Base` / `Asteroid` /
  `Projectile`. Init seeds two sectors (Core + Verge) and one linked aleph pair.
- Alephs render as **spinning funnels** (`AlephView`); flying into one warps the
  ship to the partner sector (server-authoritative, in `SimTick`), re-emerging
  just past the partner aleph. Prediction hard-snaps across the warp.
- **Sector boundaries**: a ship beyond its sector radius takes mounting hull
  damage until it returns or explodes; the HUD shows an out-of-bounds warning.
- SimTick passes (projectile hits, ship/asteroid/base collisions) are
  sector-scoped; the client shows only the local sector's objects.
- Still TODO for this item: the **minimap** below, sector-scoped *subscriptions*
  (clients currently subscribe to all sectors and filter on render), and a
  proper `WarpShip` reducer if contact-warp ever needs a manual trigger.

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

**Status — IMPLEMENTED (June 2026):**
- `Player.Team` is now `byte?` (null = in the lobby) and `Player.Ready` was
  added. New connections land teamless in the lobby; they no longer auto-assign
  a side.
- `JoinTeam` / `LeaveTeam` / `SetReady` reducers, plus `QuickJoin` (smallest
  side + ready, used by the headless autofly client and offered as a lobby
  shortcut). `JoinTeam` enforces a balance cap (you may only join a side that
  isn't already larger than the other) and is refused mid-match.
- `MaybeStartMatch` rewritten: Lobby → Active once every teamed online player is
  readied (and at least one is). **Solo is allowed** — the AI drones (PIGs)
  provide opposition — so one readied pilot launches a match. Starting resets
  the world and consumes the ready flags.
- `RestartMatch` (valid only from `Ended`) wipes the battlefield via the shared
  `ResetWorld` helper (despawn ships/drones + inputs, clear projectiles, heal
  bases to full), prunes offline players, un-readies the rest (keeping their
  team), and returns `Match.Phase` to `Lobby`.
- `SpawnShip` now requires `Phase == Active` and a team (the lobby gates flying).
- **Join mid-game:** `JoinTeam` / `QuickJoin` work during `Active` too — a teamless
  player picks a side from the lobby overlay and spawns straight into the running
  match (still balance-capped; refused only in `Ended`, which routes through
  `RestartMatch`).
- **End on side abandonment:** `Match.EngagedTeams` (bitmask) records which sides
  have fielded a human this match (set at start and on mid-game join). When an
  engaged side drops to zero online pilots (`EndMatchIfSideAbandoned`, run on
  disconnect / `LeaveTeam`), the match ends: the other side wins by forfeit, or —
  if everyone left — it quietly resets to the lobby. Solo-vs-PIGs still works
  because an un-engaged empty side never triggers it.
- Disconnect cleanup: teamless connections (CLI subscriber, owner dashboard, a
  player who never picked a side) are **deleted** on disconnect so they don't
  haunt the roster; teamed players are kept offline for reconnect and pruned by
  the next `RestartMatch` / all-leave reset.
- Client `Lobby` overlay (`client/scripts/Lobby.cs`, created by the Hud): roster
  with team/ready, team picker with live balance counts, ready/quick-join/leave,
  the "match in progress" notice for latecomers, and the winner + "Return to
  Lobby" end screen. The Hud's spawn menu only appears once you're teamed in an
  active match.

Possible follow-ups (not done): a ready **countdown** as an alternative start
trigger, and an in-lobby name-entry field (`SetName` exists but has no UI yet).

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
