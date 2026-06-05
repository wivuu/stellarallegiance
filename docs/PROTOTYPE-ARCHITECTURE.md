# Stellar Allegiance — Prototype Architecture (Complete)

> **Status: COMPLETE** — All tasks T0–T10 finished (June 2026).
> This document consolidates the original `.PLAN/` build specs into a single
> reference for anyone reading the codebase. It does not define future work.

---

## 1. What the prototype is

A minimal vertical slice of an Allegiance-style team space combat game:

- **Two ship classes** — Scout (fast, fragile) and Fighter (tanky, slow).
- **One 3D sector** with 30 asteroids, two team bases.
- **Authoritative server** via SpacetimeDB 2.0 (C# → WASM module).
- **Godot 4 C# client** with local prediction, rollback reconciliation, and
  remote ship interpolation.
- **Win condition** — destroy the enemy base with projectiles.
- **Deployed to Maincloud** — playable over the internet.

### Explicitly out of scope (deferred to next milestone)

Commander/RTS view, multiple sectors/alephs, mining economy, constructors,
tech paths, matchmaking, accounts, persistence, art pass.

---

## 2. Architecture overview

```
┌────────────────────────────┐        WebSocket         ┌─────────────────────────────┐
│    Godot 4 client (C#)     │ ◄─── subscriptions ────► │      SpacetimeDB 2.0        │
│                            │                          │     (module = WASM)         │
│  • Renders the 3D sector   │ ───── reducer calls ───► │                             │
│  • Reads input             │                          │  Tables: authoritative state │
│  • Predicts local ship     │                          │  Reducers: the only writers  │
│  • Interpolates remote ships│                         │  Scheduled reducer: SimTick  │
└────────────────────────────┘                          └─────────────────────────────┘
       (one per player)                                      (one shared instance)
```

**Authority model:** The server owns all observable state. The client predicts
only the local player's ship motion (rendering convenience, not truth). On
divergence the client reconciles to the server's authoritative state via
rollback re-simulation.

---

## 3. Repository layout

```
stellar-allegiance/
├── docs/                        # This file + future docs
├── module/                      # SpacetimeDB C# server module
│   ├── StdbModule.csproj        # references ../shared/Shared.csproj
│   └── Lib.cs                   # Tables + reducers
├── shared/
│   ├── FlightModel.cs           # Deterministic flight math (the only copy)
│   └── Shared.csproj            # net8.0 lib referenced by module/, client/, tests/
├── client/                      # Godot 4.6 C# project
│   ├── project.godot
│   ├── module_bindings/         # Generated — never hand-edit
│   ├── scenes/
│   │   ├── Main.tscn
│   │   ├── Ship.tscn
│   │   └── Base.tscn
│   └── scripts/
│       ├── ConnectionManager.cs
│       ├── ShipController.cs
│       ├── PredictionController.cs
│       ├── RemoteShipInterpolator.cs
│       └── WorldRenderer.cs     # (flight math comes from shared/Shared.csproj)
├── scripts/
│   └── publish-maincloud.sh     # Build in Docker, publish via native CLI
├── tests/                       # FlightModel determinism + golden tests
└── HANDOFF.md                   # Detailed agent handoff doc
```

---

## 4. Toolchain

| Tool | Version | Notes |
|------|---------|-------|
| SpacetimeDB CLI | 2.3.0 | `spacetime --version` |
| .NET SDK | 8.0 | Module + Godot client |
| .NET WASI workload | experimental | Module → WASM compilation |
| Godot | 4.6.3 (.NET build) | C#-capable build required |

---

## 5. Data model (SpacetimeDB tables)

| Table | PK | Hot? | Notes |
|-------|----|------|-------|
| `Match` | `Id` (singleton, always 0) | Low | Tick counter, Phase, Winner, pacing fields |
| `Player` | `Identity` | Low | Team, ShipId, Online, Name |
| `Ship` | `ShipId` (autoinc) | **High** | Pos, Vel, Rot, AngVel, Health, LastInputTick, LastFireTick |
| `ShipInput` | `InputId` (autoinc) | **High** | Server-private per-tick input buffer (ShipId+Tick indexed) |
| `Base` | `BaseId` | Low | Team, Pos, Health |
| `Asteroid` | `AsteroidId` | Static | Pos, Radius (written once at Init) |
| `Projectile` | `ProjectileId` (autoinc) | High | Team, Pos, Vel, Damage, ExpiresAtTick |

### Enums
- `ShipClass`: Scout (0), Fighter (1)
- `MatchPhase`: Lobby (0), Active (1), Ended (2)

### Ship class stats

| Stat | Scout | Fighter |
|------|-------|---------|
| Max hull | 60 | 120 |
| Thrust accel | 45 | 30 |
| Max speed | 70 | 50 |
| Linear drag | 1.2 | 1.0 |
| Angular accel | 3.5 | 2.5 |
| Angular drag | 2.5 | 2.0 |
| Weapon damage | 4 | 10 |
| Fire interval (ticks) | 4 | 8 |

---

## 6. Reducers

| Reducer | Category | Frequency |
|---------|----------|-----------|
| `Init` | Lifecycle | Once (publish) |
| `ClientConnected` | Lifecycle | On connect |
| `ClientDisconnected` | Lifecycle | On disconnect |
| `SetName` | Player action | Rare |
| `SpawnShip(ShipClass)` | Player action | On spawn |
| `ApplyInput(...)` | Player action | ~20 Hz per player |
| `SimTick` | Scheduled | 20 Hz target (real-time paced) |

**SimTick** runs three passes per sub-step:
1. **Pass A** — Integrate ships + spawn projectiles (fire cooldown gated).
2. **Pass B** — Advance projectiles, resolve hits (ship + base), cull expired.
3. **Pass C** — Ship-ship / ship-asteroid / ship-base collisions, apply damage, kill at HP≤0.

SimTick is **real-time paced**: each call computes `elapsed/dt` sub-steps
(~2 at 10 Hz Maincloud scheduler, ~1 at 20 Hz local), carrying fractional
remainder, capped at 8. This solved the Maincloud half-speed bug.

---

## 7. Flight model

Linear-drag model per fixed timestep (`dt = 1/20`):

```
angularVelocity += angularInput * angularAccel * dt
angularVelocity *= (1 - angularDrag * dt)
orientation = normalize(orientation * quatFromAxisAngle(angularVelocity * dt))

thrustVec = orientation * (strafeX, strafeY, thrust) * thrustAccel
velocity += thrustVec * dt
velocity *= (1 - linearDrag * dt)
velocity = clamp(velocity, maxSpeed)
position += velocity * dt
```

**Determinism:** Uses `MathDet.Sin/Cos` (Taylor polynomial in plain float ops)
instead of `Math.Sin/Cos` — WASM and Mono libm differ in the last bits, which
integrated every tick caused rotation drift. IEEE `+,-,*,/,sqrt` are
bit-identical across runtimes and use `Math.Sqrt` directly.

---

## 8. Net-sync protocol

| Parameter | Value |
|-----------|-------|
| Sim tick rate | 20 Hz (`dt = 0.05s`) |
| Input send rate | 20 Hz |
| Render rate | Display refresh (decoupled) |
| Remote interpolation delay | 100 ms (~2 ticks) |
| Prediction target lead | 3 ticks (env `STDB_LEAD`, clamp 1..15) |
| Reconciliation tolerance | 1.0 u position, 0.05 rad rotation |

### Prediction & reconciliation

Client predicts local ship at 20 Hz using a **slewed clock** (rate nudged
±MaxSlew to hold TargetLead ticks ahead of server). On authoritative update:
compare predicted vs auth at `LastInputTick`; if within tolerance, accept; if
diverged, snap to auth and re-simulate all buffered inputs forward (rollback).

**Per-tick input buffer** ensures server replays the client's exact input
sequence → zero drift under normal conditions (confirmed 0 reconciles over
5 min continuous flight).

### Client-side fire prediction

Own muzzle flashes are predicted locally (same fire gate as server in
prediction tick space). Ghost projectiles are matched 1:1 to authoritative
rows as they stream in. Unmatched ghosts expire after 0.6s.

### Remote ships

Snapshot interpolation: buffer authoritative transforms, render at
`now - 100ms`, lerp/slerp between samples. No extrapolation.

---

## 9. Controls

| Key | Action |
|-----|--------|
| W / S | Throttle forward / back |
| A / D | Strafe left / right |
| E / C | Strafe up / down |
| Q / Z | Roll left / right |
| Arrow keys | Yaw / Pitch |
| Space / Left mouse | Fire |
| 1 / 2 | Spawn Scout / Fighter |
| P | (Debug) Inject divergence |
| `--maincloud` flag | Connect to Maincloud instead of localhost |

---

## 10. Key decisions & lessons learned

1. **Self-contained math types** — `Vec3`/`Quat` in `FlightModel.cs`, no
   Godot or System.Numerics dependency, so module and client are byte-identical.

2. **Deterministic trig** — Taylor polynomial `Sin/Cos` eliminated rotation
   drift between WASM and Mono runtimes.

3. **Per-tick input buffer** — Changed `ShipInput` from one-row-per-ship
   (overwritten) to per-tick keyed entries. Server replays exact client
   input sequence → zero steady-state divergence.

4. **Tick-aligned prediction clock** — Client `_predTick` anchored to
   `ServerTick` on spawn, advanced on a slewed local clock. Fixed the 8.8 u
   steady divergence from server running at ~18.7 Hz vs client at 20 Hz.

5. **Real-time paced SimTick** — Server runs `elapsed/dt` sub-steps per call
   instead of assuming one step per call. Fixed Maincloud running at half
   speed (scheduler fires at ~10 Hz, not 20 Hz).

6. **Docker build + native publish** — Native CLI can't install WASI workload;
   Docker image builds the WASM; native CLI publishes the prebuilt binary.

7. **Maincloud latency** — Measured ~115-125 ms RTT (full felt loop), notably
   higher than raw TCP ~32 ms. Lead=3 reduced reconcile rate 7× vs lead=1.

---

## 11. Deployment

**Local:**
```bash
spacetime start                    # Terminal 1
spacetime publish --project-path module stellar-allegiance  # Terminal 2
# Launch Godot client (connects to localhost:3000)
```

**Maincloud:**
```bash
scripts/publish-maincloud.sh       # Build in Docker, publish to Maincloud
# Launch client with: --maincloud  (or STDB_URI=wss://maincloud.spacetimedb.com)
```

Dashboard: `spacetimedb.com/stellar-allegiance`
