using Godot;
using StellarAllegiance.Ui;

// HUD system ring: the HULL + BOOST gauges from the "Stellar Allegiance" Game-HUD design.
// Two concentric segmented arc gauges framing the aim reticle (centre of screen space) —
// HULL on the right span, BOOST on the left span, top and bottom left open so vertical aim
// stays clear. The game has no shield, so the design's SHLD arc is omitted.
//
// Pure overlay: reads the local ship's authoritative-derived state (Health/MaxHealth, the
// synced afterburner ramp AbPower) and the active camera, and draws. Never touches
// authoritative state. Created and wired up by the Hud, like the other combat overlays.
public partial class SystemRing : Control
{
    // Muzzle constants mirrored from TargetMarkers / PredictionController so the ring centres
    // on the SAME point as the aim reticle (the firing line, forward of the nose).
    private const float NoseOffset = 3f;
    private const float DefaultAimRange = 500f;

    private const float Radius = 82f; // arc radius (px) — frames the reticle/lead circle
    private const float ArcWidth = 7f; // lit/track block thickness (px)
    private const int Blocks = 10; // segments per gauge
    private const float SpanDeg = 130f; // angular sweep of each gauge
    private const float GapDeg = 2.4f; // dead space between blocks

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;

    // Match TargetMarkers: project through the F3 overview camera while the sector map is
    // open, otherwise the flight chase camera. Resolved per-access so it follows the toggle.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera)
    {
        _world = world;
        _camera = camera;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme
    }

    public override void _Process(double delta)
    {
        Visible = _world.LocalShip != null;
        if (Visible)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var local = _world.LocalShip;
        if (local == null)
            return;

        // Centre on the aim reticle (muzzle projected forward along the nose) so the ring
        // hugs the crosshair the player is already looking at; fall back to screen centre
        // when that point is behind the camera.
        Camera3D cam = Cam;
        Vector2 c;
        Vector3 fwd = local.GlobalTransform.Basis.Z.Normalized();
        Vector3 reticle = local.GlobalPosition + fwd * (NoseOffset + DefaultAimRange);
        if (cam.IsPositionBehind(reticle))
            c = GetViewportRect().Size * 0.5f;
        else
            c = cam.UnprojectPosition(reticle);

        Color track = DesignTokens.BorderLo;

        // HULL — right span (centre 0° = +X). Lit from the bottom (high-angle end) and
        // tinted by health tier (green → amber → red) so a failing hull reads at a glance.
        float hullFrac = local.MaxHealth > 0f ? Mathf.Clamp(local.Health / local.MaxHealth, 0f, 1f) : 0f;
        SegmentedArc(c, 0f, hullFrac, HealthColor(hullFrac), track, litFromEnd: true);

        // BOOST — left span (centre 180°). AbPower is already 0..1.
        float boostFrac = Mathf.Clamp(local.AbPower, 0f, 1f);
        SegmentedArc(c, 180f, boostFrac, DesignTokens.Warn, track, litFromEnd: false);

        // Mono labels just outside each gauge: tag in the token colour, value in TextHi.
        DrawTagValue(c + new Vector2(Radius + 12f, 4f), "HULL", $"{local.Health:0}", HealthColor(hullFrac), rightAlign: false);
        DrawTagValue(c + new Vector2(-(Radius + 12f), 4f), "BST", $"{boostFrac * 100f:0}", DesignTokens.Warn, rightAlign: true);
    }

    // One segmented arc gauge centred at `centerDeg`, sweeping `SpanDeg`. Each block is a
    // short DrawArc; lit blocks use `lit`, the rest `track`. `litFromEnd` lights from the
    // high-angle (bottom) end so the hull drains downward like the design's HP arc.
    private void SegmentedArc(Vector2 c, float centerDeg, float value, Color lit, Color track, bool litFromEnd)
    {
        float start = centerDeg - SpanDeg * 0.5f;
        float cell = SpanDeg / Blocks;
        int filled = Mathf.Clamp(Mathf.RoundToInt(value * Blocks), 0, Blocks);
        for (int i = 0; i < Blocks; i++)
        {
            bool isLit = litFromEnd ? i >= Blocks - filled : i < filled;
            float a1 = Mathf.DegToRad(start + i * cell + GapDeg * 0.5f);
            float a2 = Mathf.DegToRad(start + (i + 1) * cell - GapDeg * 0.5f);
            DrawArc(c, Radius, a1, a2, 6, isLit ? lit : track, ArcWidth, true);
        }
    }

    // Draw "TAG value" in JetBrains Mono — tag in `tagColor`, value in TextHi. When
    // rightAlign, the pair ends at `anchor` (used for the left/BST gauge).
    private void DrawTagValue(Vector2 anchor, string tag, string value, Color tagColor, bool rightAlign)
    {
        const int size = 12;
        Font font = UiFonts.Mono;
        string combined = tag + " " + value;
        float tagW = font.GetStringSize(tag + " ", HorizontalAlignment.Left, -1, size).X;
        Vector2 pos = anchor;
        if (rightAlign)
        {
            float totalW = font.GetStringSize(combined, HorizontalAlignment.Left, -1, size).X;
            pos.X -= totalW;
        }
        DrawString(font, pos, tag, HorizontalAlignment.Left, -1, size, tagColor);
        DrawString(font, pos + new Vector2(tagW, 0f), value, HorizontalAlignment.Left, -1, size, DesignTokens.TextHi);
    }

    // Green at full hull, amber at half, red near death — matches the design's HP arc ramp
    // and the existing base-health bar in TargetMarkers.
    private static Color HealthColor(float frac) =>
        frac > 0.5f ? DesignTokens.Ok
        : frac > 0.25f ? DesignTokens.Warn
        : DesignTokens.Danger;
}
