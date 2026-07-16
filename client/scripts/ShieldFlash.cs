using Godot;

// A brief energy-shield reaction where a bolt strikes a ship whose shield is still up. A full,
// otherwise-transparent SphereMesh centred on the ship and scaled to its radius (the shield
// "bubble"); a radial shader lights up only a hotspot at the impact point and an expanding ring
// that radiates outward across the sphere surface — like an arc of lightning spreading over the
// shield — then fades and self-frees. Additive + unshaded, so everywhere the effect is dark it
// contributes nothing and the sphere stays invisible. Spawned by WorldRenderer only when the
// struck ship's shield is holding (Shield > 0); a hull hit uses the plain HitFlash spark instead.
public partial class ShieldFlash : Node3D
{
    private const double LifeSec = 0.15;
    private const float Energy = 6.5f;    // additive bloom at the lit hotspot / front
    private const float PeakAlpha = 0.05f; // opacity at the brightest point (rest of the sphere is clear)

    // Impact-centred radial shader. `impact_dir` is the unit direction (sphere-local) from the ship
    // centre to the hit; each fragment's angular distance `ang` from it drives two lit features:
    //   * a hot core that flares at the impact point and fades over the first third of the life, and
    //   * a thin bright ring whose radius `front` expands from 0 outward — the "radiating" arc.
    // Everything scales by the global life envelope (1 -> 0), so the sphere is fully transparent
    // except where those features light it. `u_t` is life progress in [0,1].
    //
    // ONE compiled Shader shared by every flash (uniforms live on the per-instance material):
    // compiling per hit re-parsed the source every impact. AssetPreloader warms it at startup.
    private static readonly Shader SharedShader = new() { Code = ShaderCode };

    internal static void WarmShaders() => _ = SharedShader;

    private const string ShaderCode =
        """
        shader_type spatial;
        render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

        uniform vec4 tint : source_color;
        uniform vec3 impact_dir;
        uniform float energy;
        uniform float peak_alpha;
        uniform float u_t;

        const float MAX_FRONT = 2.6;   // radians the ring travels over the life (past the near cap)
        const float RING_W    = 0.05;  // ring thickness (gaussian width, rad^2)
        const float CORE_W    = 0.06;  // hotspot width (gaussian width, rad^2)

        varying vec3 local_pos; // model-space position on the unit sphere

        void vertex() {
            local_pos = VERTEX;
        }

        void fragment() {
            vec3 n = normalize(local_pos);
            float d = clamp(dot(n, normalize(impact_dir)), -1.0, 1.0);
            float ang = acos(d);                          // 0 at the impact point, PI at the antipode

            float env = 1.0 - u_t;                        // global fade-out
            float front = u_t * MAX_FRONT;                // expanding ring radius

            float ring = exp(-(ang - front) * (ang - front) / RING_W);
            // faint filament crackle along the front so it reads as an arc, not a clean shell
            ring *= 0.75 + 0.25 * sin(ang * 42.0);
            float core = exp(-ang * ang / CORE_W) * (1.0 - smoothstep(0.0, 0.35, u_t));

            float intensity = clamp((ring + core) * env, 0.0, 1.0);

            ALBEDO = tint.rgb;
            EMISSION = tint.rgb * energy * intensity;
            ALPHA = peak_alpha * intensity;
        }
        """;

    private readonly Vector3 _impactDir; // unit normal from the ship centre toward the impact point
    private readonly float _radius;      // bubble radius (≈ the ship's visual radius)
    private readonly Color _tint;

    private MeshInstance3D _sphere = null!;
    private ShaderMaterial _mat = null!;
    private double _age;

    public ShieldFlash(Vector3 outward, float radius, Color tint)
    {
        _impactDir = outward.LengthSquared() > 1e-6f ? outward.Normalized() : Vector3.Up;
        _radius = radius;
        _tint = tint;
    }

    public override void _Ready()
    {
        Name = "ShieldFlash";
        _mat = new ShaderMaterial { Shader = SharedShader };
        _mat.SetShaderParameter("tint", _tint);
        _mat.SetShaderParameter("impact_dir", _impactDir);
        _mat.SetShaderParameter("energy", Energy);
        _mat.SetShaderParameter("peak_alpha", PeakAlpha);
        _mat.SetShaderParameter("u_t", 0f);
        _sphere = new MeshInstance3D
        {
            // Full unit sphere (the shield bubble); Scale drives the actual radius. A rotation-free
            // node keeps model space world-aligned so impact_dir (a world-space normal) is valid.
            Mesh = new SphereMesh
            {
                Radius = 1f,
                Height = 2f,
                RadialSegments = 32,
                Rings = 16,
            },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_sphere);
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
        _sphere.Scale = Vector3.One * _radius;
        _mat.SetShaderParameter("u_t", t);
    }
}
