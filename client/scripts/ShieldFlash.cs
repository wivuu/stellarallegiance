using Godot;

// A brief hemispherical shield "bubble" flash where a bolt strikes a ship whose energy shield is
// still up. A single unshaded + additive SphereMesh hemisphere, oriented so its dome faces the
// impact and scaled to the ship's radius, that blooms + fades over a fraction of a second then
// self-frees — the same self-managing idiom as HitFlash, but a mesh shell (not a billboard quad)
// so it reads as a curved shield surface. Spawned by WorldRenderer only when the struck ship's
// shield is holding (Shield > 0); a hull hit uses the plain HitFlash spark instead.
public partial class ShieldFlash : Node3D
{
    private const double LifeSec = 0.22;
    private const float StartEnergy = 4f;

    private readonly Vector3 _outward; // unit normal from the ship centre toward the impact point
    private readonly float _radius; // dome radius (≈ the ship's visual radius)
    private readonly Color _tint;

    private Basis _orient = Basis.Identity; // rotation that aims the dome axis (+Y) along _outward
    private MeshInstance3D _dome = null!;
    private StandardMaterial3D _mat = null!;
    private double _age;

    public ShieldFlash(Vector3 outward, float radius, Color tint)
    {
        _outward = outward.LengthSquared() > 1e-6f ? outward.Normalized() : Vector3.Up;
        _radius = radius;
        _tint = tint;
    }

    public override void _Ready()
    {
        Name = "ShieldFlash";
        _mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // visible from inside and out
            AlbedoColor = _tint,
            EmissionEnabled = true,
            Emission = _tint,
            EmissionEnergyMultiplier = StartEnergy,
        };
        _dome = new MeshInstance3D
        {
            // Unit hemisphere (dome along +Y); Scale drives the actual radius each frame.
            Mesh = new SphereMesh
            {
                Radius = 1f,
                Height = 2f,
                IsHemisphere = true,
                RadialSegments = 24,
                Rings = 8,
            },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dome);

        // Aim the hemisphere's +Y dome axis along the outward impact normal. Guard the degenerate
        // anti-parallel case (outward ≈ -Y), where the from→to quaternion is undefined.
        if (_outward.Dot(Vector3.Up) < -0.9999f)
            _orient = new Basis(Vector3.Right, Mathf.Pi);
        else if (_outward.Dot(Vector3.Up) < 0.9999f)
            _orient = new Basis(new Quaternion(Vector3.Up, _outward));
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
        float s = _radius * Mathf.Lerp(0.9f, 1.15f, t); // slight expand as it pops
        Basis = _orient.Scaled(Vector3.One * s); // set rotation+scale, leave origin (world pos) intact
        _mat.EmissionEnergyMultiplier = StartEnergy * (1f - t);
        _mat.AlbedoColor = _tint with { A = _tint.A * (1f - t) };
    }
}
