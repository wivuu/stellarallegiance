using Godot;

namespace StellarAllegiance.Ui;

// Animated "Nebula" backdrop — the warm amber / cool-blue gas-cloud field from the Claude
// Design `Nebula.dc.html` spec. Drop it behind full-screen menu overlays whose backdrop is
// NOT the live 3D space scene (e.g. the server browser), so those screens read as sitting
// inside the nebula instead of on flat Void.
//
// One `canvas_item` shader reproduces the spec: four screen-blended radial gas clouds that
// slowly drift, a top→bottom Void vignette, a faint star-dot grid, and hairline scanlines.
// A soft radial falloff stands in for the CSS `blur()`, so it stays a cheap single-pass fill
// that animates off the shader's own TIME (no per-frame C# / QueueRedraw). `Intensity` (0..1)
// maps to the spec's alpha ramp (0.35 .. 1.0).
public partial class NebulaBackground : ColorRect
{
    private ShaderMaterial _mat = null!;
    private float _intensity = 0.60f;

    // 0 = faint (alpha 0.35), 1 = full (alpha 1.0), matching Nebula.dc.html's `intensity` prop.
    public float Intensity
    {
        get => _intensity;
        set
        {
            _intensity = Mathf.Clamp(value, 0f, 1f);
            _mat?.SetShaderParameter("intensity", _intensity);
        }
    }

    public override void _Ready()
    {
        Color = DesignTokens.Void; // opaque base; the shader paints the clouds over it
        MouseFilter = MouseFilterEnum.Ignore;

        _mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        Material = _mat;
        _mat.SetShaderParameter("intensity", _intensity);
        _mat.SetShaderParameter("void_color", DesignTokens.Void);

        UpdateSize();
        Resized += UpdateSize;
    }

    // The dot grid + scanlines are sized in pixels, so the shader needs the rect's pixel size.
    private void UpdateSize() => _mat?.SetShaderParameter("rect_size", Size);

    private const string ShaderCode =
        @"
shader_type canvas_item;

// alpha = 0.35 + intensity * 0.65   (Nebula.dc.html)
uniform float intensity = 0.6;
uniform vec4 void_color : source_color = vec4(0.0196, 0.0275, 0.0588, 1.0); // #05070F
uniform vec2 rect_size = vec2(1280.0, 720.0);

// Soft radial cloud: 1 at the centre, easing to 0 by `r`. The pow() softens the edge so the
// falloff reads like the spec's blurred radial-gradient rather than a hard disc.
float cloud(vec2 uv, vec2 c, float r) {
	float d = distance(uv, c) / r;
	return pow(clamp(1.0 - d, 0.0, 1.0), 1.6);
}

// screen blend: result = 1 - (1-a)(1-b) — matches the CSS `mix-blend-mode:screen` layers.
vec3 screen_blend(vec3 base, vec3 add) {
	return 1.0 - (1.0 - base) * (1.0 - add);
}

void fragment() {
	vec2 uv = UV;
	float t = TIME;
	float alpha = 0.35 + clamp(intensity, 0.0, 1.0) * 0.65;

	// Four drifting clouds; colours + rough placement from Nebula.dc.html. Centres are in UV
	// space and stretch with the box, exactly like the spec's percentage-sized gradient layers.
	vec2  aC = vec2(0.30, 0.28) + vec2(sin(t * 0.24) * 0.05, cos(t * 0.19) * 0.04);
	vec2  cC = vec2(0.74, 0.74) + vec2(cos(t * 0.15) * 0.06, sin(t * 0.17) * 0.05);
	vec2  bC = vec2(0.52, 0.56) + vec2(sin(t * 0.21 + 1.0) * 0.05, cos(t * 0.23 + 2.0) * 0.05);
	vec2  dC = vec2(0.24, 0.82) + vec2(cos(t * 0.13 + 3.0) * 0.05, sin(t * 0.16 + 1.5) * 0.04);

	vec3 aCol = vec3(0.839, 0.486, 0.204); // amber   214,124,52
	vec3 cCol = vec3(0.737, 0.282, 0.118); // ember   188, 72,30
	vec3 bCol = vec3(0.165, 0.376, 0.620); // blue     42, 96,158
	vec3 dCol = vec3(0.910, 0.659, 0.275); // gold    232,168,70

	float ba = cloud(uv, aC, 0.85) * 0.55;
	float bc = cloud(uv, cC, 0.85) * 0.46;
	float bb = cloud(uv, bC, 0.70) * 0.50;
	float bd = cloud(uv, dC, 0.60) * 0.32;

	vec3 neb = vec3(0.0);
	neb = screen_blend(neb, aCol * ba);
	neb = screen_blend(neb, cCol * bc);
	neb = screen_blend(neb, bCol * bb);
	neb = screen_blend(neb, dCol * bd);

	vec3 col = void_color.rgb + neb * alpha;

	// top→bottom Void vignette (spec: linear-gradient rgba(void,0.35) → rgba(void,0.9)).
	col = mix(col, void_color.rgb, mix(0.35, 0.9, uv.y));

	vec2 px = uv * rect_size;

	// star-dot grid — 1px dots on a 60px lattice, opacity 0.08.
	vec2 cell = (fract(px / 60.0) - 0.5) * 60.0;
	float dot = smoothstep(1.6, 0.4, length(cell)) * 0.08;
	col += vec3(0.863, 0.910, 1.0) * dot; // 220,232,255

	// scanlines — 1 lit px in every 3, faint cool tint (0.035 * 0.5 container opacity).
	float scan = step(fract(px.y / 3.0), 0.34) * 0.0175;
	col += vec3(0.549, 0.784, 1.0) * scan; // 140,200,255

	COLOR = vec4(col, 1.0);
}
";
}
