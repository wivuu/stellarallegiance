using static StellarAllegiance.Shared.Vec3;

namespace StellarAllegiance.Shared;

// Crew-served turret aiming — THE single arc/rest rule for a turret station (v42 crews slice 2).
// A station's HardpointDef.Dir is its ZENITH (the outward normal of the mount); the gunner aims a
// ship-local unit vector anywhere in the cone around it — a hemisphere plus a little depression
// below the mount's horizon (user steer 2026-09-13: the bare hemisphere felt too restricted). Three
// mirrors consume this and must never drift (same pattern as FireCadence):
//   - server Simulation.TryFireTurrets   (authoritative: clamps + traverses the held aim, fires along it)
//   - client TurretController             (the gunner: free look → desired aim, same traverse, local bolts)
//   - client ShipRenderer.ApplyTurrets    (remote turrets: rebuilds bolts along the streamed aim)
// The traverse (Slew, below) is the ONLY physics a turret has, and it is the gun's, not the camera's:
// the gunner's view is a free look that moves with the mouse; the traversed aim is where the bolts
// leave and where the reticle is drawn.
public static class TurretAim
{
    // Firing arc half-angle around the zenith: a hemisphere plus 15° of depression below the
    // station's horizon (105° total). The hull sits below the horizon, so the extra depression is
    // what lets a dorsal gun rake something alongside the hull without the mount's own deck
    // occluding it — deeper than this and the bolt ray starts inside the hull on the stock models.
    public const float ArcHalfAngleRad = 1.8325957145940461f; // 105°

    // Gimbal elevation limits: the horizon is 0, the zenith is +90°, and the arc's depression is the
    // (negative) floor. Every mirror clamps to exactly these two.
    public const float MaxElevationRad = 1.5707963267948966f;
    public const float MinElevationRad = MaxElevationRad - ArcHalfAngleRad; // −15°
    private const float CosArc = -0.25881904510252074f; // cos(105°)
    private const float SinArc = 0.96592582628906829f; // sin(105°)

    // TurretInputMessage.Flags bits.
    public const byte FlagFiring = 1;

    // Traverse defaults for a station that authors no `slew-deg` / `accel-deg` (projection fills
    // HardpointDef.TurretSlewRad / TurretAccelRad from these): a light mount that comes round 90° in
    // about two seconds and reaches full speed in a third of one. The bomber flies these (user steer
    // 2026-09-13: 150°/s was "too quick" for a bomber). A heavy capital station authors lower numbers.
    public const double DefaultSlewDeg = 60.0;
    public const double DefaultAccelDeg = 180.0;

    // Spread-seed "barrel" for a station: 0x80 | HardpointDef.Index — disjoint from the pilot's
    // barrel indices (< 128), so a turret volley never shares a scatter seed with a hull gun on the
    // same (ShipId, tick). Mirrored by every bolt rebuild.
    public static byte SpreadBarrel(byte hpIndex) => (byte)(0x80 | (hpIndex & 0x7F));

    // Is a (unit) aim inside the arc? Inclusive at the edge.
    public static bool InArc(Vec3 zenith, Vec3 aim) => Dot(zenith, aim) >= CosArc - 1e-6f;

    // Rest pose for an unmanned / freshly manned station: 45° up from the ship's forward (+Z)
    // projected onto the station's horizon plane — a dorsal turret rests looking forward-up, a belly
    // turret forward-down, a flank turret out-forward. A station whose zenith is parallel to +Z (the
    // tail turret) has no forward-on-horizon and rests looking straight along its zenith.
    public static Vec3 Rest(Vec3 zenith)
    {
        zenith = Normalize(zenith);
        var fwd = new Vec3(0f, 0f, 1f);
        Vec3 onPlane = fwd - zenith * Dot(fwd, zenith);
        if (onPlane.LengthSquared() < 1e-6f)
            return zenith;
        return Normalize(zenith + Normalize(onPlane));
    }

    // Clamp an aim into the arc: an aim past the arc edge is swung up to the edge in the plane it
    // shares with the zenith (the direction the gunner pushed toward survives; only the elevation is
    // pinned). A zero / non-finite aim, or one pointing straight down the zenith (no plane), lands on
    // the rest pose. Always returns a unit vector.
    public static Vec3 Clamp(Vec3 zenith, Vec3 aim)
    {
        zenith = Normalize(zenith);
        if (!float.IsFinite(aim.X) || !float.IsFinite(aim.Y) || !float.IsFinite(aim.Z))
            return Rest(zenith);
        float len2 = aim.LengthSquared();
        if (len2 < 1e-8f)
            return Rest(zenith);
        aim = aim * (1f / (float)System.Math.Sqrt(len2));
        float d = Dot(zenith, aim);
        if (d >= CosArc)
            return aim;
        Vec3 onPlane = aim - zenith * d;
        if (onPlane.LengthSquared() < 1e-6f)
            return Rest(zenith); // straight into the hull: no azimuth to keep
        // The arc edge in this azimuth: CosArc up the zenith, SinArc out along the horizon direction.
        return Normalize(zenith * CosArc + Normalize(onPlane) * SinArc);
    }

    // Station frame for a gimbal: Y = zenith, Z = the rest azimuth on the horizon (forward⊥, or an
    // arbitrary horizon direction when +Z ∥ zenith), X = Y × Z. Azimuth turns about Y, elevation
    // lifts from the horizon toward Y. Used by the gunner's controller; exposed so the maths is
    // unit-testable without Godot.
    public static (Vec3 X, Vec3 Y, Vec3 Z) Frame(Vec3 zenith)
    {
        Vec3 y = Normalize(zenith);
        var fwd = new Vec3(0f, 0f, 1f);
        Vec3 z = fwd - y * Dot(fwd, y);
        if (z.LengthSquared() < 1e-6f)
        {
            // Zenith ∥ +Z (tail / nose turret): pick "ship up" projected instead, so azimuth 0 still
            // means something stable (straight up the hull's Y).
            var up = new Vec3(0f, 1f, 0f);
            z = up - y * Dot(up, y);
        }
        z = Normalize(z);
        Vec3 x = Normalize(Cross(y, z));
        return (x, y, z);
    }

    // Gimbal → ship-local aim. azimuthRad turns about the zenith (0 = the frame's Z, positive toward
    // −X i.e. "mouse right" in the usual convention is handled by the caller); elevationRad in
    // [MinElevationRad, MaxElevationRad] lifts from the arc floor through the horizon (0) to the
    // zenith. The result is always inside the arc.
    public static Vec3 FromGimbal(Vec3 zenith, float azimuthRad, float elevationRad)
    {
        var (x, y, z) = Frame(zenith);
        if (elevationRad < MinElevationRad)
            elevationRad = MinElevationRad;
        else if (elevationRad > MaxElevationRad)
            elevationRad = MaxElevationRad;
        float ce = (float)System.Math.Cos(elevationRad);
        float se = (float)System.Math.Sin(elevationRad);
        float ca = (float)System.Math.Cos(azimuthRad);
        float sa = (float)System.Math.Sin(azimuthRad);
        Vec3 horizon = z * ca + x * sa;
        return Normalize(horizon * ce + y * se);
    }

    // Traverse: swing the gun's CURRENT aim toward the gunner's DESIRED aim under a speed cap and a
    // wind-up/wind-down acceleration, over `dt` seconds. `rate` is the mount's scalar traverse speed
    // (rad/s), carried between calls; it ramps up by accel, is capped by slew, and is held below
    // the speed that could still stop exactly on the target (v² ≤ 2·a·θ), so the gun settles on the
    // desired aim without overshoot. Both peers run this — the server per sim tick, the gunner's
    // client per frame — from the same streamed HardpointDef numbers, so the aim the bolts leave on
    // is the aim the gunner watched the gun reach. A non-positive slew/accel (an unauthored test
    // def) snaps straight to the desired aim.
    public static Vec3 Slew(Vec3 current, Vec3 desired, ref float rate, float slewRad, float accelRad, float dt)
    {
        current = Normalize(current);
        desired = Normalize(desired);
        if (slewRad <= 0f || accelRad <= 0f || dt <= 0f)
        {
            rate = 0f;
            return desired;
        }
        float cosT = System.Math.Clamp(Dot(current, desired), -1f, 1f);
        float theta = (float)System.Math.Acos(cosT);
        if (theta < 1e-4f)
        {
            rate = 0f;
            return desired;
        }
        float stopCap = (float)System.Math.Sqrt(2f * accelRad * theta);
        float target = System.Math.Min(slewRad, stopCap);
        rate = System.Math.Min(rate + accelRad * dt, target);
        float step = System.Math.Min(rate * dt, theta);
        // Rotate `current` toward `desired` by `step` inside their shared plane. Antiparallel aims
        // (no plane) pivot through the zenith-free fallback: any perpendicular will do, the arc clamp
        // downstream keeps the result legal.
        Vec3 axisDir = desired - current * cosT;
        if (axisDir.LengthSquared() < 1e-10f)
            axisDir =
                System.Math.Abs(current.Y) < 0.9f
                    ? Cross(current, new Vec3(0f, 1f, 0f))
                    : Cross(current, new Vec3(1f, 0f, 0f));
        axisDir = Normalize(axisDir);
        float c = (float)System.Math.Cos(step);
        float s = (float)System.Math.Sin(step);
        return Normalize(current * c + axisDir * s);
    }

    // Ship-local aim → (azimuth, elevation) in the station frame — the inverse of FromGimbal for an
    // aim inside the arc (used to seed the gimbal from the rest pose).
    public static (float Azimuth, float Elevation) ToGimbal(Vec3 zenith, Vec3 aim)
    {
        var (x, y, z) = Frame(zenith);
        aim = Normalize(aim);
        float e = (float)System.Math.Asin(System.Math.Clamp(Dot(aim, y), -1f, 1f));
        float a = (float)System.Math.Atan2(Dot(aim, x), Dot(aim, z));
        return (a, e < MinElevationRad ? MinElevationRad : e);
    }
}
