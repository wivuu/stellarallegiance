# .PLAN — Stellar Allegiance

> **⚠ Note:** SpacetimeDB has been removed — the server is now a standalone authoritative sim +
> lobby host and the client downloads everything over the wire. Roadmap items below that assume
> an STDB backend are historical; see the repo **README.md** for the current shape.

## Prototype (COMPLETE)

The two-ship prototype (T0-T10) and post-prototype polish are **finished**.
Archives:

- `prototype-archive/00-09, 99` — original prototype plan documents
- `prototype-archive/10-POST-PROTOTYPE-DONE.md` — completed polish & PIGs
- `docs/PROTOTYPE-ARCHITECTURE.md` — consolidated architecture reference

---

## QUICKNOTES:
- CONFIG M5 addendum
- Code cleanup and refactor
- Move the ship's team trail to a few meters behind the back of the ship, and smooth out the trail a bit so each tiny control adjustment isnt reflected in the trail so obviously. Gradient it slightly at the start so it doesnt look sharp edged at the start
---

## Roadmap (prioritized)

Themed and ordered top-to-bottom. Infra (host-server image, CI) is pulled in
as-needed per phase rather than done up front.

### Phase 1 — Configurability & maintainability refactor -- DONE

Move hard-coded tuning and content into data so new ships, weapons, and bases
are config, not code. Unblocks variety in later phases.

- **Data-driven ship classes & loadouts** — Lift weapon/tuning constants out of
  `Lib.cs` into configurable class + loadout definitions (weapon systems,
  hardpoint slotting).
  - Break 'Lib.cs' into more focused modules, e.g. 'Weapons.cs', 'Ships.cs', 'Bases.cs'.
- **Ship meshes & hardpoints** — Loader for ship models carrying hardpoints for
  weapons, thrusters, main engine + booster, and turrets.
- **Base meshes & hardpoints** — Loader for base models carrying hardpoints for
  docking (entrance), lighting (blinking), and exit point(s).

### Phase 2 — Combat feel & depth

Make the existing two-team dogfighting richer on the systems already shipped
(ships, guns, health/damage, boost, AI drones).

- **Shields & damage systems** — Layer regenerating shields over the existing
  raw-health model; damage-type interactions.
- **Missiles** — Launchers, lock-on, and chaff/flare countermeasures.
- **Mines & fields** — Deployable mines and minefields.
- **Boost recharge & ship-class feel** — Boost limit + recharge mechanic;
  some classes recharge, some don't, to differentiate combat roles. Pair with
  booster/smoke-trail particle FX that react to speed, maneuver, and damage.
- **Ship salvage & pickups** — Destroyed ships drop random ammo / guns /
  missiles / mines that must be flown over to collect.
- **Escape pods** — Eject on ship death; pod must either die or be rescued by a teammate in order for player to respawn.
- **Improved in-game UI** — Health/shield bars, ship status indicators,
  team scores; minimap already exists. (Per-player scores/ranks land in Phase 3.). Healthbars should be hud elements, not world-space objects -- currently base health bar casts a shadow on base for example

### Phase 3 — Hosting at scale

Stand up many independent game servers and let players find and join them.

- **Custom host game-server Docker image** — Self-contained SpacetimeDB game
  server; clients target it via a launch arg.
- **Multi-server / central aggregator** — Run multiple *independent*
  SpacetimeDB instances (up to ~200 players each); a central aggregator
  (likely a web server) lists available servers / acts as a browser.
- **Tune prediction lead for WAN** — `STDB_LEAD` exists; playtest and commit a
  good default, ideally adaptive on measured RTT.
- **Scores, kills/deaths & ranks** — Per-player in-match and post-match stats,
  an overall point system, and player ranks.
- **Spectator mode** — Follow players with Tab (camera orbits target); pick
  sectors to watch from the lobby.
- **Matchmaking, accounts & persistence** — Player identities, ELO, match
  history.
- **Custom Maps** - Maps configurable on a server, the asteroids would not be hardcoded, but instead the aleph layout would be configurable.


### Phase 4 — Strategy layer (Allegiance core)

The economic + RTS loop that turns the shooter into a strategy game. Largely
sequential — each item depends on the one before.

- **Mining + economy + paychecks** — Resource asteroids, miners, ore flow,
  build queues.
- **Base building + constructors** — Deployable structures for resource
  processing; ships land, repair, and rearm at bases.
- **Tech paths** — Team investment tree unlocking ship upgrades, new classes,
  and base defenses. Depends on the mining economy and bases.
- **Commander / RTS map view** — 2D strategic overlay across all sectors;
  commander issues waypoints and investment orders. Sectors already exist.
  Commander role goes to the first player to join a team (or is promoted).
- **Factions** — Distinct factions with unique ship classes, tech trees, and
  visual styles for asymmetric play.

### Cross-cutting / opportunistic

Not phase-bound — done when convenient or when a phase needs them.

- **CI / automated testing** — Docker-based module build + headless client
  integration tests. (Pulled in as-needed, esp. before Phase 3 hosting.)
- **Improve asteroid texture mapping** — Reduce stretching via better UVs or
  tri-planar mapping; explore baking and in-engine parallax/height maps.

## Deep backlog

- **Replay system** — Tick log or time-travel query playback.
- **.NET 10 upgrade** — Upgrade from .NET 8 to 10 for perf
- Fireteam support - Sub-teams of 2-6 players that can privately chat. Commanders can assign players to fireteams and can issue orders to specific fireteams.