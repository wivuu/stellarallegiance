using StellarAllegiance.Shared;
using static StellarAllegiance.Shared.Vec3;

// The gun cam's ROLL reference, factored out of CameraRig/TurretController so the maths is Godot-free
// and unit-tested (tests/CrewStoreTest) — the same reasoning that put the station lookup in
// TurretStations.
//
// The naive "up" for a camera looking down the turret's aim is the station's zenith projected off the
// aim. That projection VANISHES at the zenith itself (aim ∥ zenith), and any fallback picked there is
// a different vector than the one a frame earlier — so a gunner tracking a target up over the mount
// watched the whole view snap around as the gun neared the pole (user feedback 2026-09-13, item 5).
//
// The fix is to build the up from the gimbal instead of from a projection: for an aim at azimuth `a`
// and elevation `e` in the station frame, `aim = horizon(a)·cos(e) + zenith·sin(e)` and the ELEVATION
// TANGENT `-horizon(a)·sin(e) + zenith·cos(e)` is a unit vector perpendicular to the aim at EVERY
// elevation, the pole included (there it is simply −horizon(a)). It only needs an azimuth, and near
// the pole the azimuth is the atan2 of two vanishing components — pure noise — so the caller carries a
// CONTINUOUS azimuth that stops tracking inside a small band around the pole and stays on its branch
// if the aim ever crosses over the top. That branch is what keeps the horizon from flipping.
public static class TurretGimbal
{
    // Half-width of the dead band around the pole where the aim's azimuth is not readable (~1°). A
    // degree of aim is far below a frame of traverse at any authored slew rate, so freezing the
    // azimuth here costs no fidelity.
    public const float PoleBandRad = 0.017453292f;

    private static readonly float CosPoleBand = (float)System.Math.Cos(PoleBandRad);

    // The station-frame horizon direction at `azimuth` — the same construction FromGimbal uses, so a
    // tracked azimuth and the shared gimbal maths always mean the same circle.
    public static Vec3 Horizon(Vec3 zenith, float azimuth)
    {
        var (x, _, z) = TurretAim.Frame(zenith);
        return Normalize(z * (float)System.Math.Cos(azimuth) + x * (float)System.Math.Sin(azimuth));
    }

    // Carry the aim's azimuth forward one frame. Two cases keep the previous value rather than reading
    // a fresh one, and both exist to keep the up vector continuous:
    //   • inside the pole band the azimuth is noise (the aim has no readable horizon component);
    //   • an aim that has crossed OVER the top sits on the far side of its own branch — re-deriving
    //     there returns the opposite azimuth with a reflected elevation, which names the same
    //     direction but flips the elevation tangent. Staying on the branch (and letting the elevation
    //     run past 90°) is what "the view never flips" means.
    // The gimbal's own clamp keeps a live gunner below 90°, so the second case only guards the maths;
    // it is what makes the pole crossing testable.
    public static float TrackAzimuth(Vec3 zenith, Vec3 aim, float lastAzimuth)
    {
        Vec3 y = Normalize(zenith);
        aim = Normalize(aim);
        if (System.Math.Abs(Dot(aim, y)) >= CosPoleBand)
            return lastAzimuth;
        if (Dot(aim, Horizon(y, lastAzimuth)) < 0f)
            return lastAzimuth;
        return TurretAim.ToGimbal(y, aim).Azimuth;
    }

    // The camera up for an aim on the branch `azimuth`: the elevation tangent. Always unit length and
    // always perpendicular to the aim — including at the pole, where it degenerates to −horizon rather
    // than to nothing. The elevation is measured against the BRANCH (atan2, so it may exceed 90° once
    // the aim has crossed the top) rather than read back from ToGimbal, which would fold it.
    public static Vec3 Up(Vec3 zenith, Vec3 aim, float azimuth)
    {
        Vec3 y = Normalize(zenith);
        aim = Normalize(aim);
        Vec3 horizon = Horizon(y, azimuth);
        float e = (float)System.Math.Atan2(Dot(aim, y), Dot(aim, horizon));
        return Normalize(horizon * -(float)System.Math.Sin(e) + y * (float)System.Math.Cos(e));
    }
}
