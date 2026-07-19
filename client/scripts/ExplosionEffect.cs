using Godot;
using StellarAllegiance.Net;

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
    private static readonly GradientTexture2D Dot = VfxTextures.RadialDot();
    private static readonly CurveTexture ScaleTex = BurstScaleCurve();

    private const double FlashLife = 0.22;
    private const double RingLife = 0.42;
    private const float FlashEnergy = 14f;
    private const float RingEnergy = 2.6f;

    // Reference blast radius (world units) a warhead's visual scale is normalized against — see
    // CreateBlast.
    private const float SeekerReferenceRadius = 25f;

    private MeshInstance3D? _flash;
    private StandardMaterial3D _flashMat = null!;
    private MeshInstance3D? _ring;
    private StandardMaterial3D _ringMat = null!;
    private double _age;
    private double _totalLife;
    private float _ringMax;

    public static ExplosionEffect Create(ShipClass cls, byte team) =>
        new()
        {
            Name = "Explosion",
            _classScale = cls == ShipClass.Fighter ? 1.7f : 1.0f,
            _team = team,
        };

    // A warhead-scaled detonation blast (missile impact / mine pop): scales the visual by
    // blastRadius/SeekerReferenceRadius so a wider-radius torpedo booms proportionally bigger than
    // a seeker, while a splash-less weapon still shows a small default pop.
    public static ExplosionEffect CreateBlast(float blastRadius, byte team) =>
        new()
        {
            Name = "Explosion",
            // Scale to the warhead: the seeker's SeekerReferenceRadius blast is the 1.0 reference, so a
            // wider-radius torpedo booms proportionally bigger. Clamp low so a 0-radius (splash-less)
            // weapon still shows the small default pop, and cap high so a huge radius doesn't fill the
            // screen.
            _classScale = Mathf.Clamp(blastRadius / SeekerReferenceRadius, 0.6f, 3.0f),
            _team = team,
        };

    public override void _Ready()
    {
        float s = _classScale;
        bool big = s > 1f;
        _ringMax = 11f * s;
        _totalLife = (big ? 0.85 : 0.7) + 0.15; // outlive the debris particles, then free

        // --- Debris fireball: a DENSE, churning ball of fire that puffs up and burns down in
        // place rather than flinging sprites outward (which reads as a firework, not a blast).
        // Low outward velocity keeps the puffs overlapping into a volume; turbulence + strong
        // drag give it roil; big overlapping sprites blend the individual motes away. ---
        var proc = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 2.0f * s, // born across a wider volume, not a point
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 180f,
            InitialVelocityMin = 2f * s, // mostly slow: the ball expands a little, not a lot
            InitialVelocityMax = 12f * s,
            Gravity = Vector3.Zero,
            DampingMin = 10f, // brake quickly so nothing streaks off
            DampingMax = 18f,
            TurbulenceEnabled = true, // organic churn instead of clean radial lines
            TurbulenceNoiseStrength = 6f * s,
            TurbulenceNoiseScale = 1.3f,
            TurbulenceInfluenceMin = 0.4f,
            TurbulenceInfluenceMax = 1.0f,
            ScaleMin = 4.5f * s, // big, heavily overlapping puffs
            ScaleMax = 8.0f * s,
            ScaleCurve = ScaleTex,
            Color = Colors.White,
            ColorRamp = FireRamp(_team),
        };
        var particles = new GpuParticles3D
        {
            Amount = big ? 140 : 90, // dense enough to read as a solid fireball
            Lifetime = big ? 0.85 : 0.7, // punchy: flares up and is gone
            OneShot = true,
            Explosiveness = 1.0f, // all motes at t=0 — a burst, not a trickle
            LocalCoords = true, // node is stationary; the ball churns in place
            ProcessMaterial = proc,
            DrawPass1 = new QuadMesh { Size = new Vector2(1.4f * s, 1.4f * s) },
            MaterialOverride = BurstDrawMaterial(),
            Emitting = true, // set last, after Amount/ProcessMaterial
        };
        AddChild(particles);

        // --- Core flash: a bright billboarded pop at the instant of death. ---
        _flashMat = FlashMaterial(new Color(1f, 0.97f, 0.82f), FlashEnergy);
        _flash = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(4.0f * s, 4.0f * s) },
            MaterialOverride = _flashMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_flash);

        // --- Shockwave ring: a thin torus that races outward and fades. Randomly oriented so
        // successive blasts don't all share a plane (there's no canonical "up" in space). ---
        _ringMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = new Color(0.95f, 0.82f, 0.55f, 0.7f),
            EmissionEnabled = true,
            Emission = new Color(0.95f, 0.78f, 0.45f),
            EmissionEnergyMultiplier = RingEnergy,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // visible from both faces
        };
        _ring = new MeshInstance3D
        {
            // Thin ring of unit radius; the node Scale drives the actual expansion below.
            Mesh = new TorusMesh
            {
                InnerRadius = 0.93f,
                OuterRadius = 1.0f,
                Rings = 48,
                RingSegments = 8,
            },
            MaterialOverride = _ringMat,
            // Same uniform 0..Pi spread as RandomNumberGenerator.RandfRange gave, without a
            // per-blast RNG instance (GD.Randf() is the shared engine RNG the rest of the file
            // already uses for jitter).
            Rotation = new Vector3(GD.Randf() * Mathf.Pi, GD.Randf() * Mathf.Pi, GD.Randf() * Mathf.Pi),
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
            if (t >= 1f)
            {
                _flash.QueueFree();
                _flash = null;
            }
            else
            {
                // Pop to full size almost instantly, then hold bright and fall off late (1-t^2)
                // so the flash reads as a punch rather than a slow linear dim.
                _flash.Scale = Vector3.One * Mathf.Lerp(1.0f, 2.4f * _classScale, EaseOut(t));
                float fade = 1f - t * t;
                _flashMat.EmissionEnergyMultiplier = FlashEnergy * fade;
                _flashMat.AlbedoColor = _flashMat.AlbedoColor with { A = fade };
            }
        }

        if (_ring != null)
        {
            float t = (float)(_age / RingLife);
            if (t >= 1f)
            {
                _ring.QueueFree();
                _ring = null;
            }
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

    // Per-particle scale over life: born large at the blast core, then shrinking as the ember
    // races outward and burns down to nothing — the shrink sells the outward motion.
    private static CurveTexture BurstScaleCurve()
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0f, 1.0f));
        curve.AddPoint(new Vector2(0.5f, 0.6f));
        curve.AddPoint(new Vector2(1f, 0.0f));
        return new CurveTexture { Curve = curve };
    }

    private static StandardMaterial3D BurstDrawMaterial() =>
        new()
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
            VertexColorUseAsAlbedo = true, // honour the per-particle ColorRamp alpha
        };

    // Self-lit additive material for the flash quad and shockwave ring (emission drives bloom).
    private static StandardMaterial3D FlashMaterial(Color color, float energy) =>
        new()
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
}
