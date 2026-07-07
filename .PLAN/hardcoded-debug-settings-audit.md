# Hardcoded debug/tuning settings → YAML migration audit

Goal: find settings hardcoded for debug/playtest/tuning purposes that should eventually be
authored in the YAML content pipeline (`world.yaml`, map env blocks, hull/weapon/expendable defs)
instead of baked into code.

Six Haiku agents swept the sim core, vision/AI, mines/chaff/flight-model, networking, client
scripts, and content loaders. ~80 candidates found. This report ranks them by migration value.
Line numbers verified for the Tier-1 items; treat the rest as leads to re-confirm at edit time.

Precedent: `world.yaml` already hosts debug toggles (`debug-freeze-brain`, `debug-no-fire`) and
vision knobs (`fog-eyeball-multiplier`, `fire-signature-boost`, `fire-signature-window`) — so
gameplay/AI/vision tuning clearly belongs there. Env vars (`SIM_*`) are a half-measure: externalized
but not content-authored, not streamed, and require a restart.

---

## Tier 0 — smoking guns (explicit markers / real inconsistencies)

| Where | What | Note |
|---|---|---|
| `server/Sim/World.cs:32` | `CollisionDamageMinSpeed = 4f` | Comment literally says `// ponytail: tune knob; raise to make hulls more forgiving`. A designer already flagged this as a knob. |
| `client/scripts/BaseModelLoader.cs:40` vs `client/scripts/WorldRenderer.cs:1529,1556` | Base-radius fallback = `90f` in one place, `45f` in two others | **These disagree.** Both are "def hasn't streamed yet" fallbacks for the same base radius. Violates the repo's own "client: no baked-tuning fallback" rule and they're inconsistent. Bug-adjacent — worth fixing regardless of YAML. |
| `server/Sim/World.cs:55-61` | Asteroid field/belt params (`fill-frac`, `flatten`, `area-density` ×2 sectors) | Comment says per-sector belt overrides win, "each null field falls back to this compile-time constant." The override path exists — these are the leftover hardcoded defaults. |
| `client/scripts/TargetMarkers.cs:49-51` | `ProjectileSpeed=250f`, `NoseOffset=3f`, `MaxLeadTime=2.5f` | Comment says "Mirror the server / PredictionController muzzle constants." Hand-mirrored client copies of server weapon numbers — should derive from streamed `WeaponDef`, not be duplicated by hand. `NoseOffset=3f` also duplicated in `SystemRing.cs:21`, `ZoomView.cs:20`, and matches `World.cs:34`. |

---

## Tier 1 — high-value, clearly gameplay balance (server, not yet externalized)

### PIG AI tuning — `server/Sim/Simulation.Pig.cs` (the biggest cluster, ~27 constants)
The entire block `Simulation.Pig.cs:17-70` is AI tuning ported "verbatim from the module." All of it is
difficulty/behavior tuning a designer would sweep without recompiling. Highest-value group to move as a
single new `world.yaml` `ai:` section. Standouts:

- Decision cadence & squads: `PigBrainHz=5` (L17), `MaxPigsPerTeam=5` (L30), `PigSquadDelayTicks` (L31), `PigSpawnStaggerTicks=30` (L42), `PigBomberRespawnTicks` (L51)
- Ranges/engagement: `PigRadarRange=1200f` (L35), `PigFireRange=360f` (L36), `PigStandoff=90f` (L37), `PigBaseThreatRadius=700f` (L48)
- Skill/aim: `PigAimDeg=6f` (L38), `PigTurnGain=3.2f` (L39), aim-skill spread `PigTurnGainMin/Max`, `PigLeadFracMin/Max`, wobble (L54-59)
- Threat scoring weights (6 constants, L43-49) — target-priority formula, first knobs you'd touch for difficulty
- Avoidance/juke: `PigAvoidLookahead=160f` (L40), `PigAvoidMargin=14f` (L41), juke params `PigJukeRange/Period/Amp` (L67-70)
- Aggro/wander/missile: `PigAggroWindowTicks` (L32), `PigWanderPeriodTicks` (L50), `PigMissileHoldTicks` (L64)
- `NumTeams=2` (L29) — game-mode fundamental; med confidence

### Combat / world mechanics — `server/Sim/World.cs`
- Collision damage: `CollisionDamageScale=0.6f` (L28), `ShipShipDamageScale=1.2f` (L29), `MaxCollisionDamage=30f` (L30), `CollisionDamageMinSpeed=4f` (L32, Tier-0)
- Sector boundary hazard: `BoundaryBaseDps=8f` (L35), `BoundaryRampDps=0.12f` (L36), `BoundaryMaxDps=60f` (L37)
- Warp gates: `AlephTriggerRadius=18f` (L38), `WarpExitOffset=60f` (L39), `WarpExitJitter=0.12f` (L40)
- Sector default radii: `CoreRadius=2100f` (L43), `VergeRadius=700f` (L44) — already per-map overridable, these are the fallbacks
- Base spawn ranges (L218-219), rock size ranges core/verge `RockRadius(8,55)` / `(6,40)` (L425/L453), size skew `1.8` (L502), base Y-jitter `80.0` (L520)

### Core sim mechanics — `server/Sim/Simulation.cs`
- Economy: `PaycheckTicks = 60s` (L22)
- Docking/spawn: `DockRadiusFrac=0.9f` (L26), `LaunchSpeed=80f` (L27)
- Pods: `RescueRadius = ShipRadius*4` (L28), `PodEjectSpeed=90f` (L29), `PodEjectSpin=5f` (L30)
- Match flow: `GraceTicks=5s` reconnect (L312), `EndedToLobbyTicks=6s` (L384)

### Vision signatures — `server/Sim/Simulation.Vision.cs`
- Aleph radar signature `1.4f` (L618), rock radar signature `2.0f` (L807) — inline magic multipliers; peers already live in `world.yaml`

### Weapons/countermeasures — `server/Sim/Simulation.Mines.cs` / `.Chaff.cs`
- Mines: `MineSpeedRef=40f` (L17), `MineMaxSpeedMult=2.5f` (L19) → belong on the mine weapon/expendable def
- Chaff: velocity inheritance `0.5f` + backward kick `10f` (L66), drag `0.95f`/tick (L87), detonation buffer `+2f` (L124/149) → chaff expendable def

### Flight model — `shared/FlightModel.cs`
- `TorqueMultiplier()` agility curve, L461: `0.5 + 0.5*(2f/(f+1))` — angular accel scales 50%→100% with speed. Fundamental feel knob; belongs on hull def or `world.yaml` flight block.

---

## Tier 2 — networking / infra tunables (env-var escape hatch exists, YAML would be canonical)

### Server — `server/Net/ClientHub.cs` (AOI/LOD; all have `SIM_*` env defaults today)
`FullRateRadius=600f` (L27), `MidRateRadius=1500f` (L28), `MidEveryTicks=3` (L29), `CoarseEveryTicks=10` (L30),
`MaxRecords=96` (L31), `AoiGridCell=600f` (L38), `ParallelClientThreshold=24` (L43).

### Server — `server/Net/Protocol.cs` (fog reveal slice caps)
`RevealMaxBases=64` (L640), `RevealMaxRocks=512` (L641), `RevealMaxAlephs=64` (L642), `RevealMaxSectors=16` (L643).

### Server — `server/Program.cs`
`EmptyResetMs=5000` (L217) idle→lobby reset — the one net timing value with **no** externalization.

### Client — network prediction/interp (`ShipController.cs`, `RemoteShip.cs`, `PredictionController.cs`)
`DefaultTargetLead=3`, `SlewGain=0.08f`, `MaxSlew=0.30f` (ShipController L13-15); `InterpDelayMs=100`,
`MaxInterpDelayMs=800`, `GapDelayFactor=1.5f`, `GapEmaAlpha=0.3f`, `ClockOffsetAlpha=0.05f`, `VelSmoothRate=16f`
(RemoteShip); `PosTolerance=0.5f`, `RotTolerance=0.05f`, `BufferLen=40`, `SmoothFreq=14f` (PredictionController).
Reconnect `ReconnectInterval=1.5`, `ReconnectMax=20` (ConnectionManager L86-87). Shot-mask caps 400f/250f
(WorldRenderer L1511/1514, tied to `STDB_SHOT_MASK_MS`). Keepalive/ping cadence (ShipController L59/133).

---

## Tier 3 — content-loader hardcoded defaults & minor

- `MapLoader.cs:199-205` dust env defaults (`RadiusMin=300`, `RadiusMax=900`, `CoverageFrac=0.85`, `Flatten=0.15`, `Density=0.7`, `VisionMult=1`) **duplicated** in `shared/Defs.cs:311-317` (`SectorDust` struct). Pick one source of truth. `GodRays ?? 0f` default at L179.
- `MapCatalog.cs:80-82` map size-label thresholds `1500/3000/5000` (lobby UI buckets).
- `Simulation.Probes.cs:75` probe hit-radius fallback `4f`.
- Low/skip: `Defs.cs` `ShieldMult=1f` (L189), `ChargesPerPack=1` (L224) — neutral defaults, not debug knobs.
- Client visual FX (EngineGlow smoke params, mouse sensitivity/return) — input/visual feel, **not** game-config YAML.

---

## Suggested migration order

1. **Tier 0** — fix the `45f`/`90f` fallback disagreement now (it's a latent bug), and retire the
   hand-mirrored client weapon constants in favor of the streamed `WeaponDef`.
2. **PIG AI block → new `world.yaml` `ai:` section** — biggest single win, self-contained, explicitly "ported" tuning.
3. **World.cs combat/boundary/gate/pod knobs → `world.yaml`** — sibling to the collision/economy values.
4. **Belt/rock params → per-map belt overrides** (path already exists; just wire the fallbacks).
5. Tier 2 net tunables: promote `SIM_*` env defaults into `world.yaml` when the streamed-config plumbing is worth it.
