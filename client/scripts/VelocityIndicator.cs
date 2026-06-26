using Godot;

// A prograde velocity marker: a small circle on the HUD sitting where the local ship's
// velocity vector points — i.e. the direction it is actually TRAVELING, independent of where
// the nose is AIMING (the cyan aim reticle in TargetMarkers shows aim). The ship has true
// 6DOF flight, so strafing/drifting makes travel diverge from aim; this gives the player a
// cue for "where am I actually going."
//
// Only shown while moving forward: when traveling backwards the prograde point falls behind
// the camera (and the forward-hemisphere gate hides it explicitly), and below a small speed
// it's hidden so it doesn't jitter when nearly stationary. The screen position is smoothed
// frame-to-frame so the marker glides rather than snapping with per-frame velocity noise.
// Pure overlay — reads render transforms + the camera and draws, never touching authoritative
// state. Wired up by the Hud.
public partial class VelocityIndicator : Control
{
    private const float MinSpeed = 2f; // hide below this (u/s) to avoid jitter at rest
    private const float MarkerRange = 500f; // project this far along velocity (vanishing point of heading)
    private const float Radius = 9f; // marker ring radius (px)
    private const float SmoothRate = 12f; // exponential smoothing rate (higher = snappier)

    private static readonly Color VeloColor = new(1f, 1f, 1f, 0.6f); // faded white

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;

    // Mirror TargetMarkers: project through the F3 overview camera while it's active, else the
    // flight chase camera. Resolved per-access so it follows the toggle live.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    private Vector2 _smoothed; // smoothed screen position the marker is drawn at
    private bool _visible; // whether the marker should draw this frame

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera)
    {
        _world = world;
        _camera = camera;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
    }

    public override void _Process(double delta)
    {
        bool wasVisible = _visible;
        _visible = TryGetTarget(out Vector2 target);
        if (_visible)
        {
            // Snap on (re)appearance so it doesn't glide in from a stale position; otherwise
            // ease toward the target with frame-rate-independent exponential smoothing.
            _smoothed = wasVisible ? _smoothed.Lerp(target, 1f - Mathf.Exp(-SmoothRate * (float)delta)) : target;
        }
        QueueRedraw();
    }

    // The screen point of the prograde marker, or false if it shouldn't be shown.
    private bool TryGetTarget(out Vector2 screen)
    {
        screen = default;
        var local = _world.LocalShip;
        if (local == null)
            return false;

        Vector3 vel = local.Velocity;
        if (vel.Length() < MinSpeed)
            return false;

        Vector3 dir = vel.Normalized();
        Vector3 fwd = local.GlobalTransform.Basis.Z.Normalized();
        // Forward-hemisphere gate: only show when traveling forward (strafing-while-advancing
        // still shows; pure reverse is hidden, matching the "backwards = not visible" rule).
        if (dir.Dot(fwd) <= 0f)
            return false;

        Vector3 pt = local.GlobalPosition + dir * MarkerRange;
        Camera3D cam = Cam;
        if (cam.IsPositionBehind(pt))
            return false;

        screen = cam.UnprojectPosition(pt);
        return true;
    }

    public override void _Draw()
    {
        if (!_visible)
            return;
        DrawArc(_smoothed, Radius, 0f, Mathf.Tau, 24, VeloColor, 1.5f, true);
        DrawCircle(_smoothed, 1.5f, VeloColor);
    }
}
