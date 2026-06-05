using Godot;

// Procedural deep-space backdrop: a star field + wispy nebulae painted by a sky
// shader, so it sits at infinity and stays fixed as the pilot flies (only the
// camera's ROTATION moves it). Every visual is driven by a per-sector seed, so a
// sector looks the same for every player (the seed comes only from the sector id)
// yet each sector looks distinctly different — different nebula hues, cloud
// shapes, and star placement. WorldRenderer calls SetSector as the local ship
// warps; we recompute the shader uniforms only when the sector actually changes.
public partial class Starscape : Node3D
{
	// Home/battlefield sector — applied at startup so the pre-spawn overview already
	// shows the right backdrop (mirrors WorldRenderer.HomeSector).
	private const uint HomeSector = 0;

	private ShaderMaterial _mat = null!;
	private Godot.Environment _env = null!;
	private uint _currentSector = uint.MaxValue; // forces the first SetSector to apply

	public override void _Ready()
	{
		var shader = new Shader { Code = ShaderCode };
		_mat = new ShaderMaterial { Shader = shader };

		var sky = new Sky { SkyMaterial = _mat };

		// Swap the flat background colour for the procedural sky. Ambient stays a flat
		// colour source, but SetSector retints it per sector to match the nebula palette.
		var we = GetNode<WorldEnvironment>("../WorldEnvironment");
		_env = we.Environment;
		_env.BackgroundMode = Godot.Environment.BGMode.Sky;
		_env.Sky = sky;
		_env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
		// Critical: with a Sky background this defaults to 1.0, meaning the (bright)
		// procedural sky supplies all ambient and AmbientLightColor/Energy are ignored.
		// Zero it so our per-sector colour + energy below actually drive the ambient.
		_env.AmbientLightSkyContribution = 0f;

		SetSector(HomeSector);
	}

	// Repaint the backdrop for a sector. Deterministic in the sector id alone, so the
	// same sector renders identically for every player. No-op if unchanged.
	public void SetSector(uint sectorId)
	{
		if (sectorId == _currentSector)
			return;
		_currentSector = sectorId;

		var rng = new RandomNumberGenerator { Seed = SeedFor(sectorId) };

		// Sampling offset: shifts every sector to a different region of noise space, so
		// nebula shapes and star placement differ. Kept finite to preserve float precision.
		var offset = new Vector3(
			rng.RandfRange(-500f, 500f),
			rng.RandfRange(-500f, 500f),
			rng.RandfRange(-500f, 500f));

		// Two nebula hues per sector: a base hue and a second one offset around the wheel,
		// blended across the clouds so each sector reads as its own colour palette.
		float hueA = rng.Randf();
		float hueB = Mathf.PosMod(hueA + 0.30f + rng.RandfRange(-0.12f, 0.12f), 1f);
		var colorA = Color.FromHsv(hueA, rng.RandfRange(0.6f, 0.85f), 0.9f);
		var colorB = Color.FromHsv(hueB, rng.RandfRange(0.5f, 0.8f), 0.8f);
		// Kept dim on purpose: the nebula is a faint backdrop, not a light source. Higher
		// values wash the scene out via sky reflections bouncing onto ships/asteroids.
		float intensity = rng.RandfRange(0.05f, 0.1f);

		_mat.SetShaderParameter("seed_offset", offset);
		_mat.SetShaderParameter("nebula_color_a", colorA);
		_mat.SetShaderParameter("nebula_color_b", colorB);
		_mat.SetShaderParameter("nebula_intensity", intensity);

		// Baseline ambient that matches the sector's nebula hue (the mean of its two
		// colours) but lifted toward white so it lights the scene without over-saturating
		// the shadowed sides of ships and asteroids. Energy is well above the old 0.2 so
		// the world no longer reads as dim.
		_env.AmbientLightColor = colorA.Lerp(colorB, 0.5f).Lerp(Colors.White, 0.45f);
		_env.AmbientLightEnergy = 0.2f;
	}

	// Deterministic ulong seed from a sector id (splitmix64 finalizer over a mixed id).
	// Pure function of the id → identical backdrop for every player in that sector.
	private static ulong SeedFor(uint sectorId)
	{
		ulong h = sectorId * 0x9E3779B97F4A7C15UL + 0x1234567UL;
		h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
		h ^= h >> 27; h *= 0x94D049BB133111EBUL;
		h ^= h >> 31;
		return h;
	}

	// Sky shader: nebula = colourised fbm of 3D value noise (biased to wisps); stars =
	// jittered points on a few cell grids of differing scale, brightest layer pushed
	// past 1.0 so it feeds the environment's bloom. All seeded via seed_offset.
	private const string ShaderCode = @"
shader_type sky;

uniform vec3 seed_offset = vec3(0.0);
uniform vec3 nebula_color_a : source_color = vec3(0.5, 0.15, 0.6);
uniform vec3 nebula_color_b : source_color = vec3(0.1, 0.25, 0.7);
uniform float nebula_intensity = 0.01;
uniform vec3 bg_color : source_color = vec3(0.02, 0.02, 0.05);

vec3 hash33(vec3 p) {
	p = vec3(dot(p, vec3(127.1, 311.7, 74.7)),
			 dot(p, vec3(269.5, 183.3, 246.1)),
			 dot(p, vec3(113.5, 271.9, 124.6)));
	return fract(sin(p) * 43758.5453123);
}

float hash31(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float vnoise(vec3 x) {
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash31(i + vec3(0,0,0)), hash31(i + vec3(1,0,0)), f.x),
				   mix(hash31(i + vec3(0,1,0)), hash31(i + vec3(1,1,0)), f.x), f.y),
			   mix(mix(hash31(i + vec3(0,0,1)), hash31(i + vec3(1,0,1)), f.x),
				   mix(hash31(i + vec3(0,1,1)), hash31(i + vec3(1,1,1)), f.x), f.y), f.z);
}

float fbm(vec3 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 5; i++) {
		v += a * vnoise(p);
		p *= 2.02;
		a *= 0.5;
	}
	return v;
}

// Ridged fbm: folds each octave at its midline (1 - |2n-1|) and squares it, turning
// soft noise into sharp crests. Summed across octaves this yields filamentary,
// tendril-like structure rather than the smooth blobs plain fbm gives.
float ridged_fbm(vec3 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 6; i++) {
		float n = 1.0 - abs(2.0 * vnoise(p) - 1.0);
		v += a * n * n;
		p *= 2.1;
		a *= 0.5;
	}
	return v;
}

// One layer of stars: jittered points on a cell grid. Points stay well inside their
// cell so a single-cell test never clips them at boundaries.
float star_layer(vec3 dir, float scale, float density, float off) {
	vec3 p = dir * scale + seed_offset + off;
	vec3 id = floor(p);
	vec3 gv = fract(p) - 0.5;
	vec3 h = hash33(id);
	float present = step(1.0 - density, h.x);
	vec3 jitter = (h - 0.5) * 0.7;
	float d = length(gv - jitter);
	float core = smoothstep(0.07, 0.0, d);
	float bright = 0.3 + 0.7 * h.y;
	return core * bright * present;
}

void sky() {
	vec3 dir = normalize(EYEDIR);

	// Nebula: domain-warped ridged noise for sharp, flowing filaments instead of soft
	// blobs. The warp (q) bends the sample space so structures swirl; ridged_fbm carves
	// the bright tendrils; the threshold + exponent confine them to a few dense banks
	// and keep most of the backdrop open black.
	vec3 np = dir * 2.6 + seed_offset;
	vec3 q = vec3(fbm(np), fbm(np + vec3(3.1, 1.7, 8.2)), fbm(np + vec3(5.3, 9.1, 2.4)));
	float ridges = ridged_fbm(np + 3.5 * q);
	float cloud = pow(clamp(ridges - 0.18, 0.0, 1.0), 2.2);
	float n2 = fbm(np * 1.4 + q);
	vec3 neb = mix(nebula_color_a, nebula_color_b, clamp(n2, 0.0, 1.0));
	vec3 color = bg_color + neb * cloud * nebula_intensity;

	// Stars: three layers at different scales/densities for depth; the near layer is
	// sparser and brighter (can exceed 1.0) so it blooms.
	float s = 0.0;
	s += star_layer(dir, 180.0, 0.06, 0.0);
	s += star_layer(dir, 320.0, 0.04, 11.0);
	s += star_layer(dir, 90.0, 0.05, 27.0) * 1.6;
	vec3 star_tint = mix(vec3(0.8, 0.85, 1.0), vec3(1.0, 0.96, 0.88), clamp(s, 0.0, 1.0));
	color += star_tint * s;

	COLOR = color;
}
";
}
