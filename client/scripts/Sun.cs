using Godot;

// A purely cosmetic sun parked in the sky. It sits in the direction the scene's
// DirectionalLight3D shines FROM, so the visible disc lines up with where the
// light (and shadows) say it should be. It tracks the ACTIVE camera's POSITION but
// not its rotation: anchored at a huge fixed distance from whichever camera is
// rendering (chase or the F3 overview), it holds the same angular spot in the sky
// and stays a distant backdrop rather than a physical body you can orbit. A
// billboarded, additively-blended emissive quad reads as a glowing star and feeds
// the environment's glow/bloom. The disc's COLOUR is derived from the light's
// colour (see ApplyTint), so a sector's streamed sun hue and its visible disc
// always agree.
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

    // Emission of the ORIGINAL fixed warm disc. Kept solely as the luminance anchor: ApplyTint holds
    // every recoloured disc to this linear luminance, so no sun colour can render brighter or dimmer
    // than the classic disc did — only the authored per-sector `sun.energy` changes brightness.
    private static readonly Color BaseEmission = new(1f, 0.85f, 0.6f);

    // Untinted emission energy — pushes the disc past the env's HDR glow threshold so it blooms.
    private const float BaseEnergy = 5f;

    // How far the tint may crank BaseEnergy to hold the disc's luminance. Bounds the pathological case
    // (a near-black or single-channel light colour would otherwise demand a huge boost); every authored
    // sun colour sits far below this.
    private const float MaxEnergyBoost = 8f;

    private Vector3 _skyDir; // world-space unit vector pointing toward the sun
    private StandardMaterial3D? _mat; // disc material; ApplyTint recolours it per sector
    private Gradient? _gradient; // radial disc gradient; ApplyTint rewrites its stop colours per sector

    public override void _Ready()
    {
        // A DirectionalLight3D emits along its local -Z, so the light SOURCE lies in
        // the opposite direction (+Z of its basis). Read it once: the light is static.
        var light = GetNode<DirectionalLight3D>("../DirectionalLight3D");
        _skyDir = light.GlobalTransform.Basis.Z.Normalized();
        SkyDirection = _skyDir;

        Mesh = new QuadMesh { Size = new Vector2(DefaultSize, DefaultSize) };
        _mat = BuildMaterial();
        MaterialOverride = _mat;
        ApplyTint(light.LightColor);
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
    // Also re-reads the light COLOUR so the disc's tint tracks the streamed per-sector sun colour.
    public void RefreshFromLight()
    {
        var light = GetNode<DirectionalLight3D>("../DirectionalLight3D");
        _skyDir = light.GlobalTransform.Basis.Z.Normalized();
        SkyDirection = _skyDir;
        ApplyTint(light.LightColor);
    }

    // How hard the corona leans into the sun colour. The gradient's mid stop gets the colour DEEPENED
    // by this power (in sRGB, before Godot's own sRGB→linear widens it further), mirroring how the
    // original disc exaggerated its corona well past the light's own warmth — a soft peachy light still
    // reads as a proper amber star at the rim, while the core stays hot white.
    private const float CoronaDeepen = 2f;

    // Recolour the disc from the light's colour. The light colour is normalised so its PEAK channel
    // is 1 (hue/saturation only — never the light's dimness), then written into the gradient stops:
    // hot WHITE core, gently tinted inner ring, fully deepened corona. Rewriting the stops (rather than
    // multiply-filtering the old warm texture) lets a cool sun actually read cool — a multiplicative
    // filter can only subtract channels, so it could never turn the baked warm corona blue. Offsets and
    // alphas — the disc's SHAPE — are the fixed classic values. Brightness is pinned: the emission
    // energy is compensated so the tinted disc always matches the classic disc's linear luminance, so
    // no sun colour can dim (or blow out) the disc — only the authored `sun.energy` moves brightness.
    private void ApplyTint(Color lightColor)
    {
        if (_mat == null || _gradient == null)
            return;
        var deep = DeepTintFor(lightColor);
        var inner = Colors.White.Lerp(deep, 0.15f);
        _gradient.Colors = new[]
        {
            new Color(1f, 1f, 1f, 1f),
            new Color(inner.R, inner.G, inner.B, 1f),
            new Color(deep.R, deep.G, deep.B, 0.45f),
            new Color(0f, 0f, 0f, 0f),
        };

        // Mid-depth emission tint (between the white core and the deep corona), luminance-pinned to the
        // classic disc via the energy multiplier.
        var emission = EmissionColorFor(lightColor);
        _mat.Emission = emission;
        _mat.EmissionEnergyMultiplier =
            BaseEnergy * Mathf.Min(MaxEnergyBoost, LinearLuminance(BaseEmission) / Mathf.Max(1e-4f, LinearLuminance(emission)));
    }

    // The disc's peak-normalised, corona-deepened hue for a given light colour. Peak-normalising keeps
    // hue/saturation only (never the light's dimness); the power exaggerates saturation so the colour
    // survives the HDR blow-out.
    private static Color DeepTintFor(Color lightColor)
    {
        float peak = Mathf.Max(lightColor.R, Mathf.Max(lightColor.G, lightColor.B));
        var tint = peak > 1e-4f
            ? new Color(lightColor.R / peak, lightColor.G / peak, lightColor.B / peak)
            : new Color(1f, 1f, 1f);
        return new Color(
            Mathf.Pow(tint.R, CoronaDeepen),
            Mathf.Pow(tint.G, CoronaDeepen),
            Mathf.Pow(tint.B, CoronaDeepen));
    }

    // The disc's emission tint for a given light colour. Public so the sky glare that hugs the disc
    // (Starscape's sun_glow_color) can derive from the SAME hue — the halo and the disc must always
    // agree or the sky shows a glow in one direction and the sun in another.
    public static Color EmissionColorFor(Color lightColor) =>
        Colors.White.Lerp(DeepTintFor(lightColor), 0.6f);

    // Rec.709 luminance of an sRGB-authored colour in LINEAR space — the space the renderer (and the
    // bloom threshold) actually sees, since Color material properties are source_color-converted.
    private static float LinearLuminance(Color srgb) =>
        0.2126f * Mathf.Pow(srgb.R, 2.2f) + 0.7152f * Mathf.Pow(srgb.G, 2.2f) + 0.0722f * Mathf.Pow(srgb.B, 2.2f);

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

    private StandardMaterial3D BuildMaterial()
    {
        // Radial falloff: a hot white core easing out through a tinted corona to fully transparent at
        // the quad's edge (stop colours are per-sector — see ApplyTint; these are the neutral seeds).
        // The offsets/alphas here ARE the disc's shape and never change. 8-bit texture only carries the
        // SHAPE; the HDR brightness that drives bloom comes from the emission energy below.
        _gradient = new Gradient
        {
            Offsets = new float[] { 0.0f, 0.12f, 0.4f, 1.0f },
            Colors = new[]
            {
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0.45f),
                new Color(0f, 0f, 0f, 0f),
            },
        };
        var tex = new GradientTexture2D
        {
            Gradient = _gradient,
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
            Emission = BaseEmission,
            EmissionEnergyMultiplier = BaseEnergy,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
        };
    }
}
