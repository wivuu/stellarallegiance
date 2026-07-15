using System;
using Godot;

// Client-only base-construction VFX (v37/v38): a spinning, greenish-blue, translucent multi-layer
// glowing sphere that gradually envelops an asteroid while a constructor drone raises a base on it.
// Driven by the MsgConstructorBuilds stream (WorldRenderer.NetUpdateConstructorBuilds) — one BuildSphere
// per active build, keyed by rock id, positioned at the rock and grown via SetEnvelop each frame.
//
// Lifecycle (v38): the sphere is created only once the drone begins SINKING into the rock (the meshes
// intersect). A near-opaque inner CORE fades in (SetCover) to hide the drone as it embeds; the renderer
// also hides the constructor mesh outright once the core covers it. When the build completes and the
// finished base appears via the normal reveal path, BeginFade() dissolves the sphere over ~1.2 s,
// revealing the usable base underneath, then it frees itself.
//
// The look is counter-rotating additive shells (outer wispy fresnel glow + inner banded energy field)
// over an fbm-ish procedural shader, plus a mix-blended core, in the self-lit HDR family of ShieldFlash.
public partial class BuildSphere : Node3D
{
    // Greenish-blue construction energy (HDR, blooms past the glow threshold).
    private static readonly Color Tint = new(0.25f, 1.0f, 0.8f);

    private const float FadeSeconds = 1.2f;

    // Shared noise/fresnel helpers for both the additive shells and the mix-blended core.
    private const string NoiseHelpers =
        """
        uniform vec4 tint : source_color;
        uniform float energy;
        uniform float u_t;      // ever-increasing time (seconds) for animation
        uniform float band_dir; // +1 / -1 so shells counter-rotate

        varying vec3 local_pos;
        varying vec3 world_normal;
        varying vec3 view_dir;

        void vertex() {
            local_pos = VERTEX;
            world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
            view_dir = normalize(CAMERA_POSITION_WORLD - (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);
        }

        float hash(vec3 p) { return fract(sin(dot(p, vec3(17.1, 31.7, 53.3))) * 43758.5453); }
        float vnoise(vec3 p) {
            vec3 i = floor(p); vec3 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float n000 = hash(i + vec3(0,0,0)); float n100 = hash(i + vec3(1,0,0));
            float n010 = hash(i + vec3(0,1,0)); float n110 = hash(i + vec3(1,1,0));
            float n001 = hash(i + vec3(0,0,1)); float n101 = hash(i + vec3(1,0,1));
            float n011 = hash(i + vec3(0,1,1)); float n111 = hash(i + vec3(1,1,1));
            return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
                       mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
        }
        """;

    // Additive glow shell (fresnel rim + rotating energy bands).
    private static readonly string ShellShader =
        "shader_type spatial;\n"
        + "render_mode unshaded, blend_add, cull_disabled, depth_draw_never;\n"
        + "uniform float alpha;\n"
        + NoiseHelpers
        + """

        void fragment() {
            vec3 n = normalize(local_pos);
            float fres = pow(1.0 - clamp(dot(normalize(world_normal), normalize(view_dir)), 0.0, 1.0), 2.0);
            float t = u_t * band_dir;
            float f = vnoise(n * 4.0 + vec3(0.0, t * 0.6, 0.0)) * 0.6
                    + vnoise(n * 8.0 + vec3(t * 0.9, 0.0, 0.0)) * 0.4;
            float bands = 0.5 + 0.5 * sin(n.y * 10.0 + t * 2.0 + f * 6.2831);
            float intensity = clamp(fres * 0.7 + bands * 0.5 * f, 0.0, 1.0);
            ALBEDO = tint.rgb;
            EMISSION = tint.rgb * energy * intensity;
            ALPHA = alpha * clamp(fres + bands * 0.4, 0.0, 1.0);
        }
        """;

    // Mix-blended core: a near-solid inner shell whose opacity (cover) ramps up to hide the drone as it
    // embeds. Front faces only (cull_back), so the near hemisphere occludes the drone behind it.
    private static readonly string CoreShader =
        "shader_type spatial;\n"
        + "render_mode unshaded, blend_mix, cull_back, depth_draw_never;\n"
        + "uniform float cover;\n"
        + NoiseHelpers
        + """

        void fragment() {
            vec3 n = normalize(local_pos);
            float t = u_t * band_dir;
            float f = vnoise(n * 3.5 + vec3(0.0, t * 0.5, 0.0)) * 0.6
                    + vnoise(n * 7.0 + vec3(t * 0.7, 0.0, 0.0)) * 0.4;
            float bands = 0.5 + 0.5 * sin(n.y * 8.0 + t * 1.6 + f * 6.2831);
            ALBEDO = tint.rgb * (0.30 + 0.35 * bands);
            EMISSION = tint.rgb * energy * (0.35 + 0.55 * f);
            ALPHA = cover;
        }
        """;

    // Base per-shell opacity (scaled by the fade envelope each frame).
    private const float OuterAlpha = 0.10f;
    private const float InnerAlpha = 0.16f;

    private MeshInstance3D _outer = null!;
    private MeshInstance3D _inner = null!;
    private MeshInstance3D _core = null!;
    private ShaderMaterial _outerMat = null!;
    private ShaderMaterial _innerMat = null!;
    private ShaderMaterial _coreMat = null!;
    private float _radius;      // current world-unit shell radius (eased toward _target)
    private float _target;      // desired shell radius (rockRadius * envelop)
    private float _cover;       // eased current core opacity
    private float _coverTarget; // desired core opacity (0..1)
    private bool _fading;
    private float _fade;        // 0 = full, 1 = gone
    private double _age;

    public override void _Ready()
    {
        Name = "BuildSphere";
        _outer = MakeShell(ShellShader, radius: 1f, energy: 3.5f, bandDir: 1f, OuterAlpha, "alpha", out _outerMat);
        _inner = MakeShell(ShellShader, radius: 0.82f, energy: 5.0f, bandDir: -1f, InnerAlpha, "alpha", out _innerMat);
        _core = MakeShell(CoreShader, radius: 0.74f, energy: 2.2f, bandDir: 1f, 0f, "cover", out _coreMat);
        AddChild(_core);
        AddChild(_inner);
        AddChild(_outer);
    }

    private MeshInstance3D MakeShell(string code, float radius, float energy, float bandDir, float opacity, string opacityParam, out ShaderMaterial mat)
    {
        mat = new ShaderMaterial { Shader = new Shader { Code = code } };
        mat.SetShaderParameter("tint", Tint);
        mat.SetShaderParameter("energy", energy);
        mat.SetShaderParameter("u_t", 0f);
        mat.SetShaderParameter("band_dir", bandDir);
        mat.SetShaderParameter(opacityParam, opacity);
        return new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = 40, Rings = 22 },
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // Set the target shell radius in world units (rock radius × the current envelop fraction). Eased
    // toward smoothly in _Process so the sphere grows rather than snapping between stream updates.
    public void SetEnvelop(float worldRadius) => _target = MathF.Max(0.001f, worldRadius);

    // Set the core opacity target (0 = invisible core, 1 = fully hides the drone). Eased in _Process.
    public void SetCover(float cover) => _coverTarget = Mathf.Clamp(cover, 0f, 1f);

    // Begin the dissolve: the sphere fades to nothing over FadeSeconds, then frees itself. Idempotent —
    // the finished base has already appeared underneath via the normal reveal path.
    public void BeginFade() => _fading = true;

    public bool IsFading => _fading;

    public override void _Process(double delta)
    {
        _age += delta;
        float step = (float)Mathf.Min(1.0, delta * 3.0);
        _radius = Mathf.Lerp(_radius, _target, step);
        _cover = Mathf.Lerp(_cover, _coverTarget, step);

        if (_fading)
        {
            _fade += (float)delta / FadeSeconds;
            if (_fade >= 1f)
            {
                QueueFree();
                return;
            }
        }
        float env = 1f - Mathf.Clamp(_fade, 0f, 1f); // fade envelope (1 → 0)

        _outer.Scale = _inner.Scale = _core.Scale = Vector3.One * _radius;
        float t = (float)_age;
        _outerMat.SetShaderParameter("u_t", t);
        _innerMat.SetShaderParameter("u_t", t);
        _coreMat.SetShaderParameter("u_t", t);
        _outerMat.SetShaderParameter("alpha", OuterAlpha * env);
        _innerMat.SetShaderParameter("alpha", InnerAlpha * env);
        _coreMat.SetShaderParameter("cover", _cover * env);

        // Slow physical spin of the shells (in addition to the shader band motion).
        _outer.RotateY((float)delta * 0.4f);
        _inner.RotateY((float)delta * -0.6f);
        _core.RotateY((float)delta * 0.3f);
    }
}
