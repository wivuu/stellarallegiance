using Godot;

namespace StellarAllegiance.Ui;

// The crew GUNNER's crosshair (v42 crews slice 2): a centre ring with four tick marks, drawn where
// the gun cam looks. A gunner has no hull of their own, so the pilot's whole centre-screen stack
// (SystemRing, the aim reticle, the velocity indicator) is blank while riding — this is the only
// thing marking where the bolts go.
//
// TWO marks (slice 2b). The ring is the gun's ACTUAL aim — the gun cam looks straight down it, so it
// sits at screen centre. The small open square is the gunner's DESIRED aim, where the mouse has
// dragged the sight; the mount traverses toward it at its authored slew rate, so on a heavy capital
// station the square runs ahead and the ring visibly catches up. They coincide once the gun settles,
// and the square is dropped entirely inside a couple of pixels so a settled gun shows one clean mark.
//
// The ring's colour is the state in one glance: Warn while TurretController.Clamped (the gunner is
// pushing the aim down through the station's horizon into the captain's hull — the gun stops there,
// so the ring says so rather than letting the input vanish), Ok while TargetMarkers.OnSolution (the
// focused target's lead point is inside the gun's line — fire now), else Data. Clamp wins: a gun that
// can't reach is the more urgent fact. Mouse-transparent like every HUD overlay, and it hides whenever
// a full-screen surface owns the screen — the same gate the GunnerStrip uses.
public partial class TurretReticle : Control
{
    private const float Radius = 15f;
    private const float TickInner = 5f;
    private const float TickOuter = 11f;
    private const float RingWidth = 1.6f;

    // The desired-aim mark: a small open square, deliberately a different SHAPE from the ring so the
    // two never read as one blurred crosshair. Hidden inside DesiredDeadZone px of centre.
    private const float DesiredHalf = 4.5f;
    private const float DesiredWidth = 1.2f;
    private const float DesiredDeadZone = 2f;

    // Showcase fixture: when set, the reticle paints this state and never consults the world.
    private bool? _mock;
    private bool _mockOnSolution;
    private Vector2 _mockDesired;

    // Screen offset of the desired-aim mark from the ring, recomputed each frame while riding.
    private Vector2 _desired;
    private bool _hasDesired;

    public void Init()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    // Render standalone in the gallery: `clamped` picks the Warn (arc-edge) variant, `onSolution` the
    // Ok one, and `desired` offsets the second mark so the traversing state can be shown at rest.
    public void SetMock(bool clamped, bool onSolution = false, Vector2 desired = default)
    {
        _mock = clamped;
        _mockOnSolution = onSolution;
        _mockDesired = desired;
        MouseFilter = MouseFilterEnum.Ignore;
        // In game this is a full-rect overlay that draws at the screen centre; the gallery stacks it in
        // a box that sizes children by their MINIMUM, so reserve the ring's real footprint there (any
        // width the caller already asked for is kept).
        CustomMinimumSize = new Vector2(CustomMinimumSize.X, Radius * 2f + 24f);
        Visible = true;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_mock is not null)
            return;
        bool show = TurretController.Active && !SectorOverview.Active && !ShipLoadout.Active;
        if (show != Visible)
            Visible = show;
        if (!show)
            return;
        TrackDesired();
        QueueRedraw(); // the clamp / solution state flips with the input, not on a repaint gate
    }

    // Where the DESIRED aim lands on screen, as an offset from the ACTUAL one. Both are ship-local
    // directions from the same muzzle, so they are ranged to the same point along the gun's reach and
    // projected through the live camera — and taking the DIFFERENCE means the mark stays right even
    // where the gun cam's centre isn't exactly the aim (the eye sits above and behind the mount).
    private void TrackDesired()
    {
        _hasDesired = false;
        Camera3D? cam = GetViewport()?.GetCamera3D();
        if (cam is null)
            return;
        Transform3D pose = TurretController.RiddenPose;
        Vector3 muzzle = pose * TurretController.Station;
        float range = TurretController.AimRange;
        Vector3 actual = muzzle + (pose.Basis * TurretController.Aim).Normalized() * range;
        Vector3 want = muzzle + (pose.Basis * TurretController.DesiredAim).Normalized() * range;
        if (cam.IsPositionBehind(actual) || cam.IsPositionBehind(want))
            return;
        _desired = cam.UnprojectPosition(want) - cam.UnprojectPosition(actual);
        _hasDesired = true;
    }

    public override void _Draw()
    {
        bool clamped = _mock ?? TurretController.Clamped;
        bool onSolution = _mock is not null ? _mockOnSolution : TurretController.OnSolution;
        Color c =
            clamped ? DesignTokens.Warn
            : onSolution ? DesignTokens.Ok
            : DesignTokens.Data;
        Vector2 mid = Size * 0.5f;

        DrawArc(mid, Radius, 0f, Mathf.Tau, 48, c, RingWidth, antialiased: true);
        // Four ticks on the axes, detached from the ring so the centre of the shot stays readable
        // against a bright hull.
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Pi * 0.5f;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(mid + dir * (Radius + TickInner), mid + dir * (Radius + TickOuter), c, RingWidth, true);
        }
        DrawCircle(mid, 1.4f, c);

        Vector2 offset =
            _mock is not null ? _mockDesired
            : _hasDesired ? _desired
            : Vector2.Zero;
        if (offset.Length() < DesiredDeadZone)
            return; // the gun has caught up: one mark, not two overlapping ones
        // Dimmer than the ring — it is where the gun is GOING, not where it shoots.
        var want = new Color(c, 0.6f);
        Vector2 p = mid + offset;
        DrawRect(
            new Rect2(p - new Vector2(DesiredHalf, DesiredHalf), new Vector2(DesiredHalf * 2f, DesiredHalf * 2f)),
            want,
            false,
            DesiredWidth
        );
    }
}
