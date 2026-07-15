using Godot;
using StellarAllegiance.Ui;

// HUD system ring: the HULL + SHIELD + FUEL/BOOST gauges from the "Stellar Allegiance" Game-HUD
// design. Segmented arc gauges framing the aim reticle (centre of screen space) — on the right
// span, HULL as segmented blocks (inner) with the regenerating SHIELD as a solid arc wrapping it
// (outer); FUEL (or legacy BOOST) on the left span. Top and bottom are left open so vertical aim
// stays clear. The SHLD arc only draws on hulls that actually carry a shield (MaxShield > 0).
//
// The left gauge reads FUEL (Fuel/MaxFuel) on hulls with a modeled tank (MaxFuel > 0); on
// legacy hulls (MaxFuel <= 0, fuel unmodeled) it falls back to the old AbPower ramp so those
// classes keep a BOOST readout instead of a meaningless empty gauge.
//
// Pure overlay: reads the local ship's authoritative-derived state (Health/MaxHealth, Fuel/
// MaxFuel, the synced afterburner ramp AbPower) and the active camera, and draws. Never
// touches authoritative state. Created and wired up by the Hud, like the other combat overlays.
public partial class SystemRing : Control
{
    // Must stay consistent with TargetMarkers.AimReticleScreenPoint so the ring centres on
    // the SAME point as the aim reticle (the firing line, forward of the nose): the equipped
    // bolt weapon's effective range (via DefRegistry.BoltAimRange), or this anchor for a
    // pod/unarmed hull (or before defs stream in).
    private const float DefaultAimRange = 500f;

    private const float Radius = 82f; // arc radius (px) — frames the reticle/lead circle
    private const float ArcWidth = 7f; // lit/track block thickness (px)
    private const int Blocks = 10; // segments per gauge
    private const float SpanDeg = 130f; // angular sweep of each gauge
    private const float GapDeg = 2.4f; // dead space between blocks
    private const float ShieldRadius = Radius + 8f; // SHLD solid arc wraps just outside the HULL blocks
    private const float ShieldWidth = 5f; // thinner than the hull blocks (design: solid outer band)

    private WorldRenderer _world = null!;
    private Camera3D _camera = null!;
    private DefRegistry _defs = null!; // resolves the local hull's bolt-weapon range for the reticle centre

    // Match TargetMarkers: project through the F3 overview camera while the sector map is
    // open, otherwise the flight chase camera. Resolved per-access so it follows the toggle.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    // Wired up by the Hud (which already resolves these siblings).
    public void Init(WorldRenderer world, Camera3D camera, DefRegistry defs)
    {
        _world = world;
        _camera = camera;
        _defs = defs;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        UiFonts.EnsureLoaded(); // custom-draw node reads fonts directly, not via a Theme
    }

    public override void _Process(double delta)
    {
        Visible = _world.LocalShip != null && !ZoomView.Active && !SectorOverview.Active; // scope circle or F3 map replaces these gauges
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
        float aimRange = _defs.BoltAimRange(
            local.IsPod ? DefRegistry.PodClassId : (byte)local.Class,
            DefaultAimRange,
            local.IsPod ? null : local.LoadoutIds // effective gun's reach, not the class default
        );
        Vector3 reticle = local.GlobalPosition + fwd * aimRange;
        if (cam.IsPositionBehind(reticle))
            c = GetViewportRect().Size * 0.5f;
        else
            c = cam.UnprojectPosition(reticle);

        Color track = DesignTokens.BorderLo;

        // HULL — right span (centre 0° = +X). Lit from the bottom (high-angle end) and
        // tinted by health tier (green → amber → red) so a failing hull reads at a glance.
        float hullFrac = local.MaxHealth > 0f ? Mathf.Clamp(local.Health / local.MaxHealth, 0f, 1f) : 0f;
        SegmentedArc(c, 0f, hullFrac, HealthColor(hullFrac), track, litFromEnd: true);

        // SHIELD — a solid cyan arc wrapping just OUTSIDE the hull blocks on the same right span,
        // filled from the bottom to match. Only on hulls that carry a shield (MaxShield > 0), so a
        // shieldless class (scout/pod) shows no arc. Cyan = the chrome gauge-arc convention.
        bool hasShield = local.MaxShield > 0f;
        float shieldFrac = hasShield ? Mathf.Clamp(local.Shield / local.MaxShield, 0f, 1f) : 0f;
        if (hasShield)
            SolidArc(c, ShieldRadius, 0f, shieldFrac, DesignTokens.TeamAccent, track, ShieldWidth);

        // FUEL — left span (centre 180°), on hulls with a modeled tank. Legacy hulls
        // (MaxFuel <= 0) keep the old BOOST/AbPower ramp instead.
        bool hasFuel = local.MaxFuel > 0f;
        float leftFrac = hasFuel ? Mathf.Clamp(local.Fuel / local.MaxFuel, 0f, 1f) : Mathf.Clamp(local.AbPower, 0f, 1f);
        Color leftColor = hasFuel ? FuelColor(leftFrac) : DesignTokens.Warn;
        SegmentedArc(c, 180f, leftFrac, leftColor, track, litFromEnd: false);

        // Mono labels just outside each gauge: tag in the token colour, value in TextHi. SHLD sits
        // above HULL on the right (design order), only when this hull carries a shield.
        if (hasShield)
            DrawTagValue(c + new Vector2(Radius + 12f, -14f), "SHLD", $"{local.Shield:0}", DesignTokens.TeamAccent, rightAlign: false);
        DrawTagValue(c + new Vector2(Radius + 12f, 4f), "HULL", $"{local.Health:0}", HealthColor(hullFrac), rightAlign: false);
        string leftTag = hasFuel ? "FUEL" : "BST";
        DrawTagValue(c + new Vector2(-(Radius + 12f), 4f), leftTag, $"{leftFrac * 100f:0}", leftColor, rightAlign: true);
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

    // One SOLID arc gauge (not segmented) at `radius`, centred at `centerDeg`. Its ends are inset by
    // half the block gap so the arc caps exactly at the hull blocks' outer edges (the first block
    // starts at start+GapDeg/2, the last ends at start+SpanDeg-GapDeg/2) instead of overflowing past
    // them. The dim track spans that capped range; the lit fill grows from the high-angle (bottom)
    // end so the shield drains the same direction the hull blocks do. Used for the SHLD wrap arc.
    private void SolidArc(Vector2 c, float radius, float centerDeg, float value, Color lit, Color track, float width)
    {
        float half = SpanDeg * 0.5f - GapDeg * 0.5f; // cap to the hull's lit extent, not the full span
        float lo = centerDeg - half; // top end (aligned to the first hull block's start)
        float hi = centerDeg + half; // bottom end (aligned to the last hull block's end)
        DrawArc(c, radius, Mathf.DegToRad(lo), Mathf.DegToRad(hi), 48, track, width, true);
        float v = Mathf.Clamp(value, 0f, 1f);
        if (v > 0f)
            DrawArc(c, radius, Mathf.DegToRad(hi - (hi - lo) * v), Mathf.DegToRad(hi), 48, lit, width, true);
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

    // Amber tank, red when critically low (<=25%) — mirrors HealthColor's low-end tier so an
    // empty-tank warning reads the same way a failing hull does.
    private static Color FuelColor(float frac) => frac > 0.25f ? DesignTokens.Warn : DesignTokens.Danger;
}
