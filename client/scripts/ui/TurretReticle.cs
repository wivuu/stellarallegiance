using Godot;

namespace StellarAllegiance.Ui;

// The crew GUNNER's crosshair (v42 crews slice 2): a centre ring with four tick marks, drawn where
// the gun cam looks. A gunner has no hull of their own, so the pilot's whole centre-screen stack
// (SystemRing, the aim reticle, the velocity indicator) is blank while riding — this is the only
// thing marking where the bolts go.
//
// It reads Data normally and Warn while TurretController.Clamped, i.e. while the gunner is pushing
// the aim down through the station's horizon into the captain's hull: the gun simply stops there, so
// the ring says so rather than letting the input vanish. Mouse-transparent like every HUD overlay,
// and it hides whenever a full-screen surface owns the screen — the same gate the GunnerStrip uses.
public partial class TurretReticle : Control
{
    private const float Radius = 15f;
    private const float TickInner = 5f;
    private const float TickOuter = 11f;
    private const float RingWidth = 1.6f;

    // Showcase fixture: when set, the reticle paints this clamp state and never consults the world.
    private bool? _mock;

    public void Init()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    // Render standalone in the gallery: `clamped` picks the Warn (arc-edge) variant.
    public void SetMock(bool clamped)
    {
        _mock = clamped;
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
        if (show)
            QueueRedraw(); // the clamp state flips with the input, not on a repaint gate
    }

    public override void _Draw()
    {
        bool clamped = _mock ?? TurretController.Clamped;
        Color c = clamped ? DesignTokens.Warn : DesignTokens.Data;
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
    }
}
