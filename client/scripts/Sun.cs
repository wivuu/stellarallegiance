using Godot;

// A purely cosmetic sun parked in the sky. It sits in the direction the scene's
// DirectionalLight3D shines FROM, so the visible disc lines up with where the
// light (and shadows) say it should be. It tracks the camera's POSITION but not
// its rotation: anchored at a huge fixed distance, it holds the same angular spot
// in the sky no matter where the pilot flies, and because it slides with the
// camera you can never close the gap to it. A billboarded, additively-blended
// emissive quad reads as a glowing star and feeds the environment's glow/bloom.
public partial class Sun : MeshInstance3D
{
	// Far enough to sit well beyond any sector geometry but inside the camera's
	// far plane (6000). The quad is sized to subtend a believable stylised disc.
	private const float Distance = 4500f;
	private const float Size = 900f;

	private Camera3D _camera = null!;
	private Vector3 _skyDir; // world-space unit vector pointing toward the sun

	public override void _Ready()
	{
		_camera = GetNode<Camera3D>("../Camera3D");

		// A DirectionalLight3D emits along its local -Z, so the light SOURCE lies in
		// the opposite direction (+Z of its basis). Read it once: the light is static.
		var light = GetNode<DirectionalLight3D>("../DirectionalLight3D");
		_skyDir = light.GlobalTransform.Basis.Z.Normalized();

		Mesh = new QuadMesh { Size = new Vector2(Size, Size) };
		MaterialOverride = BuildMaterial();
		CastShadow = ShadowCastingSetting.Off;
	}

	public override void _Process(double delta)
	{
		// Follow the camera's position only — orientation is handled by the
		// billboard, and ignoring camera rotation keeps the sun fixed in the sky.
		GlobalPosition = _camera.GlobalPosition + _skyDir * Distance;
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
