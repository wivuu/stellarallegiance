# .PLAN — Stellar Allegiance

> **⚠ Architecture note:** SpacetimeDB has been removed. The server is now a standalone
> authoritative 20 Hz sim that *also* hosts the lobby, and the client downloads everything
> (world, content defs, live state) over the wire. Roadmap items below that mention an STDB
> backend or `STDB_*` env vars are historical; the prediction-lead override is still *named*
> `STDB_LEAD` in code but the lead is adaptive now (see Phase 3). For the current shape see the
> repo **README.md**.

## Prototype (COMPLETE)

The two-ship prototype (T0-T10) and post-prototype polish are **finished**.
Archives:

- `prototype-archive/00-09, 99` — original prototype plan documents
- `prototype-archive/10-POST-PROTOTYPE-DONE.md` — completed polish & PIGs
- `docs/PROTOTYPE-ARCHITECTURE.md` — consolidated architecture reference

---

## QUICKNOTES:
- Code cleanup and refactor
---

## Roadmap (prioritized)

Themed and ordered top-to-bottom. Infra (host-server image, CI) is pulled in as-needed per
phase rather than done up front.

### Phase 1 — Configurability & maintainability refactor — ✅ DONE

Tuning and content are data, not code, so new ships, weapons, and bases are config.

- ✅ **Data-driven ship classes & loadouts** — weapon/tuning constants lifted out of `Lib.cs`
  into runtime-configurable class + loadout defs (`DefRegistry`); weapon/ship/base logic split
  into focused modules.
- ✅ **Ship meshes & hardpoints** — `ShipModelLoader` reads GLBs carrying `HP_` hardpoint nodes
  (weapons, thrusters, engine + booster, turrets). The `tools/ship-gen` pipeline builds modular
  GLBs from YAML.
- ✅ **Base meshes & hardpoints** — `BaseModelLoader` reads base models with docking, lighting,
  and exit hardpoints.
- ✅ **(bonus) Server-side collision** — the server reads the same GLBs into convex hulls +
  docking hardpoints; ships dock/exit via real geometry.

### Phase 2 — Combat feel & depth

Make the existing two-team dogfighting richer on shipped systems (ships, guns, health/damage,
boost, AI drones). Partially underway.

- ✅ **Escape pods** — ships eject a pod on death; the pod must die or be rescued by a teammate
  (drones run rescue duty) before the player respawns.
- ✅ **Booster / smoke-trail FX** — booster smoke trail reacting to thrust (combat-feel polish).
- ☐ **Fog of war** — asteroids and enemy bases stay hidden until scouted by a teammate.
- ☐ **Boost recharge & ship-class feel** — boost limit + recharge mechanic; some classes
  recharge, some don't, to differentiate combat roles. (FX done; the recharge *mechanic* is not.)
- ☐ **Shields & damage systems** — regenerating shields layered over the raw-health model;
  damage-type interactions.
- ☐ **Missiles** — launchers, lock-on, and chaff/flare countermeasures.
- ☐ **Mines & fields** — deployable mines and minefields.
- ☐ **Ship salvage & pickups** — destroyed ships drop random ammo / guns / missiles / mines to
  fly over and collect.
- ◐ **Improved in-game UI** — velocity indicator, radar/targeting, and base health bar shipped;
  minimap exists. Still want player-facing health/shield bars and team scores as proper HUD
  elements (see QUICKNOTES). Per-player scores/ranks land in Phase 3.

### Phase 3 — Hosting at scale

Stand up many independent game servers and let players find and join them. **The discovery +
hosting core is done; the social/persistence layer is not.**

- ✅ **Public lobby & discovery** — `public-lobby/` registry + WebRTC signaling relay; servers
  register under `SIM_PUBLIC_NAME`, clients browse a server list (or direct-connect by `ip:port`).
  Direct-first reachability probing, WebRTC/STUN fallback (no TURN), Railway deploy.
- ✅ **Server lifecycle** — empty-server idle reset + match recycling; protocol versioning;
  client-update release checks that ban out-of-date servers/clients.
- ✅ **Adaptive prediction lead** — lead is derived from measured RTT + jitter
  (`UpdateAdaptiveLead`); `STDB_LEAD` (legacy name) remains as a manual override.
- ☐ **Scores, kills/deaths & ranks** — per-player in-match and post-match stats, an overall
  point system, and player ranks.
- ☐ **Spectator mode** — follow players with Tab (camera orbits target); pick sectors from the
  lobby.
- ☐ **Matchmaking, accounts & persistence** — player identities, ELO, match history. (The
  server browser exists; durable accounts do not.)
- ☐ **Custom maps** — server-configurable aleph layout instead of a hardcoded asteroid field.

### Phase 4 — Strategy layer (Allegiance core)

The economic + RTS loop that turns the shooter into a strategy game. Largely sequential — each
item depends on the one before. (All future work.)

- ☐ **Mining + economy + paychecks** — resource asteroids, miners, ore flow, build queues.
- ☐ **Base building + constructors** — deployable structures for resource processing; ships
  land, repair, and rearm at bases.
- ☐ **Tech paths** — team investment tree unlocking ship upgrades, new classes, and base
  defenses. Depends on the mining economy and bases.
- ☐ **Commander / RTS map view** — 2D strategic overlay across all sectors; commander issues
  waypoints and investment orders. Sectors already exist. Commander role goes to the first
  player to join a team (or is promoted).
- ☐ **Factions** — distinct factions with unique ship classes, tech trees, and visual styles
  for asymmetric play.

### Cross-cutting / opportunistic

Not phase-bound — done when convenient or when a phase needs them.

- ✅ **CI / automated testing** — tag-triggered Release workflow (client zips + GHCR server
  image); `FlightModelTest` (determinism/golden) and `CryptoTest` in `tests/`.
- ☐ **Improve asteroid texture mapping** — reduce stretching via better UVs or tri-planar
  mapping; explore baking and in-engine parallax/height maps.
- ☐ **Spatial audio polish** — `SfxManager` exists; collisions and a settings UI are deferred.

## Deep backlog

- ☐ **Replay system** — tick log or time-travel query playback.
- ☐ **.NET 10 upgrade** — upgrade from .NET 8 to 10 for perf.
- ☐ **Fireteam support** — sub-teams of 2-6 players that can privately chat. Commanders can
  assign players to fireteams and issue orders to specific fireteams.
