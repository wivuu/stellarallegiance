using Godot;

// Subtle space dust (.PLAN "more fx"). Empty space gives the eye nothing to gauge
// motion against — distant stars barely shift. This scatters a sparse field of dim
// motes in a box around the local ship; because they live in WORLD space (not local
// to the emitter) the ship flies THROUGH them, so near motes parallax past and sell
// the feeling of movement. Built procedurally in C# like the rest of the visuals.
//
// Deliberately subtle: low count, dim, no HDR (kept below the glow threshold so the
// dust never blooms), so it reads as fine grit at speed without cluttering the view.
public partial class DustField : Node3D
{
    // Half-size of the emission box around the ship. Kept fairly tight so motes are
    // near enough to parallax noticeably; the lifetime cycles them as the ship moves.
    private const float BoxHalf = 90f;

    // Motion-gated visibility: the dust exists to gauge MOVEMENT, so at rest it's just
    // clutter hovering in the void. Below MinSpeed it fades fully out; by FullSpeed it's
    // at full strength, ramping linearly between. Values are u/s (sim speed), picked low
    // so gentle drift already shows some grit and only a true standstill hides it.
    private const float MinSpeed = 2f;
    private const float FullSpeed = 18f;

    // Per-second lerp rate toward the speed-derived target, so the field eases in/out over
    // a fraction of a second instead of snapping when the throttle crosses the threshold.
    private const float FadeRate = 3.5f;

    private WorldRenderer _world = null!;
    private GpuParticles3D _particles = null!;
    private StandardMaterial3D _drawMat = null!;
    private float _fade; // current smoothed 0..1 strength, multiplies particle alpha

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");

        var dot = RadialDot();
        var proc = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(BoxHalf, BoxHalf, BoxHalf),
            Direction = Vector3.Zero,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 0f,
            Gravity = Vector3.Zero,
            ScaleMin = 0.12f,
            ScaleMax = 0.3f,
            Color = new Color(0.7f, 0.72f, 0.8f, 0.35f), // dim, faintly cool — no HDR, won't bloom
        };

        _particles = new GpuParticles3D
        {
            Amount = 90,
            Lifetime = 6.0,
            LocalCoords = false, // motes hang in world space so the ship flies through them
            ProcessMaterial = proc,
            DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1f) },
            MaterialOverride = _drawMat = DustDrawMaterial(dot),
            Visible = false,
        };
        AddChild(_particles);
    }

    public override void _Process(double delta)
    {
        var ship = _world.LocalShip;
        if (ship == null)
        {
            // No local ship (pre-spawn overview): nothing to gauge motion against, so hide.
            _particles.Visible = false;
            _fade = 0f;
            return;
        }

        // Ease the field's strength toward what the ship's speed calls for: hidden at rest,
        // full once cruising. The temporal lerp keeps threshold crossings from popping.
        float target = Mathf.Clamp((ship.Speed - MinSpeed) / (FullSpeed - MinSpeed), 0f, 1f);
        _fade = Mathf.MoveToward(_fade, target, FadeRate * (float)delta);

        _particles.Visible = _fade > 0.001f;
        if (!_particles.Visible)
            return;

        // Multiply the per-particle alpha uniformly via the draw material's albedo alpha
        // (VertexColorUseAsAlbedo makes final alpha = vertexAlpha × albedoAlpha), so the
        // whole field dims together as it fades rather than culling individual motes.
        var c = _drawMat.AlbedoColor;
        _drawMat.AlbedoColor = new Color(c.R, c.G, c.B, _fade);

        // Recenter the emission box on the ship each frame, nudged forward along the ship's
        // FACING (which is where the chase camera looks) so dust always fills the view —
        // regardless of which way the ship is actually translating. Biasing along velocity
        // instead pushed the box out of frame when strafing, so the dust appeared to vanish.
        // Existing motes stay put (world coords), so ANY movement parallaxes through them.
        Vector3 fwd = ship.GlobalTransform.Basis * Vector3.Back; // ship-local +Z forward
        _particles.GlobalPosition = ship.GlobalPosition + fwd * (BoxHalf * 0.4f);
    }

    // Discrete round mote: a small opaque core with a near-vertical edge, so it reads as a
    // hard-edged speck rather than a soft fuzzy flake. The core holds full alpha out to
    // ~20% of the radius, then drops to nothing within one antialiasing step — no halo.
    private static GradientTexture2D RadialDot()
    {
        var gradient = new Gradient
        {
            Offsets = [0f, 0.2f, 0.3f, 1f],
            Colors =
            [
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0f),
                new Color(1f, 1f, 1f, 0f),
            ],
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }

    // Distances (world units from camera) over which a mote fades in as it recedes. Below
    // Near it's fully invisible, ramping to fully opaque by Far. This kills the "giant
    // blurry speck" effect: a mote about to pass the camera shrinks to nothing instead of
    // swelling across the screen. Near sits comfortably outside the chase-camera distance.
    private const float FadeNear = 12f;
    private const float FadeFar = 35f;

    // Plain alpha-blended billboards (NOT additive) so the dust stays muted grit rather
    // than glowing — the per-particle colour alpha keeps it faint. DistanceFade culls
    // motes as they approach the camera so they never balloon up close.
    private static StandardMaterial3D DustDrawMaterial(Texture2D dot) =>
        new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = dot,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true, // honour the per-particle Color (incl. its alpha)
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = FadeNear,
            DistanceFadeMaxDistance = FadeFar,
        };
}
