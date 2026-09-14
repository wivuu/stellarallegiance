using StellarAllegiance.Shared;
using static StellarAllegiance.Shared.Vec3;

// The gunner's FREE-LOOK orientation — a ship-local camera basis the mouse turns about its OWN axes,
// the way a pilot's nose turns about the hull's. Factored out of TurretController so the maths is
// Godot-free and unit-tested (tests/CrewStoreTest), the same reasoning that put the station lookup in
// TurretStations.
//
// It deliberately replaces the azimuth/elevation gimbal this file used to hold (user steer
// 2026-09-13): "the gun camera should not have an 'azimuth', it should just carry through as if there
// were no difference at all until it reaches the other side." A gimbal has a pole — a singular axis
// where the azimuth stops meaning anything and the horizon has to be picked rather than carried — so
// no matter how carefully the branch is tracked, the view reads as spun around something. There is no
// pole here at all: yaw turns about the basis' OWN up, pitch about its OWN right, and looking up past
// the zenith simply carries over the top and keeps going down the far side, the up going with it like
// an aircraft's does through a loop.
//
// The ONLY limit is the station's firing arc (TurretAim.ArcHalfAngleRad around the mount's zenith),
// and it is applied as a rotation of the WHOLE basis — forward lands exactly on the arc edge and the
// up stays continuous — rather than as a clamp on an angle, which is what used to make the horizon
// jump at the edge.
//
// This basis IS the gun camera AND the gun (user steer 2026-09-13: the turret cam is "a free-look
// camera, constrained only by its position on the ship, the ship's orientation and its arc fence, but
// not other physics of the turret"). Its forward is the gun's aim itself — where the bolts leave and
// where the one reticle sits — so nothing trails anything. The only brake is the per-frame turn cap the
// CALLER applies (TurretController.SampleAim scales the mouse delta down to the station's slew speed)
// before handing the turn to Yaw/Pitch; this class just turns the basis it is told to.
//
// Axes are the columns of a right-handed basis in the hull's frame: Z = forward (the aim), Y = up,
// X = Y × Z. Note that the hull's forward is +Z, so X points to the viewer's LEFT — the same
// convention CameraRig's FaceForward exists to undo — which is why the caller yaws by MINUS the
// mouse's X.
public sealed class TurretLook
{
    public Vec3 X,
        Y,
        Z;

    // A freshly manned station: look along the shared rest pose with the mount's zenith (projected
    // off the aim) overhead — "standing on the deck looking where the gun rests".
    public static TurretLook Seed(Vec3 zenith)
    {
        zenith = Normalize(zenith);
        Vec3 fwd = TurretAim.Rest(zenith);
        Vec3 up = zenith - fwd * Dot(zenith, fwd);
        // Rest() only returns the zenith itself for a station whose zenith is parallel to ship +Z, in
        // which case any perpendicular is as good an up as another.
        if (up.LengthSquared() < 1e-8f)
            up = PerpendicularTo(fwd);
        var look = new TurretLook { Z = fwd, Y = Normalize(up) };
        look.Orthonormalize(); // fills X = Y × Z and squares the three up
        return look;
    }

    // Turn about the basis' OWN up. Positive swings the forward toward +X — screen LEFT in the hull's
    // forward-is-+Z frame — so a caller feeding mouse motion passes the negated delta.
    public void Yaw(float rad) => RotateAll(Y, rad);

    // Pitch about the basis' OWN right. Positive swings the forward toward −Y (nose down), so the
    // first-person "mouse up = look up" sign is the raw mouse delta (Godot's Y grows downward).
    public void Pitch(float rad) => RotateAll(X, rad);

    // Pull the forward back inside the station's firing arc, carrying the up with it: the whole basis
    // rotates about the axis perpendicular to the aim and the zenith by exactly the overshoot, so the
    // forward lands ON the arc edge and the horizon never jumps. Returns whether it had to — the
    // gunner is pushing into the edge, which is the one gunner-specific cue the reticle shows.
    public bool ClampToArc(Vec3 zenith)
    {
        zenith = Normalize(zenith);
        float d = System.Math.Clamp(Dot(Z, zenith), -1f, 1f);
        if (d >= CosArc)
            return false;
        Vec3 axis = Cross(Z, zenith);
        // Forward antiparallel to the zenith: every plane through the two is equally good, so pick the
        // basis' own right and swing back the way the gunner is already oriented.
        axis = axis.LengthSquared() < 1e-10f ? X : Normalize(axis);
        RotateAll(axis, (float)System.Math.Acos(d) - TurretAim.ArcHalfAngleRad);
        return true;
    }

    // Re-square the axes after a step's worth of float drift. Z is the authority (it is the aim), the
    // up is kept as close to what it was as the forward allows, and X is rebuilt from both.
    public void Orthonormalize()
    {
        Z = Normalize(Z);
        Vec3 x = Cross(Y, Z);
        if (x.LengthSquared() < 1e-10f)
            x = PerpendicularTo(Z); // up collapsed onto the aim (only reachable through float decay)
        X = Normalize(x);
        Y = Cross(Z, X);
    }

    // Rodrigues: rotate `v` about the unit `axis` by `rad` (right-handed).
    public static Vec3 Rotate(Vec3 v, Vec3 axis, float rad)
    {
        float c = (float)System.Math.Cos(rad);
        float s = (float)System.Math.Sin(rad);
        return v * c + Cross(axis, v) * s + axis * (Dot(axis, v) * (1f - c));
    }

    // cos of the arc half-angle, so the clamp test costs no trig on the common in-arc frame.
    private static readonly float CosArc = (float)System.Math.Cos(TurretAim.ArcHalfAngleRad);

    // Turn the whole basis so the three axes stay one rigid frame — rotating only the forward is what
    // forces an up to be invented afterwards.
    private void RotateAll(Vec3 axis, float rad)
    {
        if (rad == 0f)
            return;
        X = Rotate(X, axis, rad);
        Y = Rotate(Y, axis, rad);
        Z = Rotate(Z, axis, rad);
        Orthonormalize();
    }

    // Any unit vector perpendicular to `v` — a last-resort seed, never a value the gunner sees on a
    // normal frame.
    private static Vec3 PerpendicularTo(Vec3 v) =>
        Normalize(System.Math.Abs(v.Y) < 0.9f ? Cross(v, new Vec3(0f, 1f, 0f)) : Cross(v, new Vec3(1f, 0f, 0f)));
}
