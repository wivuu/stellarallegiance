# Glossary — Stellar Allegiance

Reference guide for common terminology across the Stellar Allegiance codebase. Organized by domain with key file locations for modification and extension.

**When to update:** Add new entries when introducing new gameplay systems, content mechanics, or architectural patterns. Cross-reference CLAUDE.md memories and related terms for consistency.

---

## Simulation & Physics

### Flight Model
Core deterministic physics system shared between server and client for ship movement, thrust, and rotation.
- **Frequency:** Very common
- **Key Files:** 
  - `shared/FlightModel.cs` — deterministic physics (shared across server/client)
  - `client/scripts/PredictionController.cs` — client-side input prediction and reconciliation
  - `server/Sim/Simulation.cs` — authoritative server simulation loop (20 Hz tick)
- **Related:** [[SimTick]], [[Held-Input Replay]]
- **Notes:** Server is single source of truth; client predicts and reconciles against server snapshots

### SimTick
Server's authoritative 20 Hz simulation loop that drives all gameplay state updates.
- **Frequency:** Very common
- **Key Files:**
  - `server/Sim/Simulation.cs` — main loop tick handler
  - `server/Sim/Simulation.Pig.cs` — pig brain decision integration
  - `server/Net/Protocol.cs` — snapshot quantization and transmission
- **Related:** [[Flight Model]], [[PigBrain]]
- **Notes:** Never blocks on network I/O; runs deterministically regardless of client connections

### Held-Input Replay
Client technique for smoothing input predictions: store held inputs, replay against server state deltas, avoid jitter when server corrects position.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/PredictionController.cs` — input history buffer and replay logic
  - `shared/FlightModel.cs` — deterministic replay against consistent physics
- **Related:** [[Flight Model]], [[Client Prediction]]
- **Notes:** Prevents popcorn/jitter when client overshoots and server corrects

### AOI (Area of Interest)
Distance-based visibility culling: server only streams entities within fixed distance tiers from each client.
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/World.cs` — entity spatial indexing
  - `server/Net/ClientHub.cs` — per-client entity interest filtering
  - `shared/Defs.cs` — AOI radius constants (SIM_*_RADIUS tuning)
- **Related:** [[Snapshot]], [[Spatial Grid]]
- **Notes:** Fixed nearest-60 replaced by distance tiers + environment knobs (SIM_NEAR_RADIUS, SIM_FAR_RADIUS, SIM_FAR_EVERY)

---

## Weapons & Combat

### Missile
Guided projectile with seekers, can lock targets, detonate via proximity or time-out.
- **Frequency:** Very common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — missile data model
  - `client/scripts/MissileView.cs` — client-side missile rendering
  - `server/Net/Protocol.cs` — MsgMissiles message separate from ship snapshots
- **Related:** [[Seeker]], [[Chaff]], [[Blast Radius]]
- **Notes:** Proto v15: MsgMissiles separate stride (never extend MsgSnapshot records); author racks AFTER guns for barrel-seed alignment

### Seeker
Guidance system that locks onto and tracks targets; disrupted by chaff clouds.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — seeker behavior/accuracy
  - `server/Sim/Simulation.cs` — ResolveSeekerTarget logic (chaff substitution seam)
- **Related:** [[Missile]], [[Chaff]], [[Target Lock]]
- **Notes:** ResolveSeekerTarget checks chaff clouds; seekers can be spoofed

### Chaff
Expendable sensor-decoy puff a ship ejects (key `C`); a seeker rolls a stateless hash
(`ChaffStrength` vs the missile's `ChaffResistance`) to break its lock and home on the puff.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Chaff.cs` — chaff model (ChaffStrength, DecoyRadius)
  - `server/Sim/Simulation.Chaff.cs` — ChaffSim + TryDropChaff/StepChaff/TryChaffAim (Track A fills)
  - `server/Net/Protocol.cs` — `MsgChaff=15` one-shot spawn broadcast (28 B); dispenser WeaponDef (Chaff kind)
  - `client/scripts/ChaffFx.cs` — client puff sprites (Track A fills)
- **Related:** [[Missile]], [[Seeker]], [[Expendables]], [[Minefield]]
- **Notes:** Proto v18: chaff is a launcher-projected `WeaponKind.Chaff` WeaponDef linked to its cargo item
  by `CargoId`; ammo comes from spawn cargo counts, not a rack; TryChaffAim is the D5 substitution seam

### Minefield
A deployed cloud of proximity mines (key `B`): one deploy scatters `MineCloudCount` mines within
`MineCloudRadius`, arms after `MineArmTicks`, then each triggers an enemy within `MineTriggerRadius`
for a `BlastPower`/`BlastRadius` splash; the field depletes mine-by-mine.
- **Frequency:** Domain-specific
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Mine.cs` — mine model (CloudRadius/CloudCount/ArmDelay/BlastRadius)
  - `shared/MinefieldLayout.cs` — splitmix64 seed→cloud offsets, shared by sim/client/tests
  - `server/Sim/Simulation.Mines.cs` — MineFieldSim + TryDeployMine/StepMines (Track B fills)
  - `server/Net/Protocol.cs` — `MsgMinefields=13` (41-B seed records, per anchor sector) + `MsgMineGone=14`
  - `client/scripts/MinefieldViews.cs` — client sprite clouds (Track B fills)
- **Related:** [[Chaff]], [[Blast Radius]], [[Expendables]]
- **Notes:** Proto v18: seed-based wire (client regenerates offsets); `aliveMask` (CloudCount ≤ 64) self-heals a resync

### Threat Lock (being-locked warning)
Warning that an enemy missile-armed ship is locking you: `ShipSim.ThreatLockState` (0 none / 1 locking /
2 locked) rides free bits in the snapshot flags byte (`ShipFlagLockingMe=4`, `ShipFlagLockedMe=8`).
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.cs` — per-tick ThreatLockState reset before Pass A; UpdateLock raises it (Track A)
  - `client/scripts/GameNetClient.cs` — decodes flags → `Ship.ThreatLock` / `LocalThreatLock`
- **Related:** [[Target Lock]], [[Missile]]

### Dock Refund
Voluntary dock at your own base refunds the ship's `PaidCost` to team credits (relaunch pays again →
net-free rearm/repair); death refunds nothing (pods don't inherit PaidCost).
- **Frequency:** Domain-specific
- **Key Files:**
  - `server/Sim/Simulation.cs` — `ShipSim.PaidCost` set in SpawnCombatShip; DockShip refunds (Track A)
- **Related:** [[Hull]], [[Payload]]

### Shield
Regenerating energy layer over the raw-health model, authored per hull/faction (`shield-capacity`,
`shield-recharge` points/sec, `shield-delay` seconds). Absorbs incoming damage before the hull;
overflow from a shield-popping hit spills into the hull the same tick; recharges after the quiet
delay. A per-weapon `shield-damage-multiplier` (default 1.0) is the damage-type interaction.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Hull.cs` — `ShieldCapacity/ShieldRecharge/ShieldDelay`; `Model/Parts/Part.cs` — `ShieldDamageMultiplier`
  - `server/Sim/Simulation.cs` — `ApplyDamage` (single damage seam for all 7 sites), spawn init, end-of-Step recharge sweep, `ShieldsEnabled` test toggle
  - `server/Net/Protocol.cs` — shield f16 in the ship snapshot (ShipRecordSize 55) + 3 shield floats/1 shieldMult in MsgDefs (proto 19)
  - `client/scripts/SystemRing.cs` — cyan SHLD solid arc wrapping the HULL gauge; `client/scripts/ShieldFlash.cs` — hemisphere hit flash
- **Related:** [[Hull]], [[Blast Radius]], [[Direct Hit Multiplier]]
- **Notes:** Proto v19; a pod uses the Pod def's shield (0). `ShieldsEnabled=false` lets damage-mechanic tests isolate raw damage. `tests/ShieldTest` is the determinism guard.

### Blast Radius
Damage falloff zone around explosion epicenter; damps based on distance and intervening obstacles.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — blast_radius, blast_power fields
  - `server/Sim/World.cs` — collision query and damage propagation
  - `server/Sim/Simulation.cs` — blast resolution
- **Related:** [[Missile]], [[Direct Hit Multiplier]]
- **Notes:** Tuned via YAML; falloff curve is inverse-distance-squared

### Direct Hit Multiplier
Damage multiplier for projectiles striking target hull directly (vs. proximity detonation).
- **Frequency:** Domain-specific
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/Missile.cs` — direct_hit_multiplier field
  - `server/Sim/Simulation.cs` — impact detection and damage scaling
- **Related:** [[Blast Radius]], [[Missile]]
- **Notes:** Scales all projectile damage (ballistic + guided); tuned in YAML

### Projectile
Client-side synthetic representation of ballistic fire (bolts, cannon rounds); never stored server-side.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/ProjectileView.cs` — render and interpolation
  - `server/Net/Protocol.cs` — ShotResolution message encodes hit position/time
  - `shared/Defs.cs` — projectile speed/lifetime constants
- **Related:** [[ShotResolution]], [[Client-Side Hit Sparks]]
- **Notes:** Phase 0: no Projectile table; client synthesizes bolts; server drains ShotResolution messages; Phase 1 = native sim server with server-side ballistic tracking

### ShotResolution
Server message reporting ballistic projectile hits: target ship ID, impact position, time offset.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgShotResolution structure
  - `client/scripts/ProjectileView.cs` — consumes resolutions to spawn hit effects
  - `server/Sim/Simulation.cs` — ballistic hit detection logic
- **Related:** [[Projectile]], [[Client-Side Hit Sparks]]
- **Notes:** Separate batch drained per SimTick; allows client to render bolts and confirm hits

---

## Content Pipeline & Game Data

### YAML Content Pipeline
Server-driven content authoring: gameplay/balance values (hulls, weapons, techs, factions) live in YAML files, compiled to binary defs, streamed to clients at runtime.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/factions/` — all content YAML (*.yaml)
  - `factions/src/Allegiance.Factions/` — content model classes and serialization
  - `server/Content/ContentLoader.cs` — boot-time loading
  - `shared/ContentValidator.cs` — YAML→defs consistency checks
  - `server/Net/Protocol.cs` — Protocol.MsgDefs wire format
  - `client/scripts/DefRegistry.cs` — client-side def subscription and caching
- **Related:** [[Def]], [[Protocol.MsgDefs]], [[Tech Tree]]
- **Notes:** Patchless runtime streaming; no client fallback (client holds authority until defs load)

### Def (Definition)
Compiled gameplay constant: hull stats, weapon stats, tech gating, prices, etc. Streamed from server to clients via MsgDefs.
- **Frequency:** Very common
- **Key Files:**
  - `shared/Defs.cs` — core def table registry and subscriptions
  - `client/scripts/DefRegistry.cs` — client caching and subscription logic
  - `server/Net/Protocol.cs` — MsgDefs serialization
- **Related:** [[YAML Content Pipeline]], [[Tech Tree]], [[Hull]], [[Weapon]]
- **Notes:** Immutable after server boot; clients guard all gameplay until defs load

### Hull
Playable ship chassis with base stats (armor, speed, turn-rate) and hardpoints for weapons/payloads.
- **Frequency:** Very common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Hulls/Hull.cs` — hull data model
  - `server/Content/factions/*.yaml` — hull definitions per faction
  - `client/scripts/ShipController.cs` — hull selection UI
  - `tools/ship-gen/` — modular hull generation from YAML parts
- **Related:** [[Weapon]], [[Payload]], [[Docking]]
- **Notes:** Immutable after game start; each hull has unique GLB 3D model and collision shape

### Weapon
Armament with barrel, fire-rate, projectile type, and damage tuning.
- **Frequency:** Very common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Parts/Weapon.cs` — weapon model
  - `server/Content/factions/*.yaml` — weapon definitions
  - `client/scripts/WeaponController.cs` — firing logic
  - `shared/FlightModel.cs` — barrel velocity and spread calculations
- **Related:** [[Hull]], [[Projectile]], [[Blast Radius]]
- **Notes:** Barrel-seed alignment: author weapon racks AFTER guns in YAML for consistent spawn positions

### Payload
Cargo, expendables, or equipment slot on a hull (e.g., missiles, chaff, fuel pods).
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Parts/Payload.cs` — payload model
  - `server/Content/factions/*.yaml` — payload definitions
  - `client/scripts/LoadoutController.cs` — loadout UI
- **Related:** [[Hull]], [[Missile]], [[Chaff]], [[Expendables]]
- **Notes:** Consumable slots track ammo; non-consumable payloads are always active

### Expendables
Single-use consumables (missiles, chaff, fuel boost) with limited ammo count.
- **Frequency:** Common
- **Key Files:**
  - `factions/src/Allegiance.Factions/Model/Expendables/` — all expendable types
  - `server/Sim/Simulation.cs` — expendable consumption and effect logic
- **Related:** [[Missile]], [[Chaff]], [[Payload]]
- **Notes:** Ammo tied to player ship; respawn restocks loadout

### Tech Tree
Unlock progression system: techs gate hull/weapon/payload availability; advancing development lines unlocks tiers.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/factions/*.yaml` — tech tree structure
  - `factions/src/Allegiance.Factions/Resolution/TechResolver.cs` — tech unlock logic
  - `shared/Defs.cs` — tech table registry
- **Related:** [[YAML Content Pipeline]], [[Def]], [[Hull]], [[Weapon]]
- **Notes:** Server-side gates available items; clients only render unlocked techs; tech data is a def

---

## Networking & Protocol

### Protocol
Binary wire format with quantized/compressed snapshots, separate missile stride, WebRTC/WebSocket dual transport.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Protocol.cs` — message definitions and serialization
  - `client/scripts/GameNetClient.cs` — deserialization and state application
  - `shared/WireQuant.cs` — quantization (f16 compression)
- **Related:** [[MsgSnapshot]], [[MsgMissiles]], [[WebRTC]]
- **Notes:** Little-endian, delta-encoded snapshots; missiles in separate MsgMissiles (never extend MsgSnapshot)

### MsgSnapshot
Quantized world state: player positions, rotations, velocities, health, weapons state.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgSnapshot structure and serialization
  - `client/scripts/GameNetClient.cs` — snapshot application and reconciliation
  - `server/Sim/Simulation.cs` — snapshot generation per SimTick
- **Related:** [[Protocol]], [[WireQuant]], [[AOI]]
- **Notes:** Sent once per SimTick to clients within AOI; quantized positions use f16

### MsgMissiles
Separate protocol message for active missiles; never packed into ship snapshots.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgMissiles structure
  - `client/scripts/GameNetClient.cs` — missile state application
  - `server/Sim/Simulation.cs` — missile lifecycle updates
- **Related:** [[Missile]], [[MsgSnapshot]], [[Protocol]]
- **Notes:** Proto v15: separate stride prevents missile data bloat; missiles sent per-missile once per tick

### WireQuant (Wire Quantization)
Half-precision (f16) floating-point compression for network transmission of velocities, power levels, and health.
- **Frequency:** Common
- **Key Files:**
  - `shared/WireQuant.cs` — f16 encoding/decoding
  - `server/Net/Protocol.cs` — applied to position/velocity in MsgSnapshot
- **Related:** [[MsgSnapshot]], [[Protocol]]
- **Notes:** Trades ~1.5% accuracy for 50% bandwidth savings; safe for physics/visuals

### WebRTC
Peer-to-peer data channel transport; preferred over WebSocket for lower latency and direct connectivity.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/WebRtcListener.cs` — server-side WebRTC peer handler
  - `client/scripts/GameNetClient.cs` — client WebRTC data channel logic
  - `public-lobby/Signaling.cs` — SDP offer/answer relay
  - `shared/Net/WebRtcSdp.cs` — SDP parsing
- **Related:** [[DIRECT-FIRST]], [[Public Lobby]], [[Signaling]]
- **Notes:** Dual WS/WebRTC; DIRECT-FIRST: answerer ondatachannel fires already-open, attach onmessage early or Hello drops

### DIRECT-FIRST
Reachability probe strategy: attempt direct P2P connection first; only fall back to relay if P2P fails.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/ReachabilityProbe.cs` — STUN probing logic
  - `public-lobby/Signaling.cs` — fallback routing
  - `server/Net/WebRtcListener.cs` — direct accept handling
- **Related:** [[WebRTC]], [[Public Lobby]]
- **Notes:** NO TURN server; reintroducing TURN would add latency and cost

### Join Token
HMAC-SHA256 signed authorization: epoch + expiry + team + faction. Server verifies at connection handshake.
- **Frequency:** Common
- **Key Files:**
  - `shared/JoinTokens.cs` — token generation and validation
  - `server/Net/ClientHub.cs` — join validation
  - `public-lobby/PublicLobby.cs` — token issuance
- **Related:** [[MsgWelcome]], [[Team]]
- **Notes:** Prevents replay attacks and unauthorized teams; expiry ~5 minutes

### MsgWelcome
Handshake message from server to client: assigns player ID, initial ship, world state snapshot, reconnect token.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Protocol.cs` — MsgWelcome structure
  - `client/scripts/GameNetClient.cs` — welcome handler and world rebuild
  - `server/Net/ClientHub.cs` — welcome generation
- **Related:** [[Reconnect Grace]], [[MsgSnapshot]]
- **Notes:** Triggers client world rebuild; reconnect token valid for 5s (ship held server-side during grace period)

### Reconnect Grace
5-second window after disconnect: server holds dropped ship state; client can reconnect and resume without respawn.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/ClientHub.cs` — reconnect token validation and ship reclaim
  - `client/scripts/GameNetClient.cs` — reconnect logic
  - `server/Net/Lobby.cs` — ship state caching
- **Related:** [[MsgWelcome]], [[Join Token]]
- **Notes:** Proto v9+; voluntary leave must send MsgBye to release ship immediately

---

## Client Rendering & UI

### Client Prediction
Client-side extrapolation of ship state between server snapshots to reduce perceived latency.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/PredictionController.cs` — prediction state and reconciliation
  - `client/scripts/RemoteShip.cs` — remote ship interpolation
  - `client/scripts/WorldRenderer.cs` — frame rendering
- **Related:** [[Flight Model]], [[Held-Input Replay]], [[MsgSnapshot]]
- **Notes:** Never blocks authority; server snapshot always wins

### WorldRenderer
Master 3D scene renderer: camera, world geometry, ships, projectiles, effects.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/WorldRenderer.cs` — main rendering orchestrator
  - `client/scripts/CameraRig.cs` — camera control and follow logic
  - `client/scripts/ShipController.cs` — local player ship rendering
  - `client/scripts/RemoteShip.cs` — remote ship rendering
- **Related:** [[GLB]], [[Collision]], [[Client Prediction]]
- **Notes:** Uses Godot 4.6 .NET; GLB models imported per-hull

### GLB
3D model format (glTF binary): embeds textures, used for hulls, asteroids, and collision geometry.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/GlbLoader.cs` — client-side GLB loading
  - `server/Assets/SimAssets.cs` — server-side GLB reading for collision
  - `shared/Collision/GlbReader.cs` — GLB parsing
  - `tools/ship-gen/` — hull GLB generation
- **Related:** [[Hull]], [[Collision]], [[SimModel]]
- **Notes:** Commit ONLY the .glb (embeds textures); .import sidecars are gitignored; run `godot --headless --import` for Godot import cache

### SimModel
Server-side collision representation: convex hulls and docking hardpoints extracted from GLB.
- **Frequency:** Common
- **Key Files:**
  - `shared/Collision/SimModel.cs` — hull + docking geometry
  - `server/Assets/SimAssets.cs` — cached .simmodel loading
  - `shared/Collision/ConvexHull.cs` — QuickHull hull generation
- **Related:** [[GLB]], [[Collision]], [[Hull]]
- **Notes:** Uncommitted .simmodel cache in `<binary>/sim-cache/`; ships exit via DockingExit

### Client-Side Hit Sparks
Visual hit feedback: spawned client-side on projectile collision, not server-driven.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ProjectileView.cs` — hit effect spawning
  - `client/scripts/SfxManager.cs` — impact audio playback
  - `server/Net/Protocol.cs` — ShotResolution message confirms hit
- **Related:** [[Projectile]], [[ShotResolution]], [[VFX]]
- **Notes:** Client visual interception; server never deletes/reduces health client-side; friendly fire sparks too

### VFX (Visual Effects)
Screen-space and world-space visual feedback: explosions, engine glow, hit flashes, lens flares.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ExplosionEffect.cs` — explosion particles
  - `client/scripts/EngineGlow.cs` — engine thrust glow
  - `client/scripts/HitFlash.cs` — hull damage flash
  - `client/scripts/LensFlare.cs` — optical effects
- **Related:** [[Client-Side Hit Sparks]], [[SfxManager]]
- **Notes:** Hooked into AddBolt/DeleteShip collision checks; spatial audio tied to world position

### DesignTokens
Godot centralized UI palette and type scale: colors, fonts, sizes. Source of truth for visual theme.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/ui/DesignTokens.cs` — token definitions
  - `client/scripts/ui/UiFonts.cs` — font asset loading
  - `DESIGN.md` — palette and type documentation
- **Related:** [[UI Components]], [[UiKit]], [[Theme]]
- **Notes:** Applied per-overlay (not globally); ChamferButton and all UI elements derive from tokens

### UI Components
Reusable Godot .NET UI controls: ChamferButton, BracketPanel, ModalHost, SettingsDialog, etc.
- **Frequency:** Very common
- **Key Files:**
  - `client/scripts/ui/` — all component classes
  - `client/scripts/ui/ChamferButton.cs` — custom-draw retro button
  - `client/scripts/ui/ModalHost.cs` — overlay layer manager
  - `client/scenes/UiShowcase.tscn` — live gallery (F9 in-game)
- **Related:** [[DesignTokens]], [[DESIGN.md]], [[UiKit]]
- **Notes:** Never hardcode colors/fonts; derive from DesignTokens; add new components to UiShowcase.tscn

### UiKit
Static factory helper for stock UI controls: `MakeButton`, `MakeSlider`, `MakeToggle`, etc.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ui/UiKit.cs` — factory methods
- **Related:** [[UI Components]], [[DesignTokens]]
- **Notes:** Use `UiKit.MakeLabel(text, TextStyle, color?)` instead of setting fonts by hand

### ModalHost
Overlay layer manager: manages z-order for menus, dialogs, settings.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ui/ModalHost.cs` — layer orchestration
  - `client/scripts/ui/SettingsDialog.cs` — settings UI
  - `client/scripts/ui/EscapeMenu.cs` — escape menu
- **Related:** [[UI Components]], [[DesignTokens]]
- **Notes:** Layer 200 for modals; SettingsDialog uses live write-through + snapshot revert

### Theme
Per-overlay Godot UI theme application; not global.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/ui/DesignTokens.cs` — theme wiring
  - `DESIGN.md` — theme application rules
- **Related:** [[DesignTokens]], [[UI Components]]
- **Notes:** Call `UiTheme.Apply(control)` on each full-screen overlay root; cannot live on CanvasLayer

### Zoom Mode (Telescopic Scope)
Circular picture-in-picture magnifier centred on screen: a second Camera3D renders the live world (shared World3D, narrow FOV) into a SubViewport, drawn clipped to a disc in place of the SystemRing gauges. `+`/`KpAdd` opens at 5x and steps 5→10→20 (capped); `−`/`KpSubtract` steps down and closes below 5x; Esc dismisses. Mouse-look sensitivity is divided by the magnification for fine aim.
- **Frequency:** Occasional
- **Key Files:**
  - `client/scripts/ZoomView.cs` — the scope (SubViewport + narrow-FOV camera, circular draw, input, `Active`/`Magnification` statics)
  - `client/scripts/Hud.cs` — instantiates it after WeaponsPanel
  - `client/scripts/SystemRing.cs` / `VelocityIndicator.cs` / `TargetMarkers.cs` — hidden while `ZoomView.Active`
  - `client/scripts/ShipController.cs` — Esc bail-out + mouse gain ÷ `ZoomView.Magnification`
- **Related:** [[UI Components]], [[DesignTokens]]
- **Notes:** Scope FOV = 2·atan(tan(75°/2)/M); shares the main World3D (split-screen idiom), never OwnWorld3D (that's the hangar preview)

---

## Server Architecture

### Lobby
In-game social state: player roster, team assignment, ready status, faction selection.
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Lobby.cs` — lobby state and player management
  - `server/Net/LobbyRegistrar.cs` — multi-lobby registry
  - `client/scripts/Lobby.cs` — client-side lobby UI
- **Related:** [[Team]], [[Faction]], [[Ready State]]
- **Notes:** Server-hosted; no external DB; team/ready state replicated to all clients

### Team
Player faction assignment: team 0 (Faction0, blue) or team 1 (Faction1, red).
- **Frequency:** Very common
- **Key Files:**
  - `server/Net/Lobby.cs` — team assignment logic
  - `shared/Defs.cs` — team constant definitions
  - `client/scripts/ui/DesignTokens.cs` — faction color mapping
- **Related:** [[Faction]], [[Join Token]]
- **Notes:** Immutable after game start; faction colors are Faction0 (blue) / Faction1 (red); TeamAccent (cyan) is chrome only

### Faction
Gameplay variant: faction-specific hulls, weapons, techs. Loaded from YAML at server boot.
- **Frequency:** Very common
- **Key Files:**
  - `server/Content/factions/*.yaml` — all faction content
  - `server/Content/FactionStart.cs` — faction startup
  - `factions/src/Allegiance.Factions/Model/Factions/Faction.cs` — faction model
- **Related:** [[YAML Content Pipeline]], [[Tech Tree]], [[Hull]]
- **Notes:** Immutable after boot; factions are defs; team assignment determines which faction player uses

### Ready State
Player signal that they are prepared for match: used for team-balanced start conditions.
- **Frequency:** Common
- **Key Files:**
  - `server/Net/Lobby.cs` — ready flag and state machine
  - `client/scripts/Lobby.cs` — ready toggle UI
- **Related:** [[Team]], [[Lobby]]
- **Notes:** Server gates match start until all players ready

### PigBrain
Server-side AI decision system: 5 Hz decision tick, evaluates targets/actions, steers cached decisions.
- **Frequency:** Common
- **Key Files:**
  - `server/Sim/Simulation.Pig.cs` — PigBrainTick and decision logic
  - `server/Sim/PigDecision.cs` — steering action encoding
  - `server/Sim/Simulation.cs` — decision caching and re-steering
- **Related:** [[SimTick]], [[Flight Model]]
- **Notes:** Decoupled from SimTick (20 Hz vs 5 Hz); PigBrainTick evaluates fresh targets; SimTick re-steers from cache; safe to hot-swap scheduled table

---

## Public Lobby & Signaling

### Public Lobby
Standalone .NET web service: game server registry, WebRTC signaling relay, server browser UI.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/PublicLobby.cs` — main web service
  - `public-lobby/ServerRegistry.cs` — active server tracking
  - `public-lobby/Signaling.cs` — WebRTC SDP relay
  - Live: `wivuu-public-lobby-production.up.railway.app`
- **Related:** [[WebRTC]], [[DIRECT-FIRST]], [[Railway Deploy]]
- **Notes:** Separate from gameplay servers; handles discovery and P2P setup only

### ServerRegistry
Directory of active game servers: hostname, port, player count, faction mix.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/ServerRegistry.cs` — registry logic
  - `public-lobby/PublicLobby.cs` — registry queries
- **Related:** [[Public Lobby]]
- **Notes:** Periodically probed for health; stale entries auto-removed

### Signaling
WebRTC SDP offer/answer relay: matches peers for connection negotiation.
- **Frequency:** Common
- **Key Files:**
  - `public-lobby/Signaling.cs` — offer/answer routing
  - `server/Net/WebRtcListener.cs` — server-side SDP handler
  - `shared/Net/WebRtcSdp.cs` — SDP parsing
- **Related:** [[WebRTC]], [[DIRECT-FIRST]]
- **Notes:** Stateless relay; no TURN fallback in current design

---

## Testing & Validation

### Flight Model Determinism Test
Golden-file regression test: confirms server physics matches replayed client path deterministically.
- **Frequency:** Domain-specific
- **Key Files:**
  - `tests/FlightModelTest/` — test suite
  - `shared/FlightModel.cs` — golden reference
- **Related:** [[Flight Model]]
- **Notes:** All tests pass as of 2026-06-12; any failure is a real regression

### Missile Test
Validates missile mechanics: acceleration, seeker targeting, chaff substitution.
- **Frequency:** Domain-specific
- **Key Files:**
  - `tests/MissileTest/` — test cases
- **Related:** [[Missile]], [[Seeker]], [[Chaff]]

### Content Validator
Ensures YAML content compiles consistently: no missing references, type safety.
- **Frequency:** Common
- **Key Files:**
  - `shared/ContentValidator.cs` — validation logic
  - `server/Content/ContentLoader.cs` — pre-boot checks
- **Related:** [[YAML Content Pipeline]], [[Def]]
- **Notes:** Runs at server startup; aborts boot on schema violations

---

## Tools & Utilities

### ship-gen
YAML-to-GLB pipeline: converts modular hull part definitions into 3D models with baked PBR and HP (hardpoint) nodes.
- **Frequency:** Common
- **Key Files:**
  - `tools/ship-gen/` — generation logic
  - `server/Content/factions/*.yaml` — hull part definitions
- **Related:** [[GLB]], [[Hull]], [[SimModel]]
- **Notes:** Canonical scout/fighter/bomber/pod wired into ShipModelLoader; output embeds textures

### Spatial Audio
3D sound positioning: plays effects anchored to world position, pans and attenuates based on listener.
- **Frequency:** Common
- **Key Files:**
  - `client/scripts/SfxManager.cs` — PlayAt/PlayUi API
  - `client/scripts/VFX.cs` — effect hooks
  - `tools/sfx-gen/` — synthetic placeholder generation
- **Related:** [[VFX]], [[Client-Side Hit Sparks]]
- **Notes:** Hooked into AddBolt/DeleteShip/CheckBoltImpacts/EngineGlow; collisions+settings-UI deferred

---

## Common Pitfalls & Anti-Patterns

### STDB SQL Limitations
SpacetimeDB `sql` command has no ORDER BY; aggregates need explicit aliases.
- **Fix:** Cache keys in memory using commutative (XOR/sum) hashes, not order-dependent FNV
- **Key File:** `shared/Stdb/` — queries

### Iter() Order Instability
Iteration order over STDB tables is unstable across runs.
- **Fix:** Use commutative aggregation (XOR/sum) for cache keys; never rely on sequential order
- **Key File:** Various query loops

### Client No Baked Tuning Fallback
Client reads tuning only from subscribed def tables; no compile-time constant fallback.
- **Fix:** Guard (hold authority) until a def loads; never hardcode gameplay values
- **Key File:** `client/scripts/DefRegistry.cs`

### WorldConfig Cube-Law Asteroid Bloat
Asteroid count balloons with `WorldConfig` cube-law; O(entities×asteroids) collision pass.
- **Fix:** Route through per-sector spatial grid; see [[Asteroid Grid Broad-Phase]]
- **Key File:** `server/Sim/World.cs`

### SubViewport UI Gotchas
SubViewport needs explicit `World3D` for raycasts; SetAnchorsAndOffsetsPreset for code-built overlays.
- **Fix:** Assign a new World3D to each SubViewport; use preset helpers
- **Key File:** `client/scripts/ui/ModalHost.cs`

### Godot GLB Import Cache
Godot 4.6 requires explicit import of .glb files or silently falls back to engine font/placeholder.
- **Fix:** Run `godot --headless --import` after pulling new assets or changing .glb
- **Key File:** Build/CI workflows

---

## Related Documentation

- **[CLAUDE.md](CLAUDE.md)** — project-specific architecture notes and gotchas
- **[DESIGN.md](DESIGN.md)** — UI component library and design-system reference
- **[server/Content/factions/](server/Content/factions/)** — YAML content definitions (gameplay values)
- **[tests/](tests/)** — determinism and integration tests
