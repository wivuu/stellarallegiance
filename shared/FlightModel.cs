// =====================================================================
//  FlightModel.cs — SHARED, DETERMINISTIC FLIGHT INTEGRATION
//
//  This file lives in the shared/Shared.csproj library and is REFERENCED (via
//  <ProjectReference>), not copied, by:
//    module/spacetimedb/StdbModule.csproj   (server authority)
//    client/wivuullegiance.csproj           (client prediction)
//    tests/FlightModelTest/                  (determinism + golden test)
//  Each consumer's own runtime compiles this one source, so the wasm-server and
//  mono-client math stays bit-identical. There is one copy — edit it here.
//
//  Requirements (.PLAN/06-FLIGHT-MODEL.md):
//    1. Fixed timestep only — always integrate with Dt, never a frame delta.
//    2. Same math, same order of operations on both sides.
//    3. Pure function — no globals, no randomness, no time reads.
//
//  Engine-independent on purpose: it uses the self-contained Vec3/Quat structs
//  below rather than Godot's or System.Numerics' types, so the assembly compiled
//  into the wasm module and the one compiled into the Godot client are truly
//  identical source. (Decision logged in .PLAN/99.)
// =====================================================================

namespace StellarAllegiance.Shared
{
    // Deterministic trig for CROSS-RUNTIME agreement. The server runs in wasm and
    // the client in mono/.NET; IEEE-754 +,-,*,/ and sqrt are correctly-rounded and
    // bit-identical on both, but Math.Sin/Cos go through each runtime's libm and
    // differ in the last bits — which, integrated every tick, makes the client's
    // predicted rotation drift from the server's and forces constant reconciliation
    // (felt as a jerk while turning). These polynomial approximations use only
    // float +,-,* (Horner form — no FMA, which C# does not auto-emit), so both
    // runtimes compute bit-identical results. Absolute accuracy matters far less
    // than the two sides agreeing exactly. (.PLAN/99 transcendental-determinism.)
    internal static class MathDet
    {
        private const float TwoPI = 6.2831853071795864f;
        private const float InvTwoPI = 0.15915494309189535f;
        private const float HalfPI = 1.5707963267948966f;

        public static float Sin(float x)
        {
            // Range-reduce to [-PI, PI]: x -= round(x / 2PI) * 2PI, using only
            // float ops + truncating casts (both deterministic across runtimes).
            float q = x * InvTwoPI;
            q = q - (float)(long)(q >= 0f ? q + 0.5f : q - 0.5f);
            x = q * TwoPI;

            // Taylor series to x^11 (Horner). Accurate to ~1e-6 over [-PI, PI];
            // the half-angles fed in here are tiny, so it's far better in practice.
            float x2 = x * x;
            return x * (1f + x2 * (-0.16666667f + x2 * (0.008333334f
                + x2 * (-0.00019841270f + x2 * (0.0000027557319f
                + x2 * -0.000000025051883f)))));
        }

        public static float Cos(float x) => Sin(x + HalfPI);

        // Deterministic exp for CROSS-RUNTIME agreement (same rationale as Sin/Cos:
        // libm's expf differs in the last bits between wasm and mono). Used ONLY at
        // stats-load time to compute a hull's per-tick drag factor, so accuracy ~1e-6
        // is ample; what matters is both runtimes computing identical bits from
        // identical f32 inputs. exp(x) = 2^k · exp(r), with k = round(x·log2e) and
        // r = x − k·ln2 ∈ [−ln2/2, ln2/2]; exp(r) via Horner (Taylor to r^6), 2^k by
        // composing the IEEE-754 exponent field (integer ops + reinterpret — both
        // bit-deterministic). No libm, no FMA.
        public static float Exp(float x)
        {
            const float Log2E = 1.4426950408889634f;
            const float Ln2 = 0.6931471805599453f;

            float t = x * Log2E;
            int k = (int)(t >= 0f ? t + 0.5f : t - 0.5f);
            float r = x - k * Ln2;

            // exp(r), |r| <= ~0.3466 — Horner Taylor series.
            float er = 1f + r * (1f + r * (0.5f + r * (0.16666667f
                + r * (0.041666668f + r * (0.008333334f + r * 0.0013888889f)))));

            return er * Pow2(k);
        }

        // 2^k for integer k via the IEEE-754 single exponent field (bias 127).
        // Clamped to the representable normal range; deterministic on every runtime.
        private static float Pow2(int k)
        {
            if (k < -126) k = -126;
            if (k > 127) k = 127;
            return System.BitConverter.Int32BitsToSingle((k + 127) << 23);
        }
    }

    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public float LengthSquared() => X * X + Y * Y + Z * Z;
        public float Length() => (float)System.Math.Sqrt(X * X + Y * Y + Z * Z);

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    public struct Quat
    {
        public float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static Quat Identity => new Quat(0f, 0f, 0f, 1f);

        // Hamilton product a*b (apply b in a's local frame when used as a*delta).
        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        // Conjugate = inverse for a unit quaternion. Used to map a world-space vector
        // into ship-local space: q.Conjugate().Rotate(worldVec).
        public Quat Conjugate() => new Quat(-X, -Y, -Z, W);

        public Quat Normalized()
        {
            float n = (float)System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
            if (n < 1e-12f) return Identity;
            float inv = 1f / n;
            return new Quat(X * inv, Y * inv, Z * inv, W * inv);
        }

        // Rotation quaternion from a rotation vector (axis * angle, radians).
        public static Quat FromRotationVector(Vec3 r)
        {
            float angle = r.Length();
            if (angle < 1e-9f) return Identity;
            float half = angle * 0.5f;
            // Deterministic sin/cos (see MathDet) so the wasm server and mono client
            // produce bit-identical rotations and don't drift apart each tick.
            float s = MathDet.Sin(half) / angle; // sin(half)/angle scales axis = r/angle
            return new Quat(r.X * s, r.Y * s, r.Z * s, MathDet.Cos(half));
        }

        // Rotate a vector by this quaternion: v' = v + 2w(q x v) + 2(q x (q x v)).
        public Vec3 Rotate(Vec3 v)
        {
            Vec3 q = new Vec3(X, Y, Z);
            Vec3 t = Vec3.Cross(q, v) * 2f;
            return v + (t * W) + Vec3.Cross(q, t);
        }
    }

    public struct ShipState
    {
        public Vec3 Pos;
        public Vec3 Vel;
        public Quat Rot;
        // Persistent per-axis turn rate (rad/s) in SHIP-LOCAL axes: X=pitch, Y=yaw,
        // Z=roll. Allegiance's rotational-inertia signature: the rate slews toward
        // the commanded rate each tick and keeps rotating briefly after the stick is
        // released. (Previously a world-axis angular velocity; the Allegiance rework
        // made it a body-local rate — server and client switch together.)
        public Vec3 AngVel;
        // Per-instance actual mass (from the synced Ship row). Equals the class
        // baseline (ShipStats.Mass) today; future cargo/upgrades may differ. A
        // value <= 0 means "unset" and Integrate falls back to the class baseline.
        public float Mass;
        // Afterburner power ramp, 0..1. Persisted/synced like AngVel so the server's
        // authority and the client's prediction ramp the afterburner identically
        // (it climbs at AbOnRate while Boost is held, falls at AbOffRate otherwise).
        public float AbPower;
    }

    public struct ShipInputState
    {
        // Yaw/Pitch/Roll are analog −1..1 COMMANDED RATE FRACTIONS (sphere-clamped in
        // Integrate). Thrust is a throttle −1..1: ≥0 commands a forward speed = that
        // fraction of MaxSpeed (throttle-commands-speed); <0 is manual reverse thrust
        // (weak, via BackMult). StrafeX/StrafeY are manual lateral thrust (weak, via
        // SideMult). See FlightModel.Integrate for the exact branches.
        public float Thrust, StrafeX, StrafeY, Yaw, Pitch, Roll;
        public bool Firing;
        public bool Boost;   // afterburner held: ramps AbPower, raising equilibrium speed
        public bool Coast;   // vector lock: engine thrust exactly cancels drag (hold velocity)
    }

    // A hull's flight feel: the human-authored "nine knobs + afterburner" (top block)
    // plus values DERIVED once from them at load (bottom block, never recomputed per
    // tick). Build via Create() so the derivation runs exactly once; the server writes
    // the authored f32s into a row and the client reads the identical bits, so both
    // sides derive bit-identical thrust/torques/drag through this same code.
    public struct ShipStats
    {
        // --- Authored (the nine knobs + afterburner) ---
        public float MaxSpeed;   // terminal velocity (u/s) — an equilibrium, not a hard cap
        public float Accel;      // forward accel (u/s²); Thrust = Mass·Accel internally
        public float Mass;       // collisions/momentum + class baseline for Ship.Mass
        public float RateYawDeg, RatePitchDeg, RateRollDeg;   // max turn rates (deg/s)
        public float DriftYawDeg, DriftPitchDeg;              // overshoot angle (deg); roll reuses pitch
        public float SideMult, BackMult;                      // 0..1 strafe / reverse thrust fraction
        public float AbAccel;                                 // extra forward accel at full afterburner
        public float AbOnRate, AbOffRate;                     // afterburner power ramp per second

        // --- Derived once by Create() (NOT authored, NOT stored in a row) ---
        public float Thrust;       // Mass·Accel — engine force capacity (clip magnitude)
        public float AbThrust;     // Mass·AbAccel — afterburner force
        public float OneMinusDrag; // 1 − exp(−Accel·Dt/MaxSpeed): per-tick drag fraction
        public float MaxRateYawRad, MaxRatePitchRad, MaxRateRollRad;   // rates in rad/s
        public float TorqueYawRad, TorquePitchRad, TorqueRollRad;      // angular-accel terms

        private const float Deg2Rad = 0.017453292519943295f;

        public static ShipStats Create(
            float maxSpeed, float accel, float mass,
            float rateYawDeg, float ratePitchDeg, float rateRollDeg,
            float driftYawDeg, float driftPitchDeg,
            float sideMult, float backMult,
            float abAccel, float abOnRate, float abOffRate)
        {
            return new ShipStats
            {
                MaxSpeed = maxSpeed, Accel = accel, Mass = mass,
                RateYawDeg = rateYawDeg, RatePitchDeg = ratePitchDeg, RateRollDeg = rateRollDeg,
                DriftYawDeg = driftYawDeg, DriftPitchDeg = driftPitchDeg,
                SideMult = sideMult, BackMult = backMult,
                AbAccel = abAccel, AbOnRate = abOnRate, AbOffRate = abOffRate,

                Thrust = mass * accel,
                AbThrust = mass * abAccel,
                // Drag factor f = exp(−thrust·dt/(mass·maxSpeed)) = exp(−accel·dt/maxSpeed):
                // mass-independent, constant for fixed dt. Stored as (1−f) since the
                // per-tick velocity bleed from drag is exactly vel·(1−f).
                OneMinusDrag = 1f - MathDet.Exp(-(accel * FlightModel.Dt) / maxSpeed),

                MaxRateYawRad = rateYawDeg * Deg2Rad,
                MaxRatePitchRad = ratePitchDeg * Deg2Rad,
                MaxRateRollRad = rateRollDeg * Deg2Rad,
                // turnTorque = mass · (rate² / (2·drift)) · π/180 (rate,drift in deg).
                // Roll deliberately reuses driftPitch (faithful to the original engine).
                TorqueYawRad = mass * (rateYawDeg * rateYawDeg / (2f * driftYawDeg)) * Deg2Rad,
                TorquePitchRad = mass * (ratePitchDeg * ratePitchDeg / (2f * driftPitchDeg)) * Deg2Rad,
                TorqueRollRad = mass * (rateRollDeg * rateRollDeg / (2f * driftPitchDeg)) * Deg2Rad,
            };
        }
    }

    public static class FlightModel
    {
        public const float TickRate = 20f;
        public const float Dt = 1f / TickRate;

        // Ship class as a raw byte so this file depends on no game enum
        // (module's ShipClass and the client's generated ShipClass differ).
        // Matches enum order in .PLAN/03: Scout = 0, Fighter = 1, Bomber = 2.
        public const byte ClassScout = 0;
        public const byte ClassFighter = 1;
        public const byte ClassBomber = 2;

        // Per-class seed stats from the extracted Allegiance hulls (.PLAN/CONFIG.md,
        // .PLAN/ship_movement/06_extracted_hull_stats.md). Authored knobs only — the
        // derived thrust/torques/drag are computed once by ShipStats.Create. These are
        // the compile-in defaults; M1 seeds an identical row into ShipClassDef so an
        // operator can retune at runtime. Note the faithful quirk: the Fighter
        // out-TURNS the Scout (60 vs 50 °/s); the Scout's edge is speed and snap.
        //                                 maxSpd accel mass  yaw  pit  rol  dYaw dPit side  back  abAcc onR  offR
        public static readonly ShipStats Scout = ShipStats.Create(
            160f, 30f, 40f, 50f, 50f, 50f, 5f, 5f, 0.5f, 0.25f, 18f, 2.0f, 1.0f);
        // abAccel 18 → abThrust/thrust = 0.6 → boosted equilibrium ≈ 1.6× MaxSpeed.

        public static readonly ShipStats Fighter = ShipStats.Create(
            100f, 25f, 36f, 60f, 60f, 60f, 5f, 5f, 0.5f, 0.5f, 10f, 2.0f, 1.0f);
        // abAccel 10 → abThrust/thrust = 0.4 → boosted equilibrium ≈ 1.4× MaxSpeed.

        // Bomber: the heavy hull, straight from the extracted Allegiance numbers
        // ("Bomber" row, 06_extracted_hull_stats.md). 20°/s rates at 8° drift give
        // only rate²/(2·drift) = 25°/s² of angular accel — ~0.8-1.6 s to wind a turn
        // up or stop it. This is the hull where the rotational inertia really shows.
        public static readonly ShipStats Bomber = ShipStats.Create(
            60f, 15f, 50f, 20f, 20f, 20f, 8f, 8f, 0.5f, 0.5f, 6f, 2.0f, 1.0f);
        // abAccel 6 → abThrust/thrust = 0.4 → boosted equilibrium ≈ 1.4× MaxSpeed.

        // Escape pod (server Ship.IsPod): a slow, unarmed lifeboat ejected on ship death.
        // Full strafe/reverse (1.0) and no afterburner (AbAccel 0) — it crawls home and
        // gets shoved around in collisions (light mass). Selected via StatsFor(class,
        // isPod) so server authority and client prediction integrate pods identically.
        public static readonly ShipStats Pod = ShipStats.Create(
            60f, 15f, 10f, 40f, 40f, 40f, 8f, 8f, 1.0f, 1.0f, 0f, 2.0f, 1.0f);

        public static ShipStats StatsFor(byte shipClass) =>
            shipClass == ClassFighter ? Fighter :
            shipClass == ClassBomber ? Bomber : Scout;

        // Pod-aware stats selection: a pod ignores its class and flies the slow,
        // boost-less Pod profile. Callers pass ship.IsPod so server authority and
        // client prediction agree on which stats a pod integrates with.
        public static ShipStats StatsFor(byte shipClass, bool isPod) =>
            isPod ? Pod : StatsFor(shipClass);

        // ---- Weapon spread -------------------------------------------------
        //
        // Per-weapon shot scatter as a cone HALF-ANGLE in radians. Tweak these to
        // taste: 0 is pinpoint, larger is sloppier. Tied to ship class for now (one
        // weapon per class); the standard Scout cannon is the "default" weapon and is
        // near-pinpoint, while the Fighter's heavier gun scatters more. Lives here in
        // the shared model so the authoritative server and the predicting client read
        // the SAME value (no mirrored-constant drift).
        public const float ScoutSpread = 0.006f;    // ~0.34° — minimal (default weapon)
        public const float FighterSpread = 0.035f;  // ~2.0°
        public const float BomberSpread = 0.012f;   // ~0.7° — slow heavy slugs, fairly true

        public static float WeaponSpreadRad(byte shipClass) =>
            shipClass == ClassFighter ? FighterSpread :
            shipClass == ClassBomber ? BomberSpread : ScoutSpread;

        // Deterministically scatter a unit fire direction within a cone of the given
        // half-angle. Keyed by (shipId, fireTick) so the wasm server and the mono
        // client compute the IDENTICAL scattered vector for a given shot — the player's
        // predicted tracer then lands exactly where the authoritative projectile goes.
        // Uses only cross-runtime-deterministic ops: integer hashing, MathDet trig, and
        // IEEE sqrt (the same primitives the integrator already relies on). A spread of
        // <= 0 returns fwd unchanged.
        public static Vec3 SpreadDirection(Vec3 fwd, float spreadRad, ulong shipId, uint fireTick)
        {
            if (spreadRad <= 0f)
                return fwd;

            // Two independent uniforms in [0,1) from a hash of the shot key.
            uint key = unchecked((uint)shipId * 2654435761u ^ (fireTick * 40503u));
            float u1 = UnitFloat(Hash(key));
            float u2 = UnitFloat(Hash(key ^ 0x9e3779b9u));

            // Polar angle within the cone (sqrt → area-uniform over the cap) + azimuth.
            float theta = spreadRad * (float)System.Math.Sqrt(u2);
            float phi = 6.2831853071795864f * u1;

            // Orthonormal basis around fwd; pick a reference axis that isn't parallel.
            Vec3 f = NormalizeVec(fwd);
            Vec3 reference = (f.Y < 0.99f && f.Y > -0.99f) ? new Vec3(0f, 1f, 0f) : new Vec3(1f, 0f, 0f);
            Vec3 right = NormalizeVec(Vec3.Cross(reference, f));
            Vec3 up = Vec3.Cross(f, right);

            float st = MathDet.Sin(theta);
            float ct = MathDet.Cos(theta);
            Vec3 radial = right * MathDet.Cos(phi) + up * MathDet.Sin(phi);

            // f·cosθ + radial·sinθ — tilt fwd off-axis by θ around azimuth φ.
            return NormalizeVec(f * ct + radial * st);
        }

        private static Vec3 NormalizeVec(Vec3 v)
        {
            float n = v.Length();
            if (n < 1e-12f) return v;
            float inv = 1f / n;
            return new Vec3(v.X * inv, v.Y * inv, v.Z * inv);
        }

        // Integer avalanche hash (lowbias32) — bit-identical on every runtime.
        private static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16; return x;
            }
        }

        // Top 24 bits of a hash → a float in [0,1).
        private static float UnitFloat(uint h) => (h >> 8) * (1f / 16777216f);

        // Speed-dependent agility: 50% angular ACCELERATION at rest, ramping to 100%
        // at max speed (max RATE is constant). Ships feel locked-up sitting still and
        // crisp at speed. TorqueMultiplier = 0.5 + 0.5·(2f/(f+1)), f = |v|/maxSpeed.
        public static float TorqueMultiplier(float speed, float maxSpeed)
        {
            float f = (maxSpeed > 0f) ? speed / maxSpeed : 0f;
            return 0.5f + 0.5f * (2f * f / (f + 1f));
        }

        // Pure, fixed-dt integration — the Allegiance per-tick flight loop (port of
        // .PLAN/ship_movement/01_flight_model.md / 05_reference_implementation.py).
        // The order of operations and the branch structure are part of the determinism
        // contract: every branch keys off f32 compares on identically-sourced values, so
        // the wasm server and the mono client fork identically and stay bit-identical.
        //
        // Convention note: this codebase's ship-local forward is +Z (projectiles fire
        // along Rot·(0,0,1)), the mirror of Allegiance's −z. The dynamics below are the
        // faithful port; the few sign flips from the reference (forward-penalty on z≥0,
        // backward = Rot·(0,0,−1)) are exactly that Z-mirror, and the yaw/pitch/roll
        // rotation signs are kept as this codebase already had them so controls don't
        // invert.
        public static ShipState Integrate(ShipState s, ShipInputState i, ShipStats st)
        {
            float dt = Dt;
            float instMass = (s.Mass > 0f) ? s.Mass : st.Mass;   // heavier instance flies heavier
            float ttv = dt / instMass;                            // thrustToVelocity (force→Δv)
            float thrust = st.Thrust;                             // class force capacity
            float speed = s.Vel.Length();

            // --- Step 1: rotation (rate- AND acceleration-limited, per axis). ---
            // Stick is clamped to a unit SPHERE (diagonal input never beats a single
            // axis). AngVel holds the persistent local turn rate (X=pitch,Y=yaw,Z=roll);
            // each axis slews toward stick·maxRate by at most TorqueMult·turnTorque·dt/mass.
            float l2 = i.Yaw * i.Yaw + i.Pitch * i.Pitch + i.Roll * i.Roll;
            float l = (l2 > 1f) ? 1f / (float)System.Math.Sqrt(l2) : 1f;
            float tm = TorqueMultiplier(speed, st.MaxSpeed) * ttv;

            float ratePitch = SlewRate(s.AngVel.X, i.Pitch * l * st.MaxRatePitchRad, tm * st.TorquePitchRad);
            float rateYaw = SlewRate(s.AngVel.Y, i.Yaw * l * st.MaxRateYawRad, tm * st.TorqueYawRad);
            float rateRoll = SlewRate(s.AngVel.Z, i.Roll * l * st.MaxRateRollRad, tm * st.TorqueRollRad);
            Vec3 angVel = new Vec3(ratePitch, rateYaw, rateRoll);

            // Apply attitude as three SEQUENTIAL local-axis rotations, yaw→pitch→roll
            // (composed on the right = each in the frame left by the previous), then
            // renormalize. Must NOT be combined into one rotation vector — sequence is
            // part of the feel.
            Quat rot = s.Rot
                * Quat.FromRotationVector(new Vec3(0f, rateYaw * dt, 0f))
                * Quat.FromRotationVector(new Vec3(ratePitch * dt, 0f, 0f))
                * Quat.FromRotationVector(new Vec3(0f, 0f, rateRoll * dt));
            rot = rot.Normalized();

            Vec3 forward = rot.Rotate(new Vec3(0f, 0f, 1f));
            Vec3 backward = forward * -1f;

            // --- Step 2: drag (defines terminal speed). drag = vel·(1−f)/ttv. ---
            Vec3 drag = s.Vel * (st.OneMinusDrag / ttv);

            // --- Step 3: afterburner power ramp + fold into drag. ---
            bool afterburning = i.Boost && st.AbThrust > 0f;
            float thrustRatio = 0f;
            float abPower = s.AbPower;
            if (st.AbThrust > 0f)
            {
                if (afterburning)
                {
                    thrustRatio = st.AbThrust / thrust;
                    abPower = abPower + dt * st.AbOnRate;
                    if (abPower > 1f) abPower = 1f;
                }
                else
                {
                    abPower = abPower - dt * st.AbOffRate;
                    if (abPower < 0f) abPower = 0f;
                }
                if (abPower != 0f)
                    drag = drag + backward * (abPower * st.AbThrust);
            }

            // --- Step 4: engine thrust direction (manual-strafe / coast / throttle). ---
            // manual: any lateral strafe or reverse throttle → direct local thrust
            // (forward still honoured via Thrust so you can strafe-and-advance). Else
            // coast cancels drag exactly; else throttle commands a forward speed.
            bool manual = i.StrafeX != 0f || i.StrafeY != 0f || i.Thrust < 0f;
            Vec3 localThrust;
            if (i.Coast && !afterburning)
            {
                localThrust = rot.Conjugate().Rotate(drag);
            }
            else if (manual)
            {
                localThrust = new Vec3(i.StrafeX * thrust, i.StrafeY * thrust, i.Thrust * thrust);
            }
            else
            {
                float negDesired = afterburning
                    ? -(st.MaxSpeed * (1f + thrustRatio))
                    : -i.Thrust * (speed > st.MaxSpeed ? speed : st.MaxSpeed);
                Vec3 desiredVel = backward * negDesired;
                localThrust = rot.Conjugate().Rotate((desiredVel - s.Vel) * (1f / ttv) + drag);
            }

            // --- Step 5: directional clip — forward (+z) strongest. Strafe is divided
            // by SideMult and reverse (−z) by BackMult BEFORE the capacity check, so
            // those directions get less real thrust. ---
            float sz = localThrust.Z >= 0f ? localThrust.Z : localThrust.Z / st.BackMult;
            Vec3 scaled = new Vec3(localThrust.X / st.SideMult, localThrust.Y / st.SideMult, sz);
            float r2 = scaled.LengthSquared();
            Vec3 engine;
            if (r2 < 1e-12f)
            {
                engine = new Vec3(0f, 0f, 0f);
            }
            else
            {
                Vec3 worldThrust = rot.Rotate(localThrust);
                engine = (r2 <= thrust * thrust)
                    ? worldThrust
                    : worldThrust * (thrust / (float)System.Math.Sqrt(r2));
            }

            // --- Step 6: integrate velocity then position. vel += ttv·(engine − drag). ---
            Vec3 vel = s.Vel + (engine - drag) * ttv;
            Vec3 pos = s.Pos + vel * dt;

            return new ShipState { Pos = pos, Vel = vel, Rot = rot, AngVel = angVel, Mass = s.Mass, AbPower = abPower };
        }

        // Slew `cur` toward `desired`, changing by at most `maxDelta` this tick.
        private static float SlewRate(float cur, float desired, float maxDelta)
        {
            if (desired < cur - maxDelta) return cur - maxDelta;
            if (desired > cur + maxDelta) return cur + maxDelta;
            return desired;
        }
    }
}
