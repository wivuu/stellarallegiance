using Godot;

// A small bright spark at the point a projectile actually hits something. A single
// billboarded, unshaded + additive quad whose HDR emission blooms via the WorldEnvironment
// glow. Hand-animated in _Process (expand + fade) then self-frees — same self-managing idiom
// as ProjectileView/TeamTrail. Spawned by WorldRenderer only on a real hit, never on a shot
// that simply expires downrange.
public partial class HitFlash : Node3D
{
	private const double LifeSec = 0.18;
	private const float StartEnergy = 5f;

	// Shared soft dot — built once, reused by every flash.
	private static readonly GradientTexture2D Dot = RadialDot();

	private MeshInstance3D _quad = null!;
	private StandardMaterial3D _mat = null!;
	private double _age;

	public override void _Ready()
	{
		Name = "HitFlash";
		_mat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BlendMode = BaseMaterial3D.BlendModeEnum.Add,
			AlbedoTexture = Dot,
			AlbedoColor = new Color(1f, 0.95f, 0.8f, 1f),   // hot white-gold core
			EmissionEnabled = true,
			EmissionTexture = Dot,
			Emission = Colors.White,
			EmissionEnergyMultiplier = StartEnergy,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
		};
		_quad = new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(1.2f, 1.2f) },
			MaterialOverride = _mat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_quad);
	}

	public override void _Process(double delta)
	{
		_age += delta;
		float t = (float)(_age / LifeSec);
		if (t >= 1f)
		{
			QueueFree();
			return;
		}
		_quad.Scale = Vector3.One * Mathf.Lerp(0.4f, 1.6f, t);
		_mat.EmissionEnergyMultiplier = StartEnergy * (1f - t);
		_mat.AlbedoColor = _mat.AlbedoColor with { A = 1f - t };
	}

	// Soft round mote: hot centre fading to transparent (same recipe as EngineGlow.RadialDot).
	private static GradientTexture2D RadialDot()
	{
		var gradient = new Gradient
		{
			Offsets = new[] { 0f, 0.5f, 1f },
			Colors = new[]
			{
				new Color(1f, 1f, 1f, 1f),
				new Color(1f, 1f, 1f, 0.4f),
				new Color(1f, 1f, 1f, 0f),
			},
		};
		return new GradientTexture2D
		{
			Gradient = gradient,
			Width = 128,
			Height = 128,
			Fill = GradientTexture2D.FillEnum.Radial,
			FillFrom = new Vector2(0.5f, 0.5f),
			FillTo = new Vector2(0.5f, 0f),
		};
	}
}
