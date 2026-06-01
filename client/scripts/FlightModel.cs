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
    }

    public struct ShipStats
    {
        public float ThrustAccel, MaxSpeed, LinearDrag, AngularAccel, AngularDrag;
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
        };

        public static readonly ShipStats Fighter = new ShipStats
        {
            ThrustAccel = 30f,
            MaxSpeed = 50f,
            LinearDrag = 1.0f,
            AngularAccel = 2.5f,
            AngularDrag = 2.0f,
        };

        public static ShipStats StatsFor(byte shipClass) =>
            shipClass == ClassFighter ? Fighter : Scout;

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
            // strafeX -> X, strafeY -> Y, thrust -> Z (forward).
            Vec3 thrustLocal = new Vec3(i.StrafeX, i.StrafeY, i.Thrust) * st.ThrustAccel;
            Vec3 thrustWorld = rot.Rotate(thrustLocal);
            Vec3 vel = s.Vel + thrustWorld * dt;
            vel = vel * (1f - st.LinearDrag * dt);

            float speedSq = vel.LengthSquared();
            float maxSq = st.MaxSpeed * st.MaxSpeed;
            if (speedSq > maxSq)
            {
                float scale = st.MaxSpeed / (float)System.Math.Sqrt(speedSq);
                vel = vel * scale;
            }

            Vec3 pos = s.Pos + vel * dt;

            return new ShipState { Pos = pos, Vel = vel, Rot = rot, AngVel = angVel };
        }
    }
}
