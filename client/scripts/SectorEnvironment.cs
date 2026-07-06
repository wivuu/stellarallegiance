using Godot;
using StellarAllegiance.Net;

// Drives the per-sector VISUAL environment the server streams alongside each sector static: the
// directional sun, and the cloud-like DUST. It's a sibling of the WorldEnvironment /
// DirectionalLight3D / Sun / Starscape nodes and is invoked from WorldRenderer.ApplySectorEnv whenever
// the local (or F3-viewed) sector changes. Nebula overrides are owned by Starscape; this node owns the
// sun + dust so those concerns stay out of the sky shader.
//
// Dust clouds render as REAL 3D ENTITIES: per streamed cloud, a MultiMesh of soft billboard "puffs"
// placed along a ridged fractal-noise field (ported from the nebula sky shader) so the cloud clumps
// into wispy filaments. A custom billboard shader adds per-puff fbm noise and reads the per-instance
// colour; each puff's colour is a two-tone blend (nebula-like variation) with the sun shading BAKED in
// (sun-facing side of a cloud bright, far side in shadow — the sector sun is static). The clouds sit
// exactly where the sim attenuates radar/vision (server/Sim/Simulation.Vision.cs DustVisionMult). This
// deliberately does NOT use Godot's volumetric fog (an earlier global-fog + FogVolume attempt tinted
// every ship/asteroid instead of drawing clouds, and was removed).
//
// God rays are a SCREEN-SPACE crepuscular pass (see GodRayShaderCode) on a CanvasLayer below the HUD:
// it smears the bright sun (and sunlit dust) into light shafts and only amplifies already-bright pixels,
// so it never flat-tints geometry the way the volumetric fog did. Driven by `sun.god-rays`.
//
// A null env (or an omitted sub-block) restores that concern to the stock Main.tscn default: the
// static authored sun, no dust, no god rays — so a map with no `environment:` renders exactly as before.
public partial class SectorEnvironment : Node3D
{
    private DirectionalLight3D _light = null!;
    private WorldEnvironment _worldEnv = null!;
    private Sun? _sun;
    private Node3D _dustRoot = null!; // parents the current sector's dust MultiMeshes

    // Captured from Main.tscn at boot so a sector with no sun override restores the authored look.
    private Transform3D _defaultLightXform;
    private Color _defaultLightColor;
    private float _defaultLightEnergy;

    private int _currentSector = -1;
    private bool _appliedEnv; // did the last apply for _currentSector carry a real (non-null) env?

    // Shared billboard look. One QuadMesh + one custom shader back EVERY puff in EVERY cloud; each puff's
    // (varied) colour + opacity ride the MultiMesh per-instance colour, and the shader adds fbm noise
    // mottling so the dust has internal texture instead of smooth discs.
    private QuadMesh _puffMesh = null!;
    private ShaderMaterial _puffMat = null!;

    // God rays: a screen-space crepuscular-ray post pass on a CanvasLayer BELOW the HUD (layer -1) that
    // smears the bright sun (and sunlit dust) into light shafts. It only amplifies already-bright pixels,
    // so it never tints dark geometry (unlike the volumetric-fog attempt). Driven by `sun.god-rays`.
    private CanvasLayer _godRayLayer = null!;
    private ShaderMaterial _godRayMat = null!;
    private float _godRaysStrength;
    private bool _godRaysOn;

    // density → per-puff billboard alpha. MANY densely-packed, low-alpha puffs fuse into a continuous
    // medium (rather than reading as a pile of discrete discs), so each puff stays very translucent and
    // the cloud emerges from their heavy overlap.
    private const float PuffAlphaGain = 0.42f;
    private const float PuffAlphaMax = 0.6f;
    // Puff diameter as a fraction of the cloud radius (further scaled per-puff by the local fractal
    // strength). Kept small so the many puffs read as fine dust rather than a few big poofy balls.
    private const float PuffSizeFrac = 0.6f;

    public override void _Ready()
    {
        _light = GetNode<DirectionalLight3D>("../DirectionalLight3D");
        _worldEnv = GetNode<WorldEnvironment>("../WorldEnvironment");
        _sun = GetNodeOrNull<Sun>("../Sun");

        _defaultLightXform = _light.Transform;
        _defaultLightColor = _light.LightColor;
        _defaultLightEnergy = _light.LightEnergy;

        // This driver never uses volumetric fog. Ensure it's off even if a scene or prior build left it
        // enabled, so nothing can wall/tint the sector.
        var e = _worldEnv.Environment;
        if (e != null)
            e.VolumetricFogEnabled = false;

        _puffMesh = new QuadMesh { Size = new Vector2(1f, 1f) };
        _puffMat = BuildPuffMaterial();
        // Bake the per-puff cloud mottling into a tiling noise texture sampled ONCE per fragment, instead
        // of evaluating a 3-octave fbm (~12 hash/sin ops) live for every one of the many overlapping,
        // overdrawn dust fragments. Seamless so the per-instance UV offset can wrap freely.
        _puffMat.SetShaderParameter("noise_tex", BuildNoiseTexture());

        _dustRoot = new Node3D { Name = "DustClouds" };
        AddChild(_dustRoot);

        _godRayMat = new ShaderMaterial { Shader = new Shader { Code = GodRayShaderCode } };
        _godRayMat.SetShaderParameter("intensity", 0.9f);
        var rayRect = new ColorRect { Material = _godRayMat, Color = Colors.White };
        rayRect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        rayRect.MouseFilter = Control.MouseFilterEnum.Ignore;
        _godRayLayer = new CanvasLayer { Name = "GodRays", Layer = -1, Visible = false };
        _godRayLayer.AddChild(rayRect);
        AddChild(_godRayLayer);
    }

    // Apply (or restore) the environment for `sector`. Env is static per sector, so this is a no-op
    // until the player actually moves to (or overviews) a different sector.
    public void Apply(uint sector, SectorEnv? env)
    {
        // Re-apply when the sector changes OR when a previous same-sector call had NO env and now one
        // arrived. The fog-gated pre-team Welcome can call us with a null env for the home sector before
        // the real sector+env lands; a plain sector-id no-op would then permanently skip the env.
        bool sameSector = _currentSector == (int)sector;
        if (sameSector && (_appliedEnv || env == null))
            return;
        _currentSector = (int)sector;
        _appliedEnv = env != null;

        ApplySun(env);
        BuildDust(env);
        ApplyGodRays(env);
    }

    private void ApplyGodRays(SectorEnv? env)
    {
        _godRaysStrength = env is { HasSun: true } ? env.GodRays : 0f;
        _godRaysOn = _godRaysStrength > 0.01f;
        _godRayLayer.Visible = _godRaysOn;
        if (_godRaysOn)
        {
            var c = _light.LightColor;
            _godRayMat.SetShaderParameter("ray_color", new Vector3(c.R, c.G, c.B));
            _godRayMat.SetShaderParameter("present", 0f);
        }
    }

    // Keep the god-ray shaft anchored on the sun's on-screen position and fade it out as the sun leaves
    // the view (behind camera → off). Only runs while god rays are enabled for the current sector.
    public override void _Process(double delta)
    {
        if (!_godRaysOn)
            return;
        var vp = GetViewport();
        var cam = vp.GetCamera3D();
        if (cam == null)
        {
            _godRayMat.SetShaderParameter("present", 0f);
            return;
        }

        Vector3 toSun = _light.GlobalTransform.Basis.Z.Normalized();
        float facing = (-cam.GlobalTransform.Basis.Z).Dot(toSun); // 1 = looking straight at the sun
        if (facing <= 0.02f)
        {
            _godRayMat.SetShaderParameter("present", 0f);
            return;
        }

        Vector2 screen = cam.UnprojectPosition(cam.GlobalPosition + toSun * 4000f);
        Vector2 size = vp.GetVisibleRect().Size;
        _godRayMat.SetShaderParameter("sun_pos", size.X > 0f ? screen / size : new Vector2(0.5f, 0.5f));
        _godRayMat.SetShaderParameter("present", Mathf.Clamp(facing, 0f, 1f) * _godRaysStrength);
    }

    private void ApplySun(SectorEnv? env)
    {
        if (env is { HasSun: true })
        {
            var dir = new Vector3(env.SunDirX, env.SunDirY, env.SunDirZ);
            if (dir.LengthSquared() > 1e-6f)
            {
                dir = dir.Normalized();
                // A DirectionalLight3D emits along local -Z; Sun.cs reads +Z (basis.Z) as the sky
                // direction. Orient so +Z points AT the sun (dir). Guard the near-vertical up-vector.
                var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.95f ? Vector3.Forward : Vector3.Up;
                _light.LookAt(_light.GlobalPosition - dir, up);
            }
            _light.LightColor = env.HasSunColor
                ? new Color(env.SunColorR, env.SunColorG, env.SunColorB)
                : _defaultLightColor;
            _light.LightEnergy = env.SunEnergy >= 0f ? env.SunEnergy : _defaultLightEnergy;
        }
        else
        {
            _light.Transform = _defaultLightXform;
            _light.LightColor = _defaultLightColor;
            _light.LightEnergy = _defaultLightEnergy;
        }

        // Keep the visible sun disc + lens flare (both read Sun.SkyDirection) aligned with the light.
        _sun?.RefreshFromLight();
    }

    // Rebuild the current sector's dust: every streamed cloud (server-authoritative position/radius/
    // density) becomes a billboard-puff cluster. A null/dust-less env clears the previous sector's dust.
    private void BuildDust(SectorEnv? env)
    {
        foreach (var child in _dustRoot.GetChildren())
            child.QueueFree();

        if (env is not { HasDust: true } || env.DustClouds.Length == 0)
            return;

        // Default is a DARK, dusky tone: this is space dust (unshaded self-lit puffs, so the colour IS
        // the on-screen brightness). Keep it dim so it reads as dust dimming the backdrop, not glowing gas.
        Color dustColor = env.HasDustColor
            ? new Color(env.DustColorR, env.DustColorG, env.DustColorB)
            : new Color(0.22f, 0.2f, 0.26f);

        // Two tones around the authored colour give nebula-like colour variation; each puff blends
        // between them and the shader adds fbm noise on top. The dust is SUN-SHADED: the sector sun is
        // static, so we bake each puff's sun exposure (its offset-from-cloud-centre vs the sun direction)
        // into its brightness — the sun-facing side of a cloud reads bright, the far side falls to shadow.
        Color warm = new Color(dustColor.R * 1.25f, dustColor.G * 1.0f, dustColor.B * 0.8f);
        Color cool = new Color(dustColor.R * 0.8f, dustColor.G * 0.95f, dustColor.B * 1.25f);
        Vector3 toSun = _light.GlobalTransform.Basis.Z.Normalized(); // +Z basis = toward the sun (Sun.cs)

        foreach (var dc in env.DustClouds)
        {
            if (dc.Radius <= 0f)
                continue;
            _dustRoot.AddChild(BuildCloudBillboards(dc, warm, cool, toSun));
        }
    }

    // Build one cloud's billboard cluster as a MultiMeshInstance3D. Rather than a uniform sphere of
    // puffs, the puffs are placed where a RIDGED FRACTAL NOISE field is dense — the same kind of ridged
    // fbm the nebula sky shader uses (Starscape.cs) — so the cloud clumps into wispy filaments/tendrils
    // instead of a round ball. Puff size + opacity track the local noise strength so the fractal
    // silhouette reads; a soft radial falloff still fades the whole cloud out at its rim.
    private MultiMeshInstance3D BuildCloudBillboards(in DustCloud c, Color warm, Color cool, Vector3 toSun)
    {
        var center = new Vector3(c.PosX, c.PosY, c.PosZ);
        var rng = new RandomNumberGenerator { Seed = SeedForCloud(c) };
        // Overdraw (fill-rate) is the dominant GPU cost of the dust: hundreds of big translucent
        // billboards stack up per pixel. Because the puffs are low-alpha and fuse into a continuous
        // medium, ~40% FEWER puffs at correspondingly higher alpha (PuffAlphaGain/Max above) reads almost
        // identically while shading far fewer fragments. Tune this divisor/clamp to trade density vs perf.
        int count = Mathf.Clamp((int)Mathf.Round(c.Radius / 6.5f), 100, 280);

        // Sample the noise at a few units across the cloud (like the nebula's dir*2.6), and offset each
        // cloud into a different region of noise space so no two clouds share a silhouette.
        float freq = 4.0f / c.Radius;
        var noiseOff = new Vector3(rng.RandfRange(-64f, 64f), rng.RandfRange(-64f, 64f), rng.RandfRange(-64f, 64f));
        const float thresh = 0.5f; // ridged-fbm level above which a filament exists

        var xforms = new Transform3D[count];
        var colors = new Color[count];
        for (int i = 0; i < count; i++)
        {
            // Rejection-sample toward the fractal filaments: try a handful of points in the ball and keep
            // the one sitting in the densest noise, so puffs concentrate along the tendrils.
            Vector3 bestLocal = Vector3.Zero;
            float bestN = -1f;
            for (int k = 0; k < 10; k++)
            {
                Vector3 dir;
                do
                {
                    dir = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f));
                } while (dir.LengthSquared() > 1f);
                Vector3 local = dir * c.Radius; // uniform-ish in the ball
                float n = RidgedFbm(local * freq + noiseOff);
                if (n > bestN)
                {
                    bestN = n;
                    bestLocal = local;
                }
                if (n > thresh)
                    break; // good enough — sits on a filament
            }

            float rFrac = bestLocal.Length() / c.Radius; // 0 = centre, 1 = rim
            float strength = Mathf.Clamp((bestN - thresh) * 2.2f + 0.35f, 0.15f, 1f); // filament core → 1
            Vector3 pos = center + bestLocal;

            float size = c.Radius * PuffSizeFrac * (0.5f + 0.7f * strength) * rng.RandfRange(0.8f, 1.15f);
            float alpha = Mathf.Min(c.Density * PuffAlphaGain * rng.RandfRange(0.75f, 1.2f), PuffAlphaMax)
                * strength * (1f - 0.55f * rFrac); // fractal weight × rim fade

            // Sun shading (baked): puffs on the sun-facing side of the cloud are brighter than the far
            // side. Two-tone colour blend + brightness jitter give the per-puff colour variation.
            float sunDot = bestLocal.LengthSquared() > 1e-4f ? bestLocal.Normalized().Dot(toSun) : 0f;
            float exposure = Mathf.Lerp(0.4f, 1.2f, sunDot * 0.5f + 0.5f);
            float bright = rng.RandfRange(0.8f, 1.25f) * exposure;
            Color baseC = warm.Lerp(cool, rng.Randf());

            xforms[i] = new Transform3D(Basis.Identity.Scaled(new Vector3(size, size, size)), pos);
            colors[i] = new Color(baseC.R * bright, baseC.G * bright, baseC.B * bright, alpha);
        }

        var mm = new MultiMesh
        {
            Mesh = _puffMesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, // per-instance colour carries dust tint + per-puff opacity
        };
        mm.InstanceCount = count; // set AFTER the format flags, before writing instances
        for (int i = 0; i < count; i++)
        {
            mm.SetInstanceTransform(i, xforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }

        // A real cloud-sized AABB (centre ± ~2×radius covers puff offsets up to Radius plus the ~0.8R
        // half-size of a rim puff) so Godot frustum-culls clouds that are off-screen or behind the
        // camera — instead of the old effectively-infinite box that forced every sector cloud to draw
        // every frame. Still big enough that a soft cloud never pops when its centre quad leaves frame.
        float ext = c.Radius * 2f;
        return new MultiMeshInstance3D
        {
            Name = "DustBillboards",
            Multimesh = mm,
            MaterialOverride = _puffMat,
            CustomAabb = new Aabb(center - Vector3.One * ext, Vector3.One * (2f * ext)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // Stable per-cloud seed from its (server-streamed, identical-for-all-clients) position, so the puff
    // arrangement is deterministic and matches across clients.
    private static ulong SeedForCloud(in DustCloud c)
    {
        ulong h = 1469598103934665603UL; // FNV-ish over the quantised position
        void Mix(float f)
        {
            h ^= (ulong)(uint)(int)Mathf.Round(f * 4f);
            h *= 1099511628211UL;
        }
        Mix(c.PosX);
        Mix(c.PosY);
        Mix(c.PosZ);
        Mix(c.Radius);
        return h;
    }

    // ---- Ridged fractal noise, ported from the nebula sky shader (Starscape.cs ShaderCode) so the dust
    // clouds share the nebula's filamentary character. Value-noise fbm folded at each octave's midline
    // (1 - |2n-1|) and squared, summed over octaves → sharp tendrils rather than soft blobs.

    private static float Frac(float x) => x - Mathf.Floor(x);

    private static float Hash31(Vector3 p)
    {
        p = p * 0.3183099f + new Vector3(0.1f, 0.1f, 0.1f);
        p = p - p.Floor();
        p *= 17f;
        return Frac(p.X * p.Y * p.Z * (p.X + p.Y + p.Z));
    }

    private static float VNoise(Vector3 x)
    {
        Vector3 i = x.Floor();
        Vector3 f = x - i;
        f = f * f * (new Vector3(3f, 3f, 3f) - 2f * f); // smoothstep weights, componentwise

        float c000 = Hash31(i + new Vector3(0, 0, 0));
        float c100 = Hash31(i + new Vector3(1, 0, 0));
        float c010 = Hash31(i + new Vector3(0, 1, 0));
        float c110 = Hash31(i + new Vector3(1, 1, 0));
        float c001 = Hash31(i + new Vector3(0, 0, 1));
        float c101 = Hash31(i + new Vector3(1, 0, 1));
        float c011 = Hash31(i + new Vector3(0, 1, 1));
        float c111 = Hash31(i + new Vector3(1, 1, 1));

        float x00 = Mathf.Lerp(c000, c100, f.X);
        float x10 = Mathf.Lerp(c010, c110, f.X);
        float x01 = Mathf.Lerp(c001, c101, f.X);
        float x11 = Mathf.Lerp(c011, c111, f.X);
        float y0 = Mathf.Lerp(x00, x10, f.Y);
        float y1 = Mathf.Lerp(x01, x11, f.Y);
        return Mathf.Lerp(y0, y1, f.Z);
    }

    private static float RidgedFbm(Vector3 p)
    {
        float v = 0f,
            a = 0.5f;
        for (int i = 0; i < 5; i++)
        {
            float n = 1f - Mathf.Abs(2f * VNoise(p) - 1f);
            v += a * n * n;
            p *= 2.1f;
            a *= 0.5f;
        }
        return v;
    }

    // One shared custom billboard shader for every dust puff. It reliably reads the MultiMesh per-instance
    // colour (a plain StandardMaterial silently dropped instance RGB), soft-falls-off to the rim, and
    // adds per-puff fbm NOISE so the dust has cloudy internal texture. Unshaded because the sun shading is
    // baked into the instance colour (BuildCloudBillboards) — the sector sun is static.
    private ShaderMaterial BuildPuffMaterial() =>
        new() { Shader = new Shader { Code = PuffShaderCode } };

    // A small tiling fbm noise texture that replaces the shader's live per-fragment fbm. Value-cubic +
    // 3 fractal octaves mirrors the old vnoise-based fbm's soft cloudy character; Seamless lets the
    // per-instance UV offset (v_seed) wrap without a visible seam. Generated once at boot, sampled by
    // every puff of every cloud.
    private static NoiseTexture2D BuildNoiseTexture() =>
        new()
        {
            Width = 512,
            Height = 512,
            Seamless = true,
            GenerateMipmaps = false,
            // Perlin fbm (organic, no blocky cells) with enough octaves that no single low-frequency blob
            // dominates — a dominant blob is what repeats into the visible "waffle" grid when the texture
            // tiles. The shader further hides tiling by rotating the lookup per instance.
            Noise = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
                FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
                FractalOctaves = 5,
                FractalLacunarity = 2.1f,
                Frequency = 0.02f,
            },
        };

    private const string PuffShaderCode =
        @"
shader_type spatial;
render_mode blend_mix, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

uniform sampler2D noise_tex : filter_linear, repeat_enable; // baked fbm mottling (see BuildNoiseTexture)

varying vec4 v_col;
varying vec2 v_seed;

void vertex() {
	v_col = COLOR; // MultiMesh per-instance colour (sun-shaded, colour-varied, + alpha)
	v_seed = vec2(float(INSTANCE_ID) * 1.37, float(INSTANCE_ID) * 0.71);
	// Camera-facing billboard that keeps the per-instance translation + scale (MultiMesh MODEL_MATRIX).
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]) * mat4(
		vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
		vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
		vec4(0.0, 0.0, 0.0, 1.0));
}

void fragment() {
	vec2 d = UV - vec2(0.5);
	float r = length(d) * 2.0;
	float fall = smoothstep(1.0, 0.0, r);          // soft round core → transparent rim
	// Rotate + offset the noise lookup per instance so the tiling texture never lines up across
	// neighbouring puffs into a regular grid / waffle. v_seed is instance-varying.
	float ca = cos(v_seed.x);
	float sa = sin(v_seed.x);
	vec2 nuv = mat2(vec2(ca, -sa), vec2(sa, ca)) * (UV - vec2(0.5)) * 1.7 + v_seed;
	float n = texture(noise_tex, nuv).r;           // per-puff cloudy mottling (baked, 1 fetch)
	float a = v_col.a * fall * clamp(0.45 + 0.85 * n, 0.0, 1.1);
	if (a < 0.003) discard;                        // skip near-transparent rim: kill fill-rate overdraw
	ALBEDO = v_col.rgb * (0.7 + 0.6 * n);          // noise breaks up the flat colour
	ALPHA = a;
}
";

    // Screen-space crepuscular ""god rays"": march from each pixel toward the sun's screen position,
    // accumulating only the BRIGHT pixels along the way (the sun disc + sunlit dust) with distance decay,
    // and add the result. Dark geometry contributes nothing, so it never flat-tints the scene.
    private const string GodRayShaderCode =
        @"
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform vec2 sun_pos = vec2(0.5, 0.5);
uniform float intensity = 0.9;
uniform vec3 ray_color : source_color = vec3(1.0, 0.9, 0.7);
uniform float present = 0.0;

const int SAMPLES = 48;
const float DENSITY = 0.9;
const float DECAY = 0.96;

void fragment() {
	vec2 uv = SCREEN_UV;
	vec3 base = texture(screen_tex, uv).rgb;
	vec3 rays = vec3(0.0);
	if (present > 0.001) {
		vec2 delta = (uv - sun_pos) * (DENSITY / float(SAMPLES));
		vec2 s = uv;
		float illum = 1.0;
		for (int i = 0; i < SAMPLES; i++) {
			s -= delta;
			vec3 smp = texture(screen_tex, s).rgb;
			float lum = max(smp.r, max(smp.g, smp.b));
			rays += smp * smoothstep(0.55, 1.1, lum) * illum; // only bright pixels shaft
			illum *= DECAY;
		}
		rays = rays / float(SAMPLES) * intensity * present;
	}
	COLOR = vec4(base + rays * ray_color, 1.0);
}
";
}
