"""
Self-contained reference implementation of the Allegiance ship flight model.

A faithful port of CshipIGC::ExecuteShipMove() and the Orientation math, with no
external dependencies (pure Python + math). Run it directly to see a ship
accelerate, reach terminal speed, and execute rate-limited turns:

    python3 05_reference_implementation.py

Conventions match the engine:
  - Ship-local axes: Right(+x), Up(+y), Forward(-z).  Forward thrust is -z.
  - Orientation is a 3x3 row-major rotation matrix; rows are local axes in world space.
  - Angular velocity (turn_rates) persists between ticks -> rotational inertia.
  - Mass cancels out of linear & angular acceleration (kept only for clarity/collisions).

See 01_flight_model.md and 04_data_schema.md for the prose explanation.
"""

import math

# ---------------------------------------------------------------------------
# Minimal vector / matrix helpers (3D)
# ---------------------------------------------------------------------------

def vadd(a, b):   return (a[0]+b[0], a[1]+b[1], a[2]+b[2])
def vsub(a, b):   return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def vscale(a, s): return (a[0]*s, a[1]*s, a[2]*s)
def vdot(a, b):   return a[0]*b[0] + a[1]*b[1] + a[2]*b[2]
def vlen(a):      return math.sqrt(vdot(a, a))
def vlen2(a):     return vdot(a, a)
def vcross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def vnorm(a):
    l = vlen(a)
    return (a[0]/l, a[1]/l, a[2]/l) if l else (0.0, 0.0, 0.0)


class Orientation:
    """3x3 rotation matrix. Rows: 0=Right, 1=Up, 2=-Forward (engine convention)."""

    def __init__(self):
        # Identity: right=+x, up=+y, backward=+z  (forward = -z)
        self.r = [[1.0, 0.0, 0.0],
                  [0.0, 1.0, 0.0],
                  [0.0, 0.0, 1.0]]

    def right(self):    return tuple(self.r[0])
    def up(self):       return tuple(self.r[1])
    def backward(self): return tuple(self.r[2])
    def forward(self):  return (-self.r[2][0], -self.r[2][1], -self.r[2][2])

    def to_world(self, v):
        """local vector -> world  (v * orientation)."""
        r = self.r
        return (v[0]*r[0][0] + v[1]*r[1][0] + v[2]*r[2][0],
                v[0]*r[0][1] + v[1]*r[1][1] + v[2]*r[2][1],
                v[0]*r[0][2] + v[1]*r[1][2] + v[2]*r[2][2])

    def to_local(self, v):
        """world vector -> local  (orientation.TimesInverse(v))."""
        r = self.r
        return (v[0]*r[0][0] + v[1]*r[0][1] + v[2]*r[0][2],
                v[0]*r[1][0] + v[1]*r[1][1] + v[2]*r[1][2],
                v[0]*r[2][0] + v[1]*r[2][1] + v[2]*r[2][2])

    def yaw(self, t):
        c, s = math.cos(t), math.sin(t); r = self.r
        self.r = [
            [c*r[0][0]-s*r[2][0], c*r[0][1]-s*r[2][1], c*r[0][2]-s*r[2][2]],
            [r[1][0],             r[1][1],             r[1][2]],
            [c*r[2][0]+s*r[0][0], c*r[2][1]+s*r[0][1], c*r[2][2]+s*r[0][2]],
        ]

    def pitch(self, t):
        c, s = math.cos(t), math.sin(t); r = self.r
        self.r = [
            [r[0][0],             r[0][1],             r[0][2]],
            [c*r[1][0]+s*r[2][0], c*r[1][1]+s*r[2][1], c*r[1][2]+s*r[2][2]],
            [c*r[2][0]-s*r[1][0], c*r[2][1]-s*r[1][1], c*r[2][2]-s*r[1][2]],
        ]

    def roll(self, t):
        c, s = math.cos(t), math.sin(t); r = self.r
        self.r = [
            [c*r[0][0]+s*r[1][0], c*r[0][1]+s*r[1][1], c*r[0][2]+s*r[1][2]],
            [c*r[1][0]-s*r[0][0], c*r[1][1]-s*r[0][1], c*r[1][2]-s*r[0][2]],
            [r[2][0],             r[2][1],             r[2][2]],
        ]

    def renormalize(self):
        fwd = self.forward()
        up = self.up()
        right = vcross(fwd, up)
        new_up = vcross(right, fwd)
        lr, lu, lf = vlen(right), vlen(new_up), vlen(fwd)
        if lr and lu and lf:
            fwd, new_up, right = vscale(fwd, 1/lf), vscale(new_up, 1/lu), vscale(right, 1/lr)
            self.r[0] = list(right)
            self.r[1] = list(new_up)
            self.r[2] = [-fwd[0], -fwd[1], -fwd[2]]   # row 2 = -forward


# ---------------------------------------------------------------------------
# Hull definition (the 8 feel knobs) + derived engine values
# ---------------------------------------------------------------------------

DEG2RAD = math.pi / 180.0

class HullType:
    """Build from the human-authored design numbers (see 04_data_schema.md)."""

    def __init__(self, name, max_speed, acceleration,
                 rate_yaw_deg, rate_pitch_deg, rate_roll_deg,
                 drift_yaw_deg, drift_pitch_deg,
                 side_mult, back_mult, mass=1.0,
                 ab_max_thrust=0.0, ab_on_rate=2.0, ab_off_rate=2.0):
        self.name = name
        self.mass = mass
        self.max_speed = max_speed
        self.thrust = mass * acceleration                       # thrust = mass * accel
        self.max_turn_rate = [rate_yaw_deg * DEG2RAD,           # rad/s
                              rate_pitch_deg * DEG2RAD,
                              rate_roll_deg * DEG2RAD]
        # turnTorque = mass * (rate^2 / (2*drift)) * pi/180   (roll reuses drift_pitch)
        self.turn_torque = [
            mass * (rate_yaw_deg**2   / (2.0 * drift_yaw_deg))   * DEG2RAD,
            mass * (rate_pitch_deg**2 / (2.0 * drift_pitch_deg)) * DEG2RAD,
            mass * (rate_roll_deg**2  / (2.0 * drift_pitch_deg)) * DEG2RAD,
        ]
        self.side_mult = side_mult
        self.back_mult = back_mult
        self.ab_max_thrust = ab_max_thrust
        self.ab_on_rate = ab_on_rate
        self.ab_off_rate = ab_off_rate


# ---------------------------------------------------------------------------
# Ship state + the per-tick update (ExecuteShipMove port)
# ---------------------------------------------------------------------------

# Control button bitmask (subset that affects motion) -- values from the engine.
COAST     = 4
BACKWARD  = 8
FORWARD   = 16
LEFT      = 32
RIGHT     = 64
UP        = 128
DOWN      = 256
AFTERBURN = 512

class Ship:
    def __init__(self, hull):
        self.hull = hull
        self.velocity = (0.0, 0.0, 0.0)
        self.orientation = Orientation()
        self.turn_rates = [0.0, 0.0, 0.0]   # yaw, pitch, roll (rad/s), persistent
        # controls: jsValues[yaw, pitch, roll, throttle]; throttle rests at -1
        self.js = [0.0, 0.0, 0.0, -1.0]
        self.state_m = 0                    # button bitmask
        self.ab_power = 0.0                 # afterburner ramp 0..1

    def torque_multiplier(self):
        frac = vlen(self.velocity) / self.hull.max_speed
        return 0.5 + 0.5 * 2.0 * frac / (frac + 1.0)

    def execute_move(self, dT):
        h = self.hull
        thrust = h.thrust
        thrust2 = thrust * thrust
        thrust_to_velocity = dT / h.mass
        o = self.orientation

        # --- Step 1: rotation (rate- & accel-limited) ---
        l = self.js[0]**2 + self.js[1]**2 + self.js[2]**2
        l = (1.0 / math.sqrt(l)) if l > 1.0 else 1.0
        tm = self.torque_multiplier() * thrust_to_velocity
        for i in range(3):
            desired = self.js[i] * l * h.max_turn_rate[i]
            max_delta = tm * h.turn_torque[i]
            if desired < self.turn_rates[i] - max_delta:
                self.turn_rates[i] -= max_delta
            elif desired > self.turn_rates[i] + max_delta:
                self.turn_rates[i] += max_delta
            else:
                self.turn_rates[i] = desired

        o.yaw(  self.turn_rates[0] * dT)
        o.pitch(-self.turn_rates[1] * dT)   # pitch negated
        o.roll( self.turn_rates[2] * dT)
        o.renormalize()

        backward = o.backward()
        speed = vlen(self.velocity)
        max_speed = h.max_speed

        # --- Step 2: drag ---
        f = math.exp(-thrust * thrust_to_velocity / max_speed)
        drag = vscale(self.velocity, (1.0 - f) / thrust_to_velocity)

        # --- Step 3: afterburner ---
        afterF = bool(self.state_m & AFTERBURN)
        thrust_ratio = 0.0
        if h.ab_max_thrust > 0.0:
            ab_thrust = h.ab_max_thrust
            if afterF:
                thrust_ratio = ab_thrust / thrust
                self.ab_power = min(1.0, self.ab_power + dT * h.ab_on_rate)
            else:
                self.ab_power = max(0.0, self.ab_power - dT * h.ab_off_rate)
            if self.ab_power != 0.0:
                drag = vadd(drag, vscale(backward, self.ab_power * ab_thrust))

        # --- Step 4: engine thrust direction ---
        engine_vector = (0.0, 0.0, 0.0)
        strafe_mask = LEFT | RIGHT | UP | DOWN | FORWARD | BACKWARD
        if self.state_m & strafe_mask:
            x = (-1 if self.state_m & LEFT else 0) + (1 if self.state_m & RIGHT else 0)
            y = (-1 if self.state_m & DOWN else 0) + (1 if self.state_m & UP else 0)
            z = (1 if self.state_m & BACKWARD else 0) + (-1 if self.state_m & FORWARD else 0)
            local_thrust = (thrust * x, thrust * y, thrust * z)
        else:
            if (self.state_m & COAST) and not afterF:
                local_thrust = o.to_local(drag)
            else:
                if afterF:
                    neg_desired = max_speed * (-1.0 - thrust_ratio)
                else:
                    neg_desired = (-0.5 * (1.0 + self.js[3])) * (speed if speed > max_speed else max_speed)
                desired_velocity = vscale(backward, neg_desired)
                local_thrust = o.to_local(
                    vadd(vscale(vsub(desired_velocity, self.velocity), 1.0 / thrust_to_velocity), drag))

        # --- Step 5: directional clip (forward strongest) ---
        sm = h.side_mult
        sz = local_thrust[2] if local_thrust[2] <= 0.0 else local_thrust[2] / h.back_mult
        scaled = (local_thrust[0] / sm, local_thrust[1] / sm, sz)
        r2 = vlen2(scaled)
        if r2 == 0.0:
            engine_vector = (0.0, 0.0, 0.0)
        else:
            world_thrust = o.to_world(local_thrust)
            if r2 <= thrust2:
                engine_vector = world_thrust
            else:
                engine_vector = vscale(world_thrust, thrust / math.sqrt(r2))

        # --- Step 6: integrate velocity ---
        self.velocity = vadd(self.velocity,
                             vscale(vsub(engine_vector, drag), thrust_to_velocity))


# ---------------------------------------------------------------------------
# Demo
# ---------------------------------------------------------------------------

def _demo():
    # Example "Fighter"-ish hull (illustrative numbers, not from a real core).
    fighter = HullType(
        name="Fighter",
        max_speed=70.0, acceleration=40.0,
        rate_yaw_deg=90.0, rate_pitch_deg=90.0, rate_roll_deg=120.0,
        drift_yaw_deg=45.0, drift_pitch_deg=45.0,
        side_mult=0.6, back_mult=0.5, mass=1.0,
        ab_max_thrust=80.0,
    )
    ship = Ship(fighter)
    dT = 1.0 / 20.0   # 20 Hz physics, like the original

    print(f"Hull '{fighter.name}': thrust={fighter.thrust:.1f}  "
          f"maxTurnRate(yaw)={fighter.max_turn_rate[0]:.3f} rad/s  "
          f"turnTorque(yaw)={fighter.turn_torque[0]:.3f}")
    print("\n--- Full throttle forward (reach terminal speed) ---")
    ship.js[3] = 1.0   # throttle full forward
    for step in range(1, 121):
        ship.execute_move(dT)
        if step % 20 == 0:
            print(f"t={step*dT:4.1f}s  speed={vlen(ship.velocity):6.2f} "
                  f"({100*vlen(ship.velocity)/fighter.max_speed:5.1f}% of max)")

    print("\n--- Hold full yaw (rate-limited spin-up) ---")
    ship.js[0] = 1.0   # full yaw
    for step in range(1, 61):
        ship.execute_move(dT)
        if step % 10 == 0:
            print(f"t={step*dT:4.1f}s  yawRate={math.degrees(ship.turn_rates[0]):6.2f} deg/s "
                  f"(max {math.degrees(fighter.max_turn_rate[0]):.0f})")

    print("\n--- Release yaw (inertia: rate decays, not instant) ---")
    ship.js[0] = 0.0
    for step in range(1, 31):
        ship.execute_move(dT)
        if step % 5 == 0:
            print(f"t={step*dT:4.1f}s  yawRate={math.degrees(ship.turn_rates[0]):6.2f} deg/s")


if __name__ == "__main__":
    _demo()
