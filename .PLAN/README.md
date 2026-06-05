# .PLAN — Stellar Allegiance

## Prototype (COMPLETE)

The two-ship prototype (T0–T10) and post-prototype polish are **finished**.
Archives:

- `prototype-archive/00–09, 99` — original prototype plan documents
- `prototype-archive/10-POST-PROTOTYPE-DONE.md` — completed polish & PIGs
- `docs/PROTOTYPE-ARCHITECTURE.md` — consolidated architecture reference

---

## Current Milestone — Sectors & Lobby

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

<visuals> **Improve asteroid texture mapping** - Minimize stretching and distortion on asteroids by adjusting UV coordinates or using tri-planar mapping techniques to ensure textures wrap more naturally around the irregular shapes of the asteroids. If using tri-planar mapping, explore blending and baking the textures for better visual quality. Implement parallax/height maps in-engine for added surface detail without increasing polygon count, enhancing the visual richness of the asteroids.

<visuals> **Implement 'booster' and smoke trail particle effects** - Design and integrate particle systems for ship boosters and smoke trails to enhance the visual feedback of movement and combat. Use a combination of sprite-based particles and shader effects to create dynamic, visually appealing trails that react to ship speed and maneuvers. Consider adding variations in color and intensity based on ship type or damage level for added visual interest. Boosters should have a limit and a recharge mechanic, allow some ships to recharge and some without recharge to create more interesting ship classes and combat dynamics.