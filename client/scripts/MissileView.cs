using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  MissileView.cs — CLIENT IN-FLIGHT GUIDED-MISSILE VISUAL
//
//  One node per live server missile (WorldRenderer._missiles). Mirrors the ShipModelLoader
//  idiom: load the authored `assets/missiles/<ModelName>.glb`, normalize it to a small fixed
//  length so any art scale lands sensibly, and fall back to a procedural capsule when the GLB
//  is missing. A rear-mounted EngineGlow, tuned from the WeaponDef's trail knobs, gives the
//  missile a small booster plume + smoke contrail (throttle pinned so it always burns).
//
//  Motion: missiles stream at the full snapshot rate (20 Hz) but there's no RemoteShip-style
//  interpolation buffer — instead we dead-reckon the last authoritative pos along its velocity
//  each frame and EASE the rendered node toward that, so a corrected snapshot nudges rather
//  than pops. The hull orients along its velocity vector.
// =====================================================================
public partial class MissileView : Node3D
{
    // Longest local axis (world units) a loaded missile GLB is uniform-scaled to — small, so a
    // seeker reads as a dart against the ships it chases whatever scale the art was authored at.
    private const float TargetLength = 1.5f;

    // How fast the rendered node eases toward the dead-reckoned authoritative position. High
    // enough that a correction resolves within a frame or two at 20 Hz without a visible pop.
    private const float EaseRate = 18f;

    // Team, kept for the impact-blast tint when the missile detonates (WorldRenderer looks it up).
    public byte Team { get; private set; }

    // The launching weapon's splash radius, kept so the impact FX (ExplosionEffect.CreateBlast)
    // scales to the warhead — a torpedo booms bigger than a seeker. 0 until the def resolved.
    public float BlastRadius { get; private set; }

    // Dead-reckoned authoritative position (advanced by _vel each frame) and the last known
    // velocity. The node's own Position eases toward _targetPos.
    private Vector3 _targetPos;
    private Vector3 _vel;

    private EngineGlow? _glow;

    // Build the visual from the launching missile-kind WeaponDef (model + trail). A null def
    // (the weapon hasn't streamed yet — shouldn't happen, defs precede any ship) falls back to
    // the capsule + a neutral warm plume so a missile is never invisible.
    public void Initialize(Vector3 pos, Vector3 vel, byte team, WeaponDef? def)
    {
        Team = team;
        BlastRadius = def?.BlastRadius ?? 0f;
        Position = pos;
        _targetPos = pos;
        _vel = vel;

        // Hull: authored GLB (normalized to the fixed dart length) or the procedural fallback.
        Node3D hull = LoadHull(def?.ModelName) ?? BuildPlaceholderMesh();
        AddChild(hull);

        // Rear booster plume tuned from the def's trail knobs. TrailScale sizes the flame, the
        // authored TrailColor tints it (0xRRGGBBAA), TrailLifetime sets how long the contrail
        // lingers behind the missile.
        float trailScale = def is { TrailScale: > 0f } ? def.TrailScale : 0.45f;
        float trailLife = def is { TrailLifetime: > 0f } ? def.TrailLifetime : 0.7f;
        Color trailColor = def != null && def.TrailColor != 0 ? ColorFromRgba(def.TrailColor) : new Color(1f, 0.78f, 0.56f);
        float radius = 0.25f * trailScale;
        _glow = new EngineGlow
        {
            Name = "EngineGlow",
            Nozzles = new[] { new Vector3(0f, 0f, -TargetLength * 0.45f) }, // rear face (−Z)
            NozzleRadius = radius,
            PlumeLength = 2.0f * trailScale,
            LightRange = 6f * trailScale,
            CoreColor = trailColor,
            SmokeLifetime = trailLife,
        };
        AddChild(_glow);
        // Pinned drive: a modest boost keeps the smoke contrail emitting (it's a boost-only tell
        // on ships) while barely washing the authored tint toward the afterburner blue-white.
        _glow.SetThrottle(1f, 0.15f);
    }

    // Latest server truth for this missile: reset the dead-reckoning baseline + velocity. The
    // rendered node then eases toward the freshly-advanced target rather than snapping.
    public void OnAuthoritative(Vector3 pos, Vector3 vel)
    {
        _targetPos = pos;
        _vel = vel;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Dead-reckon the authoritative baseline forward, then ease the rendered node toward it
        // (starts coincident with it on the first frame, so there's no spawn pop).
        _targetPos += _vel * dt;
        Position = Position.Lerp(_targetPos, 1f - Mathf.Exp(-EaseRate * dt));

        // Orient along velocity (local +Z = forward, matching the ship/hardpoint convention).
        if (_vel.LengthSquared() > 1e-4f)
            Basis = BasisFacingZ(_vel);

        // Keep the booster pinned so the plume/contrail never gutters out mid-flight.
        _glow?.SetThrottle(1f, 0.15f);
    }

    // Load `assets/missiles/<name>.glb` normalized to the dart length, or null when it's absent
    // (Initialize then builds the capsule placeholder). Mirrors ShipModelLoader.LoadHull.
    private static Node3D? LoadHull(string? modelName)
    {
        if (string.IsNullOrEmpty(modelName))
            return null;
        Node3D? hull = GlbLoader.Load($"res://assets/missiles/{modelName}.glb");
        if (hull == null)
            return null;
        GlbLoader.NormalizeLongestAxis(hull, TargetLength);
        return hull;
    }

    // Procedural fallback: a slim capsule pointing local +Z (the flight-forward axis), self-lit
    // so it reads even with no scene lighting on it.
    private static MeshInstance3D BuildPlaceholderMesh() =>
        new()
        {
            Mesh = new CapsuleMesh
            {
                Radius = 0.16f,
                Height = TargetLength,
                RadialSegments = 8,
                Rings = 4,
            },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.7f, 0.72f, 0.78f),
                Metallic = 0.6f,
                Roughness = 0.4f,
            },
            RotationDegrees = new Vector3(90f, 0f, 0f), // capsule long axis +Y -> +Z
        };

    // 0xRRGGBBAA -> Color.
    private static Color ColorFromRgba(uint rgba) =>
        new(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    // Orthonormal basis whose local +Z points along `forward` (game-forward), with the up
    // reference swapped when forward is near-vertical so the cross product stays conditioned —
    // the same convention ShipModelLoader.BasisFacingZ uses for hardpoint markers.
    private static Basis BasisFacingZ(Vector3 forward)
    {
        Vector3 z = forward.Normalized();
        Vector3 upRef = Mathf.Abs(z.Dot(Vector3.Up)) > 0.999f ? Vector3.Right : Vector3.Up;
        Vector3 x = upRef.Cross(z).Normalized();
        Vector3 y = z.Cross(x);
        return new Basis(x, y, z);
    }
}
