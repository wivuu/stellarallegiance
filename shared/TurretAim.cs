using static StellarAllegiance.Shared.Vec3;

namespace StellarAllegiance.Shared;

// Crew-served turret aiming — THE single arc/rest rule for a turret station (v42 crews slice 2).
// A station's HardpointDef.Dir is its ZENITH (the outward normal of the mount); the gunner aims a
// ship-local unit vector anywhere in the hemisphere around it. Three mirrors consume this and must
// never drift (same pattern as FireCadence):
//   - server Simulation.TryFireTurrets   (authoritative: clamps the held aim, fires along it)
//   - client TurretController             (the gunner: azimuth/elevation gimbal → aim, local bolts)
//   - client ShipRenderer.ApplyTurrets    (remote turrets: rebuilds bolts along the streamed aim)
// No slew-rate limit in this slice — bolts leave where the reticle points; if one is ever wanted it
// belongs here so both peers apply the same one.
public static class TurretAim
{
    // Firing arc half-angle around the zenith: a full hemisphere. The hull sits below the station's
    // horizon, so the arc edge is the horizon itself.
    public const float ArcHalfAngleRad = 1.5707963267948966f;

    // TurretInputMessage.Flags bits.
    public const byte FlagFiring = 1;

    // Spread-seed "barrel" for a station: 0x80 | HardpointDef.Index — disjoint from the pilot's
    // barrel indices (< 128), so a turret volley never shares a scatter seed with a hull gun on the
    // same (ShipId, tick). Mirrored by every bolt rebuild.
    public static byte SpreadBarrel(byte hpIndex) => (byte)(0x80 | (hpIndex & 0x7F));

    // Is a (unit) aim inside the arc? Inclusive at the horizon.
    public static bool InArc(Vec3 zenith, Vec3 aim) => Dot(zenith, aim) >= -1e-6f;

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

    // Clamp an aim into the arc: an aim below the horizon is swung up to the horizon in the plane it
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
        if (d >= 0f)
            return aim;
        Vec3 onPlane = aim - zenith * d;
        if (onPlane.LengthSquared() < 1e-6f)
            return Rest(zenith); // straight into the hull: no azimuth to keep
        return Normalize(onPlane);
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
    // [0, π/2] lifts from the horizon to the zenith. The result is always inside the arc.
    public static Vec3 FromGimbal(Vec3 zenith, float azimuthRad, float elevationRad)
    {
        var (x, y, z) = Frame(zenith);
        if (elevationRad < 0f)
            elevationRad = 0f;
        else if (elevationRad > ArcHalfAngleRad)
            elevationRad = ArcHalfAngleRad;
        float ce = (float)System.Math.Cos(elevationRad);
        float se = (float)System.Math.Sin(elevationRad);
        float ca = (float)System.Math.Cos(azimuthRad);
        float sa = (float)System.Math.Sin(azimuthRad);
        Vec3 horizon = z * ca + x * sa;
        return Normalize(horizon * ce + y * se);
    }

    // Ship-local aim → (azimuth, elevation) in the station frame — the inverse of FromGimbal for an
    // aim inside the arc (used to seed the gimbal from the rest pose).
    public static (float Azimuth, float Elevation) ToGimbal(Vec3 zenith, Vec3 aim)
    {
        var (x, y, z) = Frame(zenith);
        aim = Normalize(aim);
        float e = (float)System.Math.Asin(System.Math.Clamp(Dot(aim, y), -1f, 1f));
        float a = (float)System.Math.Atan2(Dot(aim, x), Dot(aim, z));
        return (a, e < 0f ? 0f : e);
    }
}
