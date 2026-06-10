# The Flight Model — per-tick integration

This is the heart of Allegiance's feel. One function runs every physics tick and updates a ship's
orientation and velocity. Verbatim engine code is in `source_excerpts/ExecuteShipMove.cpp`; a
runnable port is `05_reference_implementation.py`. This file explains it step by step.

`dT = timeStop - timeStart` (seconds). Conventions: **ship-local axes** are Right(x), Up(y),
Forward(-z) — i.e. forward thrust is **negative z** in local space. See `02_rotation_math.md`.

## Symbols

| Symbol | Meaning | Source |
|---|---|---|
| `dT` | tick duration, seconds | frame time |
| `mass` | hull mass | `DataHullTypeIGC.mass` |
| `thrust` | engine force (Newtons) | `GetThrust()` |
| `maxSpeed` | terminal speed | `GetMaxSpeed()` |
| `MaxTurnRate[axis]` | max angular velocity, rad/s | `GetMaxTurnRate(axis)` |
| `TurnTorque[axis]` | angular-accel term | `GetTurnTorque(axis)` |
| `SideMultiplier` | strafe thrust scale (<1) | `GetSideMultiplier()` |
| `BackMultiplier` | reverse thrust scale (<1) | `GetBackMultiplier()` |
| `thrustToVelocity` | `dT / mass` (force→Δv) | computed |
| `turnRate[axis]` | persistent current angular velocity | ship state |
| `stick[axis]` | input −1..1 (yaw,pitch,roll,throttle) | `ControlData.jsValues` |

---

## Step 1 — Rotation (rate- and acceleration-limited)

```text
# Stick (yaw, pitch, roll) is constrained to a SPHERE, not a box:
l = yaw² + pitch² + roll²
if l > 1:  l = 1/sqrt(l)        # diagonal input never exceeds full single-axis deflection
else:      l = 1

tm = TorqueMultiplier * thrustToVelocity         # TorqueMultiplier: 0.5 at rest → 1.0 at max speed

for axis in (Yaw, Pitch, Roll):
    desiredRate = stick[axis] * l * MaxTurnRate[axis]   # commanded angular velocity
    maxDelta    = tm * TurnTorque[axis]                 # max change to rate this tick

    # slew current rate toward target, clamped by maxDelta (this is the inertia):
    if   desiredRate < turnRate[axis] - maxDelta:  turnRate[axis] -= maxDelta
    elif desiredRate > turnRate[axis] + maxDelta:  turnRate[axis] += maxDelta
    else:                                          turnRate[axis]  = desiredRate

# Apply to attitude (PITCH IS NEGATED), then re-orthonormalize:
orientation.Yaw(   turnRate[Yaw]   * dT)
orientation.Pitch(-turnRate[Pitch] * dT)
orientation.Roll(  turnRate[Roll]  * dT)
orientation.Renormalize()
```

Feel notes:
- `turnRate` **persists between ticks** — releasing the stick doesn't stop you instantly; the rate
  decays toward 0 at `maxDelta` per tick. This is the rotational-inertia signature.
- Input clamped to a **unit sphere**: a full yaw+pitch diagonal is no faster than a pure yaw.
- All maneuvering is disabled while ripcording (warping).

### TorqueMultiplier (speed-dependent agility)

```text
fraction = |velocity| / maxSpeed
TorqueMultiplier = 0.5 + 0.5 * (2*fraction / (fraction + 1))
```

| Speed | fraction | TorqueMultiplier |
|---|---|---|
| Stationary | 0.0 | 0.50 |
| Half max | 0.5 | ~0.83 |
| Max speed | 1.0 | 1.00 |

Only angular **acceleration** scales with speed; the **max rate** is constant. Ships feel locked-up
sitting still and crisp at speed.

---

## Step 2 — Drag (defines terminal speed)

```text
f    = exp( -thrust * thrustToVelocity / maxSpeed )   # = exp(-thrust*dT/(mass*maxSpeed))
drag = velocity * (1 - f) / thrustToVelocity          # a force opposing current velocity
```

Velocity-proportional drag tuned so that at full forward thrust, speed asymptotes to `maxSpeed`.
No hard clamp — you can briefly exceed `maxSpeed`, then drag bleeds it off. Closed form:
`V(t) = V0·exp(-k·t)` with `k = thrust/(maxSpeed·mass)` (see `03_constants_and_enums.md`).

---

## Step 3 — Afterburner (if mounted)

```text
abThrust = afterburner.maxThrust (× GA)
if held:  thrustRatio = abThrust / thrust
power ramps 0..1 (power += dT*onRate when held, -= dT*offRate when released)
drag += (power * abThrust) * shipBackward      # folded into drag so it feeds the engine calc
```

While held, the effective target speed rises to `maxSpeed * (1 + thrustRatio)`.

---

## Step 4 — Engine thrust direction

Two control modes:

**Manual strafe (direction buttons pressed):** thrust is `±thrust` on each pressed local axis.
```text
x = (-1 if left)  + (+1 if right)
y = (-1 if down)  + (+1 if up)
z = (+1 if back)  + (-1 if forward)     # forward = negative z
localThrust = (thrust*x, thrust*y, thrust*z)   # in ship-local space
```

**Throttle / coast (no strafe buttons):**
```text
if coasting (and not afterburning):
    localThrust = orientation.toLocal(drag)            # exactly cancel drag → hold velocity
else:
    if afterburning: negDesiredSpeed = maxSpeed * (-1 - thrustRatio)
    else:            negDesiredSpeed = -0.5*(1 + throttle) * max(speed, maxSpeed)
    desiredVelocity = shipBackward * negDesiredSpeed    # backward * negative = forward motion
    localThrust = orientation.toLocal((desiredVelocity - velocity)/thrustToVelocity + drag)
```
`throttle` ∈ [-1, 1]; `-0.5*(1+throttle)` maps it to [0, -1] × speed along forward. Throttle resting
at -1 → `negDesiredSpeed = 0` (no thrust); throttle at +1 → full forward.

---

## Step 5 — Directional thrust clipping (forward is strongest)

```text
scaled.x = localThrust.x / SideMultiplier            # strafe penalized
scaled.y = localThrust.y / SideMultiplier
scaled.z = localThrust.z <= 0 ? localThrust.z        # forward (z<=0): full strength
                              : localThrust.z / BackMultiplier   # reverse penalized

if |scaled|² > thrust²:                              # exceeds engine capacity → clip
    engineVector = worldThrust * (thrust / |scaled|)
else:
    engineVector = worldThrust                       # worldThrust = localThrust * orientation
```

Because strafe/reverse are *divided* by their (<1) multipliers before the magnitude check, you get
less real thrust in those directions. Forward (z ≤ 0) is unpenalized.

---

## Step 6 — Integrate velocity

```text
velocity += thrustToVelocity * (engineVector - drag)
```

That's the whole loop. The eight conceptual steps (input clamp → torque-limited turn → apply attitude
→ drag → afterburner → thrust direction → clip → integrate) **are** the Allegiance flight feel.
