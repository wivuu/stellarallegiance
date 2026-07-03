using Godot;
using StellarAllegiance.Ui;

// A prograde velocity marker: a small circle on the HUD sitting where the local ship's
// velocity vector points — i.e. the direction it is actually TRAVELING, independent of where
// the nose is AIMING (the cyan aim reticle in TargetMarkers shows aim). The ship has true
// 6DOF flight, so strafing/drifting makes travel diverge from aim; this gives the player a
// cue for "where am I actually going."
//
// Styled after the design's "self MOVEMENT indicator": a dim cyan ring + centre dot, a mono
// speed readout to its right, and a couple of faint velocity-trail dots streaming toward the
// marker. It shares the cyan accent with the aim reticle by design — the two are told apart
// by the marker being dimmer, spoke-less, and speed-tagged (and they only coincide when
// flying dead ahead).
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
    private const float Radius = 13f; // marker ring radius (px) — design's ~26px diameter
    private const float SmoothRate = 12f; // exponential smoothing rate (higher = snappier)

    // Cyan structural accent, matching the design's movement indicator. The ring is dim, the
    // centre dot brighter, and the trail dots fainter still — see the _Draw comment.
    private static readonly Color RingColor = new(DesignTokens.TeamAccent, 0.45f);
    private static readonly Color DotColor = new(DesignTokens.TeamAccent, 0.75f);

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;

    // Mirror TargetMarkers: project through the F3 overview camera while it's active, else the
    // flight chase camera. Resolved per-access so it follows the toggle live.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    private Vector2 _smoothed; // smoothed screen position the marker is drawn at
    private bool _visible; // whether the marker should draw this frame
    private float _speed; // current speed (u/s), shown in the mono readout
    private Vector2 _trailDir; // unit screen vector ship->marker; the trail streams back along it
    private bool _hasTrail; // whether the ship centre projected in front (so a trail can draw)

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera)
    {
        _world = world;
        _camera = camera;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // mono speed readout is drawn directly, not via a Theme
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

    // The screen point of the prograde marker, or false if it shouldn't be shown. Also caches
    // the speed readout and the screen-space trail direction (ship centre -> marker).
    private bool TryGetTarget(out Vector2 screen)
    {
        screen = default;
        _hasTrail = false;
        var local = _world.LocalShip;
        if (local == null)
            return false;

        Vector3 vel = local.Velocity;
        _speed = vel.Length();
        if (_speed < MinSpeed)
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

        // Trail direction: project the ship centre and point from it toward the marker, so the
        // trail dots stream back along the actual on-screen travel path. Skipped (no trail)
        // when the ship centre is behind the camera.
        if (!cam.IsPositionBehind(local.GlobalPosition))
        {
            Vector2 shipScreen = cam.UnprojectPosition(local.GlobalPosition);
            Vector2 d = screen - shipScreen;
            if (d.LengthSquared() > 1e-4f)
            {
                _trailDir = d.Normalized();
                _hasTrail = true;
            }
        }
        return true;
    }

    public override void _Draw()
    {
        if (!_visible)
            return;

        // Velocity-trail dots streaming back toward the ship (faintest furthest out), so the
        // marker reads as "where I'm heading" with a sense of motion. Drawn under the marker.
        if (_hasTrail)
        {
            DrawCircle(_smoothed - _trailDir * 7f, 1f, new Color(DesignTokens.TeamAccent, 0.34f));
            DrawCircle(_smoothed - _trailDir * 14f, 1f, new Color(DesignTokens.TeamAccent, 0.22f));
        }

        DrawArc(_smoothed, Radius, 0f, Mathf.Tau, 24, RingColor, 1.5f, true);
        DrawCircle(_smoothed, 1.5f, DotColor);

        // Mono speed readout to the marker's right (design: "▲ 218 m/s"); game units are `u`.
        DrawString(
            UiFonts.Mono,
            _smoothed + new Vector2(Radius + 5f, -Radius + 7f),
            $"▲ {_speed:0} u/s",
            HorizontalAlignment.Left,
            -1,
            9,
            DesignTokens.Text2
        );
    }
}
