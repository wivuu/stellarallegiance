using Godot;
using SpacetimeDB.Types;

// A one-shot ship-death blast, built procedurally (no assets) on the same unshaded + additive
// + HDR-emission idiom as EngineGlow so the WorldEnvironment glow blooms it. Three layered
// elements give it shape instead of a formless spray:
//   1. a hot core flash that punches bright then vanishes,
//   2. an expanding shockwave ring that races outward and fades,
//   3. a fireball of debris embers that shoot out, decelerate, and burn down.
// Stationary at the death point; self-frees once the longest-lived element is done. Fighters
// get a noticeably bigger blast than Scouts.
public partial class ExplosionEffect : Node3D
{
	private float _classScale = 1f;
	private byte _team;

	// Shared immutable resources — built once, reused by every explosion (no per-blast GC).
	private static readonly GradientTexture2D Dot = RadialDot();
	private static readonly CurveTexture ScaleTex = BurstScaleCurve();

	private const double FlashLife = 0.16;
	private const double RingLife = 0.42;
	private const float FlashEnergy = 7f;
	private const float RingEnergy = 5f;

	private MeshInstance3D? _flash;
	private StandardMaterial3D _flashMat = null!;
	private MeshInstance3D? _ring;
	private StandardMaterial3D _ringMat = null!;
	private double _age;
	private double _totalLife;
	private float _ringMax;

	public static ExplosionEffect Create(ShipClass cls, byte team) => new()
	{
		Name = "Explosion",
		_classScale = cls == ShipClass.Fighter ? 1.7f : 1.0f,
		_team = team,
	};

	public override void _Ready()
	{
		float s = _classScale;
		bool big = s > 1f;
		_ringMax = 11f * s;
		_totalLife = (big ? 1.1 : 0.9) + 0.15;   // outlive the debris particles, then free

		// --- Debris fireball: embers shot outward that decelerate and burn down. ---
		var proc = new ParticleProcessMaterial
		{
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
			EmissionSphereRadius = 0.5f * s,
			Direction = new Vector3(0f, 1f, 0f),
			Spread = 180f,
			InitialVelocityMin = 14f * (big ? 1.4f : 1f),
			InitialVelocityMax = 40f * (big ? 1.4f : 1f),
			Gravity = Vector3.Zero,
			DampingMin = 14f,                    // strong drag: embers fling out then hang & fade
			DampingMax = 22f,
			ScaleMin = 0.8f * s,
			ScaleMax = 2.0f * s,
			ScaleCurve = ScaleTex,
			Color = Colors.White,
			ColorRamp = FireRamp(_team),
		};
		var particles = new GpuParticles3D
		{
			Amount = big ? 90 : 54,
			Lifetime = big ? 1.1 : 0.9,
			OneShot = true,
			Explosiveness = 1.0f,                // all motes at t=0 — a burst, not a trickle
			LocalCoords = true,                  // node is stationary; embers fly out from it
			ProcessMaterial = proc,
			DrawPass1 = new QuadMesh { Size = new Vector2(1.5f * s, 1.5f * s) },
			MaterialOverride = BurstDrawMaterial(),
			Emitting = true,                     // set last, after Amount/ProcessMaterial
		};
		AddChild(particles);

		// --- Core flash: a bright billboarded pop at the instant of death. ---
		_flashMat = FlashMaterial(new Color(1f, 0.95f, 0.75f), FlashEnergy);
		_flash = new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(2.4f * s, 2.4f * s) },
			MaterialOverride = _flashMat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_flash);

		// --- Shockwave ring: a thin torus that races outward and fades. Randomly oriented so
		// successive blasts don't all share a plane (there's no canonical "up" in space). ---
		var rng = new RandomNumberGenerator();
		rng.Randomize();
		_ringMat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BlendMode = BaseMaterial3D.BlendModeEnum.Add,
			AlbedoColor = new Color(1f, 0.85f, 0.55f, 1f),
			EmissionEnabled = true,
			Emission = new Color(1f, 0.8f, 0.45f),
			EmissionEnergyMultiplier = RingEnergy,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // visible from both faces
		};
		_ring = new MeshInstance3D
		{
			// Thin ring of unit radius; the node Scale drives the actual expansion below.
			Mesh = new TorusMesh { InnerRadius = 0.86f, OuterRadius = 1.0f, Rings = 48, RingSegments = 8 },
			MaterialOverride = _ringMat,
			Rotation = new Vector3(rng.RandfRange(0f, Mathf.Pi), rng.RandfRange(0f, Mathf.Pi), rng.RandfRange(0f, Mathf.Pi)),
			Scale = Vector3.One * 0.5f,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_ring);
	}

	public override void _Process(double delta)
	{
		_age += delta;

		if (_flash != null)
		{
			float t = (float)(_age / FlashLife);
			if (t >= 1f) { _flash.QueueFree(); _flash = null; }
			else
			{
				_flash.Scale = Vector3.One * Mathf.Lerp(0.5f, 2.4f * _classScale, EaseOut(t));
				_flashMat.EmissionEnergyMultiplier = FlashEnergy * (1f - t);
				_flashMat.AlbedoColor = _flashMat.AlbedoColor with { A = 1f - t };
			}
		}

		if (_ring != null)
		{
			float t = (float)(_age / RingLife);
			if (t >= 1f) { _ring.QueueFree(); _ring = null; }
			else
			{
				float e = EaseOut(t);
				_ring.Scale = Vector3.One * Mathf.Lerp(0.5f, _ringMax, e);
				_ringMat.EmissionEnergyMultiplier = RingEnergy * (1f - t);
				_ringMat.AlbedoColor = _ringMat.AlbedoColor with { A = 1f - t };
			}
		}

		if (_age >= _totalLife)
			QueueFree();
	}

	// Decelerating ease (fast out of the gate, settling at the end) for the flash + ring sweep.
	private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

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

	// Per-particle scale over life: small spark -> full ember -> shrink out.
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

	// Self-lit additive material for the flash quad and shockwave ring (emission drives bloom).
	private static StandardMaterial3D FlashMaterial(Color color, float energy) => new()
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		BlendMode = BaseMaterial3D.BlendModeEnum.Add,
		AlbedoTexture = Dot,
		AlbedoColor = color,
		EmissionEnabled = true,
		EmissionTexture = Dot,
		Emission = Colors.White,
		EmissionEnergyMultiplier = energy,
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
