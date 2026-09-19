using static StellarAllegiance.Shared.Vec3;

namespace StellarAllegiance.Shared;

// Crew-served turret aiming — THE single arc/rest rule for a turret station (v42 crews slice 2).
// A station's HardpointDef.Dir is its ZENITH (the outward normal of the mount); the gunner aims a
// ship-local unit vector anywhere in the cone around it — a hemisphere plus a little depression
// below the mount's horizon (user steer 2026-09-13: the bare hemisphere felt too restricted). Three
// mirrors consume this and must never drift (same pattern as FireCadence):
//   - server Simulation.TryFireTurrets   (trusts the gunner's aim: arc-clamps it and fires along it)
//   - client TurretController             (the gunner: free look = the aim, sent on the wire, local bolts)
//   - client ShipRenderer.ApplyTurrets    (remote turrets: rebuilds bolts along the streamed aim)
// Turret aim is CLIENT-AUTHORITATIVE (user steer 2026-09-13: a gun that lags the sight was "very
// laggy and difficult to control"): the gun is exactly where the gunner looks. The station's slew
// SPEED (HardpointDef.TurretSlewRad) is the only physics left, and the CLIENT applies it — as the
// SUSTAINED-rate limit on the look (SlewLimit's token bucket: small motions are never limited). The server never traverses anything: it
// takes the aim the gunner sent, clamps it into the arc (so a stale or forged aim can never fire
// through the hull) and fires along it.
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

    // Slew-speed default for a station that authors no `slew-deg` and whose world authors no
    // `turret.default-slew-deg` (projection fills HardpointDef.TurretSlewRad): the fastest SUSTAINED
    // rate the gunner's look — and so the gun — may turn, in degrees per second. A light mount comes
    // round 180° in a second; a heavy capital station authors lower. There is no wind-up.
    public const double DefaultSlewDeg = 180.0;

    // The slew limit is a TOKEN BUCKET, not a per-frame cap (user steer 2026-09-19: "don't limit small
    // motions for any type of turret, limit max turn rate"). The bucket holds SlewWindowSec worth of
    // traverse (slew × window) and refills at the slew rate, so any motion smaller than the bucket —
    // a nudge, a correction, a short flick — passes 1:1 on every mount, and only a SUSTAINED spin is
    // held to the slew rate. A per-frame cap did the opposite: at a 60 Hz frame it bit at a few
    // hundred px/s of hand speed, so almost every motion was truncated and the response went
    // sub-linear ("small movements feel large, large movements feel slow").
    public const float SlewWindowSec = 0.15f;

    // One frame of the bucket. `budgetRad` is the gunner's carried allowance (seed it with
    // SlewCapacity on taking a seat); `turnRad` is the magnitude of the turn the mouse asked for this
    // frame. Returns the scale (0..1) to apply to that turn. Motion past the allowance is DROPPED, not
    // banked — a banked turn would keep the view moving after the hand stopped. slewRad <= 0 is an
    // uncapped station.
    public static float SlewLimit(
        ref float budgetRad,
        float slewRad,
        float dt,
        float turnRad,
        float windowSec = SlewWindowSec
    )
    {
        if (slewRad <= 0f)
            return 1f;
        float capacity = SlewCapacity(slewRad, dt, windowSec);
        budgetRad = MathF.Min(budgetRad + slewRad * MathF.Max(dt, 0f), capacity);
        if (turnRad <= 0f)
            return 1f;
        float granted = MathF.Min(turnRad, budgetRad);
        budgetRad -= granted;
        return granted / turnRad;
    }

    // The bucket's size: the window's worth of traverse, never less than one frame's so a long frame
    // (or a zero window, = the old per-frame cap) still turns at the slew rate.
    public static float SlewCapacity(float slewRad, float dt = 0f, float windowSec = SlewWindowSec) =>
        MathF.Max(slewRad * MathF.Max(windowSec, 0f), slewRad * MathF.Max(dt, 0f));

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
        // Length in double: a finite but huge forged aim (1e30) squares past float range, and an
        // infinite len2 would scale the aim to ZERO — which then passes the arc test below.
        double len2 = (double)aim.X * aim.X + (double)aim.Y * aim.Y + (double)aim.Z * aim.Z;
        if (len2 < 1e-8)
            return Rest(zenith);
        double inv = 1.0 / System.Math.Sqrt(len2);
        aim = new Vec3((float)(aim.X * inv), (float)(aim.Y * inv), (float)(aim.Z * inv));
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
