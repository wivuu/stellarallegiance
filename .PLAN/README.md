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

- in-game chat, lobby and in-game

- shield and damage systems

**Improved In-game UI** — Health bars, shields (if present) minimap, ship status indicators, team scores.

- per-player in-match and post-match scores/kills/deaths, overall point system and player ranks

**Tune prediction lead for WAN** — `STDB_LEAD` env var exists; playtest and
commit. May want adaptive lead based on measured RTT.

**Commander / RTS map view** — 2D strategic overlay for all sectors; commander
issues waypoints and investment orders. Depends on sectors existing first. Promote commander done by commander, or first player to join a team.

**Mining + economy + paychecks** — Resource asteroids, miners, ore flow,
build queues. The economic engine that makes Allegiance a strategy game.

**Base building + constructors** — Deployable structures for resource processing, ships land and repair at bases

- escape pods

**Tech paths** — Team investment tree unlocking ship upgrades, new classes,
base defenses. Depends on mining economy and building bases that unlock tech.

- ship classes and loadouts
    - weapon systems

**Factions** - Distinct factions with unique ship classes, tech trees, and visual styles. Adds variety and strategic depth to the game by encouraging different playstyles and team compositions based on faction strengths and weaknesses.

**Spectator mode** — Follow other players with tab key (camera spins around player), click on different sectors from lobby.

**CI / automated testing** — Docker-based module build + headless client
integration tests.

**Matchmaking / accounts / persistence** — Player identities, ELO, match
history.

## Visual polish
**Ship meshes and hardpoints** - Create mechanism to load ship models which contain hardpoints for weapons, thrusters, main engine+booster, turrets (if any)

**Base meshes and hardpoints** - Create mechanism to load base models which contain hardpoints for docking (entrance), lighting (blinking), exit point(s).

**Engine glow** - Add dynamic glow effects to ship engines that intensify with throttle and boost usage, enhancing the visual feedback of movement and combat. Use particle systems and shader effects to create a vibrant, immersive experience that reflects the power and agility of the ships.

**Improve asteroid texture mapping** - Minimize stretching and distortion on asteroids by adjusting UV coordinates or using tri-planar mapping techniques to ensure textures wrap more naturally around the irregular shapes of the asteroids. If using tri-planar mapping, explore blending and baking the textures for better visual quality. Implement parallax/height maps in-engine for added surface detail without increasing polygon count, enhancing the visual richness of the asteroids.

**Implement 'booster' and smoke trail particle effects** - Design and integrate particle systems for ship boosters and smoke trails to enhance the visual feedback of movement and combat. Use a combination of sprite-based particles and shader effects to create dynamic, visually appealing trails that react to ship speed and maneuvers. Consider adding variations in color and intensity based on ship type or damage level for added visual interest. Boosters should have a limit and a recharge mechanic, allow some ships to recharge and some without recharge to create more interesting ship classes and combat dynamics.

## Deep backlog
- **Replay system** — Tick log or time-travel query playback.
