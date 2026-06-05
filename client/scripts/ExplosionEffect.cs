using Godot;
using SpacetimeDB.Types;

// A one-shot fiery burst spawned where a ship dies. Procedural (no assets), built on the
// same unshaded + additive + HDR-emission idiom as EngineGlow so the WorldEnvironment glow
// blooms it. Stationary at the death point; self-frees when the burst finishes. Fighters
// get a noticeably bigger blast than Scouts.
public partial class ExplosionEffect : Node3D
{
	private float _classScale = 1f;
	private byte _team;

	// Shared immutable resources — built once, reused by every explosion (no per-blast GC).
	private static readonly GradientTexture2D Dot = RadialDot();
	private static readonly CurveTexture ScaleTex = BurstScaleCurve();

	public static ExplosionEffect Create(ShipClass cls, byte team) => new()
	{
		Name = "Explosion",
		_classScale = cls == ShipClass.Fighter ? 1.7f : 1.0f,
		_team = team,
	};

	public override void _Ready()
	{
		float s = _classScale;

		var proc = new ParticleProcessMaterial
		{
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
			EmissionSphereRadius = 0.6f * s,
			Direction = new Vector3(0f, 1f, 0f),
			Spread = 180f,                       // omnidirectional blast
			InitialVelocityMin = 18f * (s > 1f ? 1.4f : 1f),
			InitialVelocityMax = 42f * (s > 1f ? 1.4f : 1f),
			Gravity = Vector3.Zero,
			DampingMin = 8f,                     // decelerate like a fireball settling
			DampingMax = 12f,
			// Pop in, peak, then shrink to nothing (paired with the larger ScaleMin/Max).
			ScaleMin = 1.0f * s,
			ScaleMax = 1.8f * s,
			ScaleCurve = ScaleTex,
			Color = Colors.White,
			ColorRamp = FireRamp(_team),
		};

		var particles = new GpuParticles3D
		{
			Amount = _classScale > 1f ? 110 : 64,
			Lifetime = _classScale > 1f ? 1.1 : 0.9,
			OneShot = true,
			Explosiveness = 1.0f,                // all motes at t=0 — a burst, not a trickle
			LocalCoords = true,                  // node is stationary; motes fly out from it
			ProcessMaterial = proc,
			DrawPass1 = new QuadMesh { Size = new Vector2(1.6f * s, 1.6f * s) },
			MaterialOverride = BurstDrawMaterial(),
			Emitting = true,                      // set last, after Amount/ProcessMaterial
		};
		// Free the whole effect once the last mote's lifetime elapses.
		particles.Finished += QueueFree;
		AddChild(particles);
	}

	// Fiery mote ramp: HDR white-yellow core -> orange -> deep red -> transparent. The >1
	// RGB values push the burst past the glow threshold so it blooms. Tinted slightly toward
	// the dead ship's team hue so friend/foe deaths still read at a glance.
	private static GradientTexture1D FireRamp(byte team)
	{
		var tint = team == 0 ? new Color(0.6f, 0.8f, 1.1f) : new Color(1.1f, 0.6f, 0.5f);
		var gradient = new Gradient
		{
			Offsets = new[] { 0f, 0.25f, 0.6f, 1f },
			Colors = new[]
			{
				new Color(1.6f, 1.4f, 0.5f, 1f),
				new Color(1.5f, 0.7f, 0.2f, 1f),
				new Color(0.9f * tint.R, 0.2f * tint.G, 0.1f * tint.B, 0.8f),
				new Color(0.4f, 0.1f, 0.05f, 0f),
			},
		};
		return new GradientTexture1D { Gradient = gradient, Width = 64 };
	}

	// Per-particle scale over life: small spark -> full fireball -> shrink out.
	private static CurveTexture BurstScaleCurve()
	{
		var curve = new Curve();
		curve.AddPoint(new Vector2(0f, 0.4f));
		curve.AddPoint(new Vector2(0.3f, 1.0f));
		curve.AddPoint(new Vector2(1f, 0.0f));
		return new CurveTexture { Curve = curve };
	}

	private static StandardMaterial3D BurstDrawMaterial() => new()
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		BlendMode = BaseMaterial3D.BlendModeEnum.Add,
		AlbedoTexture = Dot,
		EmissionEnabled = true,
		EmissionTexture = Dot,
		Emission = Colors.White,
		EmissionEnergyMultiplier = 3.0f,
		BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
		VertexColorUseAsAlbedo = true,   // honour the per-particle ColorRamp alpha
	};

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
