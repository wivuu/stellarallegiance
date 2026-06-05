// =====================================================================
//  FlightModel.cs — SHARED, DETERMINISTIC FLIGHT INTEGRATION
//
//  CANONICAL COPY lives in shared/. It is copied VERBATIM into:
//    module/spacetimedb/FlightModel.cs   (server authority)
//    client/scripts/FlightModel.cs       (client prediction)
//  The three files MUST be byte-for-byte identical (see shared/sync.sh and
//  the T3 acceptance gate). Edit shared/ then run sync.sh — never edit a copy.
//
//  Requirements (.PLAN/06-FLIGHT-MODEL.md):
//    1. Fixed timestep only — always integrate with Dt, never a frame delta.
//    2. Same math, same order of operations on both sides.
//    3. Pure function — no globals, no randomness, no time reads.
//
//  Engine-independent on purpose: it uses the self-contained Vec3/Quat structs
//  below rather than Godot's or System.Numerics' types, so the copy compiled
//  into the wasm module and the copy compiled into the Godot client are truly
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
        public Vec3 AngVel;
    }

    public struct ShipInputState
    {
        public float Thrust, StrafeX, StrafeY, Yaw, Pitch, Roll;
        public bool Firing;
        public bool Boost;   // afterburner held: extra forward thrust + raised speed cap
    }

    public struct ShipStats
    {
        public float ThrustAccel, MaxSpeed, LinearDrag, AngularAccel, AngularDrag;
        // Afterburner: while Boost is held, forward thrust accel is scaled by
        // BoostThrustMult and the speed cap by BoostSpeedMult (both 1 = no boost).
        public float BoostThrustMult, BoostSpeedMult;
    }

    public static class FlightModel
    {
        public const float TickRate = 20f;
        public const float Dt = 1f / TickRate;

        // Ship class as a raw byte so this file depends on no game enum
        // (module's ShipClass and the client's generated ShipClass differ).
        // Matches enum order in .PLAN/03: Scout = 0, Fighter = 1.
        public const byte ClassScout = 0;
        public const byte ClassFighter = 1;

        // Per-class stats from .PLAN/03 (placeholders; AngularDrag chosen here,
        // all subject to tuning in T4). MaxHull is not part of integration.
        public static readonly ShipStats Scout = new ShipStats
        {
            ThrustAccel = 45f,
            MaxSpeed = 70f,
            LinearDrag = 1.2f,
            AngularAccel = 3.5f,
            AngularDrag = 2.5f,
            BoostThrustMult = 2.2f,   // afterburner: 99 u/s forward accel
            BoostSpeedMult = 1.6f,    // and a 112 u/s top speed while held
        };

        public static readonly ShipStats Fighter = new ShipStats
        {
            ThrustAccel = 30f,
            MaxSpeed = 50f,
            LinearDrag = 1.0f,
            AngularAccel = 2.5f,
            AngularDrag = 2.0f,
            BoostThrustMult = 1.8f,   // heavier hull: 54 u/s forward accel
            BoostSpeedMult = 1.4f,    // and a 70 u/s top speed while held
        };

        public static ShipStats StatsFor(byte shipClass) =>
            shipClass == ClassFighter ? Fighter : Scout;

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

        public static float WeaponSpreadRad(byte shipClass) =>
            shipClass == ClassFighter ? FighterSpread : ScoutSpread;

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

        // Pure, fixed-dt integration. Implements the math block from .PLAN/06
        // exactly; the order of operations is part of the contract.
        public static ShipState Integrate(ShipState s, ShipInputState i, ShipStats st)
        {
            float dt = Dt;

            // --- Orientation: integrate angular input (ship-local axes). ---
            // pitch -> X, yaw -> Y, roll -> Z.
            Vec3 angularInput = new Vec3(i.Pitch, i.Yaw, i.Roll) * st.AngularAccel;
            Vec3 angVel = s.AngVel + angularInput * dt;
            angVel = angVel * (1f - st.AngularDrag * dt);
            Quat rot = (s.Rot * Quat.FromRotationVector(angVel * dt)).Normalized();

            // --- Linear: thrust along ship-local axes, then drag, then clamp. ---
            // strafeX -> X, strafeY -> Y, thrust -> Z (forward). The afterburner
            // (Boost) scales the FORWARD axis accel and raises the speed cap while
            // held; strafe is unaffected. Branching on the bool is deterministic
            // (no transcendentals), so client prediction and server stay bit-identical.
            float fwdAccel = st.ThrustAccel * (i.Boost ? st.BoostThrustMult : 1f);
            Vec3 thrustLocal = new Vec3(i.StrafeX * st.ThrustAccel, i.StrafeY * st.ThrustAccel, i.Thrust * fwdAccel);
            Vec3 thrustWorld = rot.Rotate(thrustLocal);
            Vec3 vel = s.Vel + thrustWorld * dt;
            vel = vel * (1f - st.LinearDrag * dt);

            float maxSpeed = st.MaxSpeed * (i.Boost ? st.BoostSpeedMult : 1f);
            float speedSq = vel.LengthSquared();
            float maxSq = maxSpeed * maxSpeed;
            if (speedSq > maxSq)
            {
                float scale = maxSpeed / (float)System.Math.Sqrt(speedSq);
                vel = vel * scale;
            }

            Vec3 pos = s.Pos + vel * dt;

            return new ShipState { Pos = pos, Vel = vel, Rot = rot, AngVel = angVel };
        }
    }
}
