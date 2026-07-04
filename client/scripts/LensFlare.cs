using Godot;

// A subtle, screen-space lens flare for the cosmetic Sun. A lens flare is a CAMERA artifact —
// its ghosts/streak are arrayed relative to the screen center, not anchored to world geometry —
// so it lives here as a 2D overlay rather than a 3D billboard. It projects the sun's
// fixed sky direction (shared from Sun.SkyDirection) to a screen point and lays out, all in
// additive light:
//   * a soft warm core glow sitting on the sun disc,
//   * a few translucent "ghost" discs strung along the sun -> screen-center axis,
//   * a gentle anamorphic horizontal streak through the sun.
// The whole thing fades as one with MasterIntensity, modulated by how near the sun is to the
// center of view (brightest when you look toward the light) and gated off entirely when the sun
// is behind the camera — which is how a real, subtle flare behaves. Pure overlay: reads the
// camera + Sun.SkyDirection and draws, never touching game state. Wired up by the Hud.
public partial class LensFlare : Control
{
    // One dial for "how subtle" — every element's opacity is scaled by this.
    private const float MasterIntensity = 0.5f;

    // Core glow drawn on the sun disc itself.
    private const float GlowRadius = 85f; // soft core glow radius (px)
    private const float GlowAlpha = 0.35f;

    // Ghosts: discs placed at sun + (center - sun) * t. Factors > 1 land on the far side of
    // center. Radii/alphas/tints chosen so they read as faint, varied lens reflections.
    private static readonly float[] GhostFactors = { 0.25f, 0.5f, 0.85f, 1.3f, 1.7f };
    private static readonly float[] GhostRadii = { 22f, 45f, 16f, 60f, 30f };
    private static readonly float[] GhostAlphas = { 0.07f, 0.05f, 0.09f, 0.04f, 0.06f };

    private const float StreakHalfWidth = 520f; // horizontal anamorphic streak through the sun
    private const float StreakHalfHeight = 4f;
    private const float StreakAlpha = 0.045f;

    // Warm (sun) and cool (lens-coating) tints. Ghosts alternate between them.
    private static readonly Color Warm = new(1f, 0.85f, 0.6f);
    private static readonly Color Cool = new(0.55f, 0.75f, 1f);

    private GradientTexture2D _disc = null!; // radial white -> transparent (glow, ghosts, streak)

    private Camera3D _camera = null!;

    // Static world, for the sun line-of-sight test. A lens flare is light from the source hitting
    // the lens, so when a rock or base blocks the disc the whole flare should vanish — otherwise the
    // additive overlay bleeds sun-glow over solid geometry. Null until Init wires it.
    private WorldRenderer _world = null!;

    // Project through the F3 overview camera while the sector map is open, else the flight chase
    // camera — matching TargetMarkers / VelocityIndicator. Resolved per-access to follow the toggle.
    private Camera3D Cam => SectorOverview.ActiveCamera ?? _camera;

    private Vector2 _sunScreen; // cached projected sun position
    private float _intensity; // cached master intensity for this frame (0 = nothing to draw)

    // Wired up by the Hud (which resolves the chase camera sibling and WorldRenderer).
    public void Init(Camera3D camera, WorldRenderer world)
    {
        _camera = camera;
        _world = world;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game
        // Additive blend so every draw call ADDS light over the scene (mirrors the Sun quad) and
        // never darkens — a lens flare is pure light reflecting inside the lens.
        Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };
    }

    public override void _Ready()
    {
        // A soft round disc: hot center easing to transparent. 8-bit alpha carries only the SHAPE;
        // brightness comes from the additive blend + per-draw modulate. Same recipe as Sun's quad.
        _disc = RadialTexture(
            new Gradient
            {
                Offsets = new[] { 0f, 0.5f, 1f },
                Colors = new[] { Colors.White, new Color(1f, 1f, 1f, 0.25f), new Color(1f, 1f, 1f, 0f) },
            }
        );
    }

    public override void _Process(double delta)
    {
        _intensity = 0f;

        Vector3 skyDir = Sun.SkyDirection;
        if (skyDir == Vector3.Zero) // Sun._Ready hasn't run yet
            return;

        Camera3D cam = Cam;
        if (cam == null)
            return;

        Vector3 sunWorld = cam.GlobalPosition + skyDir * Sun.Distance;
        if (cam.IsPositionBehind(sunWorld)) // sun behind the camera: no flare at all
        {
            QueueRedraw();
            return;
        }

        _sunScreen = cam.UnprojectPosition(sunWorld);

        // Falloff: brightest when the sun sits near the center of view, easing to nothing as it
        // drifts toward (and past) the screen edge — so the flare blooms only when you look at the
        // light and never leaves stray ghosts streaking across an empty sky.
        Vector2 view = GetViewportRect().Size;
        Vector2 center = view * 0.5f;
        float dist = (_sunScreen - center).Length();
        float halfDiag = center.Length();
        float falloff = 1f - Mathf.SmoothStep(0f, halfDiag, dist);

        // Fade the whole flare as the disc slips behind a rock or base — a blocked source casts no
        // flare, so the additive overlay stops bleeding sun-glow through solid geometry.
        float visibility = _world.SunVisibility(cam.GlobalPosition, skyDir);

        _intensity = MasterIntensity * falloff * visibility;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_intensity <= 0.001f)
            return;

        Vector2 center = GetViewportRect().Size * 0.5f;
        Vector2 axis = center - _sunScreen; // sun -> screen center: the ghost layout line

        // Core glow on the sun disc.
        DrawSprite(_disc, _sunScreen, GlowRadius, Tint(Warm, GlowAlpha));

        // Ghosts strung along the axis, alternating warm/cool tints.
        for (int i = 0; i < GhostFactors.Length; i++)
        {
            Vector2 p = _sunScreen + axis * GhostFactors[i];
            Color tint = (i % 2 == 0) ? Warm : Cool;
            DrawSprite(_disc, p, GhostRadii[i], Tint(tint, GhostAlphas[i]));
        }

        // A gentle horizontal streak through the sun.
        DrawStretched(_disc, _sunScreen, StreakHalfWidth, StreakHalfHeight, Tint(Warm, StreakAlpha));
    }

    // Color tinted to the given alpha, then scaled by this frame's master intensity so the whole
    // flare fades as one.
    private Color Tint(Color c, float alpha) => new(c.R, c.G, c.B, alpha * _intensity);

    // A radially-symmetric sprite centered on p with the given pixel radius.
    private void DrawSprite(Texture2D tex, Vector2 p, float radius, Color modulate)
    {
        var rect = new Rect2(p - new Vector2(radius, radius), new Vector2(radius * 2f, radius * 2f));
        DrawTextureRect(tex, rect, false, modulate);
    }

    // The disc sprite stretched into a wide, short ellipse — the anamorphic streak.
    private void DrawStretched(Texture2D tex, Vector2 p, float halfW, float halfH, Color modulate)
    {
        var rect = new Rect2(p - new Vector2(halfW, halfH), new Vector2(halfW * 2f, halfH * 2f));
        DrawTextureRect(tex, rect, false, modulate);
    }

    // A square radial GradientTexture2D (gradient sampled center -> edge). Reuses the same radial
    // recipe the Sun uses for its disc, so the flare's procedural sprites stay self-contained.
    private static GradientTexture2D RadialTexture(Gradient gradient) =>
        new()
        {
            Gradient = gradient,
            Width = 256,
            Height = 256,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
}
