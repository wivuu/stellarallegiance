using Godot;

// Client-only VFX (v38): a continuous spray of rocky chunks flung off an asteroid's surface while a
// constructor drone grinds its way in (the SINKING phase). Anchored at the drone/contact point and kept
// alive by WorldRenderer.UpdateBuildSpheres for as long as the build row is in phase 1; the instant the
// drone embeds and hides (phase 2) — or the build drops out — the renderer calls Stop(), which cuts the
// emitter and lets the last chunks finish their arc before the node self-frees. No assets: chunky,
// tumbling boxes on a plain lit rock-grey material (deliberately NOT the additive/HDR glow family — this
// reads as physical debris, not energy).
public partial class ConstructorDebris : Node3D
{
    private const float ChunkLifetime = 1.1f;

    private GpuParticles3D _particles = null!;
    private bool _stopping;
    private double _stopAge;

    public override void _Ready()
    {
        Name = "ConstructorDebris";

        var proc = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 2.5f,            // spat from across the grind footprint, not a point
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 180f,                          // fling out every which way off the contact point
            InitialVelocityMin = 6f,
            InitialVelocityMax = 16f,
            Gravity = Vector3.Zero,                 // space — drag alone brings the chunks to rest
            DampingMin = 3f,
            DampingMax = 7f,
            AngularVelocityMin = -260f,             // tumbling rock, not drifting sprites
            AngularVelocityMax = 260f,
            ScaleMin = 0.5f,
            ScaleMax = 1.4f,
            Color = new Color(0.55f, 0.48f, 0.4f),  // dusty rock-grey (fed to vertex colour)
        };
        _particles = new GpuParticles3D
        {
            Amount = 48,
            Lifetime = ChunkLifetime,
            OneShot = false,                        // continuous while the drone sinks
            Explosiveness = 0f,                     // a steady stream, not a single burst
            LocalCoords = false,                    // chunks fly in world space; the node can track the drone
            ProcessMaterial = proc,
            DrawPass1 = ChunkMesh(),
            Emitting = true,                        // set last, after Amount/ProcessMaterial
        };
        AddChild(_particles);
    }

    // Cut emission and hand the node off to self-free: it lingers one chunk-lifetime so the last rocks
    // already in flight finish falling out before it disappears. Idempotent.
    public void Stop()
    {
        if (_stopping)
            return;
        _stopping = true;
        _particles.Emitting = false;
    }

    public bool Stopping => _stopping;

    public override void _Process(double delta)
    {
        if (!_stopping)
            return;
        _stopAge += delta;
        if (_stopAge >= ChunkLifetime)
            QueueFree();
    }

    // Small chunky box with a plain rough grey material; per-particle scale/rotation give varied,
    // tumbling rock. VertexColorUseAsAlbedo lets the process-material Color tint each chunk.
    private static BoxMesh ChunkMesh()
    {
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.5f, 0.44f, 0.37f),
            Roughness = 1f,
            VertexColorUseAsAlbedo = true,
        };
        return new BoxMesh { Size = new Vector3(0.7f, 0.7f, 0.7f), Material = mat };
    }
}
