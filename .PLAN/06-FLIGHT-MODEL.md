# 06 — Flight Model

Allegiance's signature feel comes from a **linear-drag** flight model: thrust accelerates you,
drag opposes velocity, top speed is capped, but inertia still matters so you carry momentum
through turns. This is neither full Newtonian (no drag) nor arcade (instant stop). Get this
right before anything else — if flying an empty sector isn't satisfying, no networking or
strategy layer will rescue the game.

## The math

Per fixed timestep `dt` (= `1/TICK_RATE`, i.e. `1/20 = 0.05s`):

```
# Orientation: integrate angular input into the quaternion.
angularInput = (pitch, yaw, roll) * angularAccel     # per-axis, ship-local
angularVelocity += angularInput * dt
angularVelocity *= (1 - angularDrag * dt)            # damp toward zero when no input
orientation = normalize(orientation * quatFromAxisAngle(angularVelocity * dt))

# Linear: thrust is along ship-local axes, then drag, then clamp.
thrustVec_local = (strafeX, strafeY, thrust) * thrustAccel
thrustVec_world = orientation * thrustVec_local
velocity += thrustVec_world * dt
velocity *= (1 - linearDrag * dt)                    # linear drag term
if |velocity| > maxSpeed: velocity = normalize(velocity) * maxSpeed
position += velocity * dt
```

Notes:
- `linearDrag` and `maxSpeed` together define the feel. Drag pulls you toward a terminal
  velocity under constant thrust; `maxSpeed` is a hard clamp so combat stays bounded.
- Because drag is proportional to velocity, ships coast and decelerate smoothly when thrust
  is released, rather than stopping dead — this is the inertia that makes maneuvering a skill.
- Angular drag damps rotation so the ship settles rather than spinning forever.

## Shared, deterministic, identical on both sides

This integration must be implemented **once** in `shared/FlightModel.cs` and copied verbatim
into both `module/` (server authority) and `client/` (prediction). Requirements:

1. **Fixed timestep only.** Always integrate with the constant `dt`. Never pass Godot's
   variable frame `delta` into the authoritative or predicted path.
2. **Same math, same order of operations.** Floating-point order matters for reconciliation
   tolerance. Keep the function byte-for-byte identical between the two copies.
3. **Pure function.** `Integrate(state, input, classStats, dt) -> newState` with no hidden
   globals, no randomness, no time reads. Given the same inputs it must produce the same
   output on client and server.

## Suggested signature

```csharp
public struct ShipState {
    public Vector3 Pos;
    public Vector3 Vel;
    public Quaternion Rot;
    public Vector3 AngVel;
}

public struct ShipInputState {
    public float Thrust, StrafeX, StrafeY, Yaw, Pitch, Roll;
    public bool Firing;
}

public struct ShipStats {
    public float ThrustAccel, MaxSpeed, LinearDrag, AngularAccel, AngularDrag;
}

public static class FlightModel {
    public const float TickRate = 20f;
    public const float Dt = 1f / TickRate;

    public static ShipState Integrate(ShipState s, ShipInputState i, ShipStats st) {
        // implement exactly the math block above; return the new state
    }

    public static ShipStats StatsFor(ShipClass c) { /* return the constants from doc 03 */ }
}
```

> Use the same vector/quaternion type semantics on both sides. Godot's `Vector3`/`Quaternion`
> and the module's math types may differ in API; if so, implement a tiny self-contained math
> struct in `shared/` so the two copies are truly identical and don't depend on engine types.
> Record that decision in `99` if you go this route.

## Tuning loop
Expose the per-class stats as the constants table in `03`. Expect to spend real time tuning
`ThrustAccel`, `MaxSpeed`, and `LinearDrag` against feel. The first acceptance gate (`09`,
Task 4) is subjective: "flying the Scout around asteroids for two minutes is fun." Treat that
gate seriously — it is the whole foundation.
