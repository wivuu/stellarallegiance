# Rotation / Attitude Math

The flight model stores ship attitude as a 3×3 rotation matrix (`Orientation`) and rotates it about
the ship's *own local axes* each tick. Verbatim engine code is in `source_excerpts/orientation.cpp`;
this file is the conceptual summary. If your engine has its own quaternion/matrix type, you only need
to match the **conventions and the per-tick operations**, not this exact matrix code.

## Conventions (critical to match)

The matrix rows are the ship's local axes expressed in world space:

| Row | Local axis | Accessor |
|---|---|---|
| 0 | Right (+x) | `GetRight()` |
| 1 | Up (+y) | `GetUp()` |
| 2 | **−Forward** | `GetBackward()` returns row 2; `GetForward()` = −row 2 |

So **forward is −z in local space.** This is why forward thrust uses negative z and why the throttle
code multiplies `shipBackward` by a negative desired speed to move forward.

- `worldVec = localVec * orientation` — maps a ship-local vector into world space.
- `localVec = orientation.TimesInverse(worldVec)` — maps world → ship-local (transpose).

## Per-tick rotations

`Yaw(θ)`, `Pitch(θ)`, `Roll(θ)` each **left-multiply** the orientation by an axis rotation, i.e. they
rotate the ship about its current local axis by θ radians. The flight loop calls, every tick:

```text
orientation.Yaw(   turnRate_yaw   * dT)
orientation.Pitch(-turnRate_pitch * dT)     # pitch sign inverted (stick up = nose up convention)
orientation.Roll(  turnRate_roll  * dT)
orientation.Renormalize()
```

The rotation matrices (local-axis, left-multiplied):

```
Yaw(θ)  about Y:        Pitch(θ) about X:        Roll(θ) about Z:
[ cosθ  0  -sinθ]       [ 1    0     0  ]        [ cosθ  sinθ  0]
[  0    1    0  ]       [ 0  cosθ  sinθ ]        [-sinθ  cosθ  0]
[ sinθ  0   cosθ]       [ 0 -sinθ  cosθ ]        [  0     0    1]
```

## Renormalize

Accumulating many small rotations makes the matrix drift from orthonormal. `Renormalize()` rebuilds a
clean basis each tick from the current forward + up via cross products (Gram-Schmidt):

```text
right = forward × up
up    = right × forward
normalize(forward, up, right)        # if any is zero-length, reset to identity
```

If you use quaternions instead, normalize the quaternion each tick — same purpose.

## Equivalent quaternion approach (for a modern engine)

You don't have to use matrices. The behavior to reproduce is: maintain a current angular velocity
`ω = (turnRate_yaw, turnRate_pitch, turnRate_roll)` in **ship-local** space, then each tick:

```text
# build a small local-frame rotation and compose on the RIGHT (local):
q_delta     = quat_from_euler( yaw= turnRate_yaw*dT,
                               pitch=-turnRate_pitch*dT,
                               roll= turnRate_roll*dT )
orientation = normalize(orientation * q_delta)
```

Apply yaw, then pitch, then roll order to match (the engine applies them as three sequential
left-multiplies in that order). Keep the **−pitch** sign and the **forward = −z** convention.
