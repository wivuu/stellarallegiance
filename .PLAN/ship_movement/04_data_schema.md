# Per-Ship Data Schema & Derivation

The engine consumes a binary `DataHullTypeIGC` (layout in `03_constants_and_enums.md`). It is built
from a human-authored source — either a SQL `ShipTypes` table or a `TypesOfShips.csv` file shipped
with a game "core". **Those data files are not in the source repo**; this page gives you the exact
schema and conversion formulas so you can read any published core's `TypesOfShips.csv`, or author your
own ship table for the homage.

---

## Source column → engine field mapping (with conversions)

These are the conversions the server applies when loading the `ShipTypes` table into `DataHullTypeIGC`:

| Source column | Engine field | Conversion / meaning |
|---|---|---|
| `flWeight` | `mass` | raw. **Collisions only** (cancels from accel/turn — see below). |
| `SpeedMax` | `speed` (maxSpeed) | raw. Terminal speed. |
| `flAcceleration` | `thrust` | `thrust = mass * flAcceleration`. Forward acceleration. |
| `flRateYaw` (deg/s) | `maxTurnRates[Yaw]` | `× π/180` → rad/s. Max turn velocity. |
| `flRatePitch` (deg/s) | `maxTurnRates[Pitch]` | `× π/180` → rad/s. |
| `flRateRoll` (deg/s) | `maxTurnRates[Roll]` | `× π/180` → rad/s. |
| `flDriftYaw` (deg) | `turnTorques[Yaw]` | `mass * (flRateYaw² / (2·flDriftYaw)) · π/180` |
| `flDriftPitch` (deg) | `turnTorques[Pitch]` | `mass * (flRatePitch² / (2·flDriftPitch)) · π/180` |
| `flDriftPitch` (deg)¹ | `turnTorques[Roll]` | `mass * (flRateRoll² / (2·flDriftPitch)) · π/180` |
| `flAccelSideMult` | `sideMultiplier` | strafe thrust fraction (<1). |
| `flAccelBackMult` | `backMultiplier` | reverse thrust fraction (<1). |
| `BaseSignature` | `signature` | `/100`. Detectability, not motion. |
| `RangeScanner` | `scannerRange` | sensors. |
| `EnergyMax` | `maxEnergy` | weapon/AB energy pool. |
| `RateRechargeEnergy` | `rechargeRate` | energy regen. |
| `MaxFuel` | `maxFuel` | afterburner fuel. |
| `ecm` | `ecm` | countermeasures. |
| `flLength` | `length` | collision/visual size. |
| `HitPoints` / `DefenseType` | `hitPoints` / `defenseType` | durability. |
| `ripcordSpeed` | `ripcordSpeed` | `× c_fcidRipcordTime` (constant) → warp charge time. |
| `ripcordCost`, `MaxAmmo`, `Capabilities`, part masks | loadout/abilities | not motion. |

¹ Roll torque deliberately reuses `flDriftPitch` in the original server code.

### `flDrift` intuition

`flDrift` is the angle (degrees) a ship would **drift/overshoot** while decelerating from full turn
rate to a stop. From rotational kinematics `ω² = 2·α·θ`, the angular acceleration is:

```
α = flRate² / (2 · flDrift)        (deg/s²; ×π/180 → rad/s²)
```

Small `flDrift` → snappy direction changes. Large `flDrift` → wallowy, floaty turning. This single
number is the biggest lever on how "tight" a ship feels.

---

## Why mass cancels out of flight feel

Both linear and angular acceleration are **independent of mass**:

**Linear:** `Δv = thrustToVelocity · thrust = (dT/mass)·(mass·flAcceleration) = dT·flAcceleration`
→ effective acceleration is exactly `flAcceleration`.

**Angular:** `maxDelta = (TorqueMult·dT/mass)·(mass·flRate²/(2·flDrift)·π/180)`
`= TorqueMult·dT·flRate²/(2·flDrift)·π/180`
→ angular acceleration is `TorqueMult·flRate²/(2·flDrift)`.

**For the homage:** you can drive the entire flight feel from `SpeedMax`, `flAcceleration`,
`flRate{Yaw,Pitch,Roll}`, `flDrift{Yaw,Pitch}`, `flAccelSideMult`, `flAccelBackMult`. Keep `mass`
only if you simulate ramming/collisions (it governs momentum transfer there).

---

## `TypesOfShips.csv` column order

The CSV loader reads columns positionally (1-indexed). **Note:** in the CSV path the turn-rate and
turn-torque columns are stored **already converted** (radians / final torque), space-separated as
`yaw pitch roll` — unlike the SQL path which converts from degrees. Column order:

| # | Field | Notes |
|---|---|---|
| 1 | hullID | unique ship id |
| 2 | mass | `flWeight` |
| 3 | signature | |
| 4 | speed | `SpeedMax` |
| 5 | maxTurnRates | space-separated `yaw pitch roll` (radians/s) |
| 6 | turnTorques | space-separated `yaw pitch roll` |
| 7 | thrust | Newtons |
| 8 | sideMultiplier | |
| 9 | backMultiplier | |
| 10 | scannerRange | |
| 11 | maxFuel | |
| 12 | ecm | |
| 13 | length | |
| 14 | maxEnergy | |
| 15 | rechargeRate | |
| 16 | ripcordSpeed | |
| 17 | ripcordCost | |
| 18 | maxAmmo | |
| 19 | successorHullID | |
| 20 | maxWeapons | hardpoint data follows after this |

Sibling CSVs in the same core folder: `Hardpoints.csv`, `ShipsSFX.csv`, `Constants.csv` (global float
constants), `Afterburners.csv`, plus expendables/weapons tables.

---

## Ship roster

Gameplay ship classes enumerated in the codebase: **Scout · Fighter · Interceptor · StealthFighter ·
Bomber · Gunship**. The full game adds capital ships, miners, constructors, layers, etc. — all just
more rows in the same `ShipTypes` table with identical physics.

Qualitative feel by class (design intent — tune the schema knobs to match; real numbers need a core):

| Class | maxSpeed | accel | turn rate | drift (agility) | strafe/back | role |
|---|---|---|---|---|---|---|
| Scout | high | high | high | low (snappy) | decent | recon, fast & fragile |
| Interceptor | very high | very high | high | low | low | anti-fighter sprinter |
| Fighter | medium | medium | medium | medium | medium | all-rounder |
| StealthFighter | medium | medium | medium | medium | medium | low-signature variant |
| Bomber | low | low | low | high (wallowy) | low | anti-capital ordnance |
| Gunship | low-med | low | low-med | medium-high | medium | durable heavy weapons |

---

## Authoring your own ship (minimal set)

To define a ship for the homage you need just these eight numbers:

```yaml
maxSpeed:        # terminal velocity (world units/s)
acceleration:    # forward accel (world units/s²)   -> thrust internally
rateYaw:         # deg/s  max yaw rate
ratePitch:       # deg/s  max pitch rate
rateRoll:        # deg/s  max roll rate
driftYaw:        # deg    yaw overshoot angle  (smaller = snappier)
driftPitch:      # deg    pitch/roll overshoot angle
sideMultiplier:  # 0..1   strafe thrust fraction
backMultiplier:  # 0..1   reverse thrust fraction
```

Feed them into `05_reference_implementation.py` (which does the deg→rad and α derivation for you).
