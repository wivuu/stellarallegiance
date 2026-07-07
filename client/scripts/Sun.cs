using Godot;

// A purely cosmetic sun parked in the sky. It sits in the direction the scene's
// DirectionalLight3D shines FROM, so the visible disc lines up with where the
// light (and shadows) say it should be. It tracks the ACTIVE camera's POSITION but
// not its rotation: anchored at a huge fixed distance from whichever camera is
// rendering (chase or the F3 overview), it holds the same angular spot in the sky
// and stays a distant backdrop rather than a physical body you can orbit. A
// billboarded, additively-blended emissive quad reads as a glowing star and feeds
// the environment's glow/bloom.
public partial class Sun : MeshInstance3D
{
    // Far enough to sit well beyond any sector geometry but inside the camera's
    // far plane. The quad is sized to subtend a believable stylised disc.
    public const float Distance = 4500f;
    // Default visible-disc quad width. A sector with no `sun.size` override streams a -1 sentinel and
    // SectorEnvironment falls back to this; maps set a larger/smaller disc via YAML (see SetDiscSize).
    public const float DefaultSize = 900f;

    // World-space unit vector pointing toward the sun, shared so the screen-space
    // lens flare overlay (LensFlare) can anchor its bright core exactly on the disc.
    // Vector3.Zero until _Ready runs — consumers must guard for that.
    public static Vector3 SkyDirection { get; private set; }

    private Vector3 _skyDir; // world-space unit vector pointing toward the sun

    public override void _Ready()
    {
        // A DirectionalLight3D emits along its local -Z, so the light SOURCE lies in
        // the opposite direction (+Z of its basis). Read it once: the light is static.
        var light = GetNode<DirectionalLight3D>("../DirectionalLight3D");
        _skyDir = light.GlobalTransform.Basis.Z.Normalized();
        SkyDirection = _skyDir;

        Mesh = new QuadMesh { Size = new Vector2(DefaultSize, DefaultSize) };
        MaterialOverride = BuildMaterial();
        CastShadow = ShadowCastingSetting.Off;
    }

    // Resize the visible disc to a world-space quad width. SectorEnvironment calls this per-sector from
    // the streamed `sun.size` (or DefaultSize when a sector omits it). The disc sits at a fixed Distance,
    // so a larger width subtends a larger angular disc without moving the sun. No-op until _Ready builds
    // the mesh; the caller re-applies on every sector change so a late _Ready still gets the right size.
    public void SetDiscSize(float size)
    {
        if (size > 0f && Mesh is QuadMesh q)
            q.Size = new Vector2(size, size);
    }

    // Re-read the light direction after the per-sector environment driver (SectorEnvironment) reorients
    // the DirectionalLight3D, so the disc AND the LensFlare (both key off SkyDirection) follow the sun.
    public void RefreshFromLight()
    {
        var light = GetNode<DirectionalLight3D>("../DirectionalLight3D");
        _skyDir = light.GlobalTransform.Basis.Z.Normalized();
        SkyDirection = _skyDir;
    }

    public override void _Process(double delta)
    {
        // Anchor to whichever camera is currently rendering so the sun always sits in
        // the sky backdrop — including the F3 overview, where following the (parked)
        // chase camera would otherwise leave it as a physical quad sitting in the sector.
        var cam = GetViewport().GetCamera3D();
        if (cam == null)
            return;
        GlobalPosition = cam.GlobalPosition + _skyDir * Distance;
    }

    private static StandardMaterial3D BuildMaterial()
    {
        // Radial falloff: a hot white core easing out through a warm corona to fully
        // transparent at the quad's edge. 8-bit texture only carries the SHAPE; the
        // HDR brightness that drives bloom comes from the emission energy below.
        var gradient = new Gradient
        {
            Offsets = new float[] { 0.0f, 0.12f, 0.4f, 1.0f },
            Colors = new[]
            {
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 0.95f, 0.85f, 1f),
                new Color(1f, 0.55f, 0.25f, 0.45f),
                new Color(0f, 0f, 0f, 0f),
            },
        };
        var tex = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 256,
            Height = 256,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };

        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            // Additive blend lets the disc glow over the dark sector without a hard
            // edge, and contributes nothing where the gradient fades to black.
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoTexture = tex,
            EmissionEnabled = true,
            EmissionTexture = tex,
            Emission = new Color(1f, 0.85f, 0.6f),
            EmissionEnergyMultiplier = 5f, // push past the env's HDR glow threshold
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
        };
    }
}
