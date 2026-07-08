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
    private Starscape? _starscape; // sky-shader sun glare must re-aim whenever ApplySun moves the light
    private Node3D _dustRoot = null!; // parents the current sector's dust MultiMeshes

    // Captured from Main.tscn at boot so a sector with no sun override restores the authored look.
    private Transform3D _defaultLightXform;
    private Color _defaultLightColor;
    private float _defaultLightEnergy;
    private float _defaultAmbientEnergy; // WorldEnvironment ambient-light energy at boot (sun.ambient restores to this)

    private int _currentSector = -1;
    private bool _appliedEnv; // did the last apply for _currentSector carry a real (non-null) env?
    private SectorEnv? _currentEnv; // env last applied for _currentSector; SyncShafts reads its sun each pass

    // True when the current sector's env casts dust shadows. A shadow volume only reads as a darkened
    // SHAFT because it multiply-blends into dust — with no dust there is nothing to darken, so a sector
    // that omits `dust:` casts NONE (needs both a sun for the downsun axis AND actual dust clouds).
    // WorldRenderer gates its per-frame camera-distance occluder re-scan on this so a sunless or
    // dustless sector does no work.
    public bool CastsSectorShadows => _currentEnv is { HasSun: true } && HasSectorDust;

    // True when the current sector actually has dust to darken: a streamed dust block with ≥1 seeded
    // cloud (a `dust:` with amount 0 streams the block but zero clouds → still no shafts).
    private bool HasSectorDust => _currentEnv is { HasDust: true } e && e.DustClouds.Length > 0;

    // The current sector's shadow occluders, handed in by WorldRenderer.ApplySectorEnv: the biggest few
    // rocks/bases NEAR the camera, each as (its scene Node3D, its LOCAL-frame hull vertices). SyncShafts
    // bakes a shader-extrudable shadow-volume mesh per occluder and PARENTS it to the node so it tumbles
    // with the rock; the vertex shader extrudes it downsun every frame (ShadowVolume + ShaftShaderCode).
    // The set is distance-based and re-handed as the camera moves; empty = no shafts.
    private System.Collections.Generic.IReadOnlyList<(Node3D Node, Vector3[] LocalVerts)> _occluders =
        System.Array.Empty<(Node3D, Vector3[])>();

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

    // Asteroid / base shadow shafts as SPIN-TRACKING shadow VOLUMES: each occluder's convex hull is baked
    // once in its LOCAL frame (ShadowVolume.Build) and parented to the occluder node, so it tumbles with
    // the rock; the vertex shader extrudes the away-from-sun faces downsun every frame, so the dark shaft
    // in the dust follows the rock's real (spinning) silhouette. The sun axis is static per sector, so
    // only the per-occluder MODEL transform (free, inherited) changes frame to frame.
    private ShaderMaterial _shaftMat = null!;
    // The live shadow-volume MeshInstance3Ds, KEYED by the occluder node each parents under. The occluder
    // set is DISTANCE-BASED (the rocks near the camera; see WorldRenderer.GatherShadowOccluders) and
    // re-evaluated as the camera moves, so SyncShafts only builds/frees the delta — a rock already casting
    // keeps its baked volume. Held here so a sector change or teardown can free them even though they live
    // under WorldRenderer's rocks.
    private readonly System.Collections.Generic.Dictionary<Node3D, MeshInstance3D> _shadowByNode = new();
    private readonly System.Collections.Generic.HashSet<Node3D> _shadowWantScratch = new();
    private readonly System.Collections.Generic.List<Node3D> _shadowDropScratch = new();
    private const float ShaftLength = 3500f; // how far downsun a shaft reaches (multiply fades it in dust)
    private const float ShaftDarkness = 0.98f; // multiply factor at the shaft core — lower = starker/darker
    // Distance (m) downsun over which the shadow fades to nothing: full-dark at the rock, gone a few
    // hundred metres out, so a rock only shades the dust close to it rather than casting a 3.5 km streak.
    private const float ShaftFadeDistance = 750f;

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
        _starscape = GetNodeOrNull<Starscape>("../Starscape");

        _defaultLightXform = _light.Transform;
        _defaultLightColor = _light.LightColor;
        _defaultLightEnergy = _light.LightEnergy;

        // This driver never uses volumetric fog. Ensure it's off even if a scene or prior build left it
        // enabled, so nothing can wall/tint the sector.
        var e = _worldEnv.Environment;
        if (e != null)
        {
            e.VolumetricFogEnabled = false;
            _defaultAmbientEnergy = e.AmbientLightEnergy; // restore point for a sector with no ambient override
        }

        _puffMesh = new QuadMesh { Size = new Vector2(1f, 1f) };
        _puffMat = BuildPuffMaterial();
        // Bake the per-puff cloud mottling into a tiling noise texture sampled ONCE per fragment, instead
        // of evaluating a 3-octave fbm (~12 hash/sin ops) live for every one of the many overlapping,
        // overdrawn dust fragments. Seamless so the per-instance UV offset can wrap freely.
        _puffMat.SetShaderParameter("noise_tex", BuildNoiseTexture());

        _dustRoot = new Node3D { Name = "DustClouds" };
        AddChild(_dustRoot);

        _shaftMat = new ShaderMaterial { Shader = new Shader { Code = ShaftShaderCode } };
        _shaftMat.SetShaderParameter("dark", ShaftDarkness);
        _shaftMat.SetShaderParameter("shaft_len", ShaftLength);
        _shaftMat.SetShaderParameter("fade_dist", ShaftFadeDistance);
        // Draw the shafts AFTER the (default-priority) dust puffs so the multiply lands on top of the
        // already-drawn dust and actually darkens it, rather than being covered by later puffs.
        _shaftMat.RenderPriority = 2;

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
    public void Apply(
        uint sector,
        SectorEnv? env,
        System.Collections.Generic.IReadOnlyList<(Node3D Node, Vector3[] LocalVerts)>? occluders = null)
    {
        // Re-apply when the sector changes OR when a previous same-sector call had NO env and now one
        // arrived. The fog-gated pre-team Welcome can call us with a null env for the home sector before
        // the real sector+env lands; a plain sector-id no-op would then permanently skip the env.
        bool sameSector = _currentSector == (int)sector;
        if (sameSector && (_appliedEnv || env == null))
            return;
        _currentSector = (int)sector;
        _appliedEnv = env != null;
        _currentEnv = env;
        _occluders = occluders ?? System.Array.Empty<(Node3D, Vector3[])>();

        ApplySun(env);
        BuildDust(env);
        SyncShafts();
        ApplyGodRays(env);
    }

    // Refresh ONLY the shadow-volume set for the CURRENT sector (sun + dust are static per sector). Called
    // as the camera moves so the distance-based occluder selection tracks it: SyncShafts builds volumes for
    // newly-near occluders and frees those that fell out of range, leaving unchanged ones untouched.
    public void UpdateOccluders(
        System.Collections.Generic.IReadOnlyList<(Node3D Node, Vector3[] LocalVerts)> occluders)
    {
        _occluders = occluders;
        SyncShafts();
    }

    // Drop the sector-env cache so the NEXT Apply rebuilds even for the same sector id. Called on a world
    // teardown (WorldRenderer.Reset): the shadow volumes now PARENT to rock nodes that Reset frees, so the
    // fresh Welcome must rebuild them once it re-adds the rocks — the same-sector dedup would otherwise
    // skip that re-apply and leave the reconnected sector with no shafts.
    public void Invalidate()
    {
        _currentSector = -1;
        _appliedEnv = false;
        _currentEnv = null;
        // The volumes parent to rock nodes the teardown frees; drop our refs (freeing the parents frees them)
        // so the next Apply rebuilds from an empty set instead of diffing against dead nodes.
        _shadowByNode.Clear();
    }

    // "How buried in dust is this world-space point" for the dust-ambient audio (DustField):
    // the max over the current sector's clouds of density × a soft radial falloff (1 at a
    // cloud's centre → 0 at its rim). Mirrors the server's DustCoverageAt (Simulation.Vision.cs)
    // but with a smooth edge, so the audio swells toward a cloud's core rather than switching on
    // at the boundary. Cloud centres are placed in world space (BuildCloudBillboards), matching
    // the ship's GlobalPosition. 0 when the current sector streamed no dust. Clamped to [0,1].
    public float DustDensityAt(Vector3 worldPos)
    {
        if (_currentEnv is not { HasDust: true } env || env.DustClouds.Length == 0)
            return 0f;
        float best = 0f;
        foreach (var c in env.DustClouds)
        {
            if (c.Radius <= 0f)
                continue;
            float d = worldPos.DistanceTo(new Vector3(c.PosX, c.PosY, c.PosZ));
            if (d >= c.Radius)
                continue;
            float cov = c.Density * (1f - d / c.Radius);
            if (cov > best)
                best = cov;
        }
        return Mathf.Min(best, 1f);
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
        // Aspect corrects the sun-proximity mask to a true circle in screen space (UVs are 0..1 on both
        // axes, so a raw UV distance is stretched); the mask restricts the ray SOURCE to the sun disc/halo
        // so bright engine glow elsewhere on screen doesn't seed its own shafts.
        _godRayMat.SetShaderParameter("aspect", size.Y > 0f ? size.X / size.Y : 1.777f);
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
            ApplyAmbient(env.SunAmbient >= 0f ? env.SunAmbient : _defaultAmbientEnergy);
            _sun?.SetDiscSize(env.SunSize >= 0f ? env.SunSize : Sun.DefaultSize);
        }
        else
        {
            _light.Transform = _defaultLightXform;
            _light.LightColor = _defaultLightColor;
            _light.LightEnergy = _defaultLightEnergy;
            ApplyAmbient(_defaultAmbientEnergy);
            _sun?.SetDiscSize(Sun.DefaultSize);
        }

        // Keep the visible sun disc + lens flare (both read Sun.SkyDirection) aligned with the light,
        // and re-aim the sky shader's sun-glare halo (a uniform, not geometry) at the same spot — a
        // stale halo leaves a big glow in one part of the sky with the disc somewhere else entirely.
        _sun?.RefreshFromLight();
        _starscape?.RefreshSun();
    }

    // Set the sector's ambient (fill) light energy on the WorldEnvironment — the flat base illumination
    // every surface gets regardless of the directional sun, so a sector can read brighter/darker overall.
    private void ApplyAmbient(float energy)
    {
        var e = _worldEnv.Environment;
        if (e != null)
            e.AmbientLightEnergy = energy;
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
        // between them and the shader adds fbm noise on top. The tones derive from the SECTOR'S SUN
        // COLOUR (ApplySun ran first, so _light.LightColor is the resolved streamed-or-default sun): the
        // warm tone leans into the sun's own hue (an amber sun ambers its dust) and the cool tone drifts
        // only slightly cool — a strong blue drift would grey warm dust into pastel. The dust is
        // SUN-SHADED: the sector sun is static, so we bake each puff's sun exposure (its
        // offset-from-cloud-centre vs the sun direction) into its brightness — the sun-facing side of a
        // cloud reads bright, the far side falls to shadow.
        Color sunC = _light.LightColor;
        Color warm = new Color(
            dustColor.R * (0.6f + 0.8f * sunC.R),
            dustColor.G * (0.55f + 0.7f * sunC.G),
            dustColor.B * (0.5f + 0.6f * sunC.B));
        Color cool = new Color(dustColor.R * 0.72f, dustColor.G * 0.78f, dustColor.B * 0.95f);
        Vector3 toSun = _light.GlobalTransform.Basis.Z.Normalized(); // +Z basis = toward the sun (Sun.cs)

        // Forward-scatter uniforms: puffs IGNITE with the sun colour when the camera looks sunward
        // through them (see PuffShaderCode), scaled by the authored god-ray strength so a rays-off
        // sector keeps quiet dust; the floor keeps sunlit dust reading lit even at god-rays 0.
        _puffMat.SetShaderParameter("sun_dir", toSun);
        _puffMat.SetShaderParameter("sun_color", new Vector3(sunC.R, sunC.G, sunC.B));
        _puffMat.SetShaderParameter(
            "backlight",
            env is { HasSun: true } ? 0.5f * env.GodRays + 0.15f : 0f);

        // Opacity scales how opaque the dust RENDERS (per-puff alpha), matching the radar attenuation the
        // server derives from the same knob — so low-opacity dust reads as a faint see-through haze and
        // high-opacity as a solid veil, independent of the visual `amount` (cloud coverage/count).
        float opacity = Mathf.Clamp(env.DustOpacity, 0f, 1f);

        foreach (var dc in env.DustClouds)
        {
            if (dc.Radius <= 0f)
                continue;
            _dustRoot.AddChild(BuildCloudBillboards(dc, warm, cool, toSun, opacity));
        }
    }

    // Sync the current sector's spin-tracking shadow volumes to the selected occluder set. Each occluder
    // gets a convex-hull mesh baked in its LOCAL frame (ShadowVolume.Build), parented to its scene node so
    // it tumbles with the rock, and drawn with the shared shaft material whose vertex shader extrudes it
    // downsun each frame — so the dark shaft in the dust follows the rock's real (spinning) silhouette. The
    // set is DISTANCE-BASED and re-evaluated as the camera moves (WorldRenderer.GatherShadowOccluders), so
    // this builds ONLY the volumes for newly-present occluders and frees those that dropped out; a rock
    // already casting keeps its baked volume (ShadowVolume.Build never re-runs for it). Only the static sun
    // axis is a shader uniform. Everything is cleared when a sector has no sun or no occluders.
    private void SyncShafts()
    {
        // No shafts without a sun (no downsun axis), without dust to darken, or without occluders.
        if (_currentEnv is not { HasSun: true } || !HasSectorDust || _occluders.Count == 0)
        {
            ClearShafts();
            return;
        }

        // Downsun = away from the sun (Sun.cs treats +Z basis as toward the sun), matching the direction
        // real shadows fall. ApplySun oriented _light for this sector; the axis is static per sector but may
        // have changed with the sector, so refresh it here before extruding.
        Vector3 axis = _light.GlobalTransform.Basis.Z;
        if (axis.LengthSquared() < 1e-6f)
        {
            ClearShafts();
            return;
        }
        _shaftMat.SetShaderParameter("downsun", -axis.Normalized());

        // Build a volume for every wanted occluder that doesn't already have one; record the wanted set.
        _shadowWantScratch.Clear();
        foreach (var (node, localVerts) in _occluders)
        {
            if (!GodotObject.IsInstanceValid(node))
                continue;
            _shadowWantScratch.Add(node);
            if (_shadowByNode.ContainsKey(node))
                continue; // already casting — leave its baked volume in place

            var mesh = ShadowVolume.Build(localVerts);
            if (mesh == null)
                continue;

            var mi = new MeshInstance3D
            {
                Name = "ShadowVolume",
                Mesh = mesh,
                MaterialOverride = _shaftMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // The shader extrudes verts far downsun, well outside the mesh's own (hull-hugging) AABB;
                // a loose CustomAabb keeps the shaft submitted instead of being frustum-culled when the
                // rock itself is off-screen.
                CustomAabb = new Aabb(Vector3.One * -1e5f, Vector3.One * 2e5f),
            };
            node.AddChild(mi); // inherits the rock's spin, position, and scale for free
            _shadowByNode[node] = mi;
        }

        // Free volumes whose occluder fell out of the selected set (or was freed under us).
        _shadowDropScratch.Clear();
        foreach (var (node, _) in _shadowByNode)
            if (!_shadowWantScratch.Contains(node) || !GodotObject.IsInstanceValid(node))
                _shadowDropScratch.Add(node);
        foreach (var node in _shadowDropScratch)
        {
            if (_shadowByNode.TryGetValue(node, out var mi) && GodotObject.IsInstanceValid(mi))
                mi.QueueFree();
            _shadowByNode.Remove(node);
        }
    }

    // Free every live shadow volume and forget it (sunless sector, empty occluder set, or degenerate sun).
    private void ClearShafts()
    {
        foreach (var mi in _shadowByNode.Values)
            if (GodotObject.IsInstanceValid(mi))
                mi.QueueFree();
        _shadowByNode.Clear();
    }

    // Build one cloud's billboard cluster as a MultiMeshInstance3D. Rather than a uniform sphere of
    // puffs, the puffs are placed where a RIDGED FRACTAL NOISE field is dense — the same kind of ridged
    // fbm the nebula sky shader uses (Starscape.cs) — so the cloud clumps into wispy filaments/tendrils
    // instead of a round ball. Puff size + opacity track the local noise strength so the fractal
    // silhouette reads; a soft radial falloff still fades the whole cloud out at its rim.
    private MultiMeshInstance3D BuildCloudBillboards(in DustCloud c, Color warm, Color cool, Vector3 toSun, float opacity)
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

        // Break the round-ball silhouette: give each cloud a random tilt + anisotropic (ellipsoidal)
        // stretch so no two clouds share an outline, and below let filament-dense directions REACH
        // further out than sparse ones so the rim frays into tendrils instead of a clean sphere.
        var cloudBasis = new Basis(RandomUnitAxis(rng), rng.RandfRange(0f, Mathf.Tau));
        var axisScale = new Vector3(
            rng.RandfRange(0.6f, 1.45f),
            rng.RandfRange(0.5f, 1.05f), // squash Y a touch — dust reads as a drifting sheet, not a ball
            rng.RandfRange(0.6f, 1.45f));

        var xforms = new Transform3D[count];
        var colors = new Color[count];
        for (int i = 0; i < count; i++)
        {
            // Rejection-sample toward the fractal filaments: try a handful of points in the ball and keep
            // the one sitting in the densest noise, so puffs concentrate along the tendrils.
            Vector3 bestDir = Vector3.Zero;
            float bestN = -1f;
            for (int k = 0; k < 10; k++)
            {
                Vector3 dir;
                do
                {
                    dir = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f));
                } while (dir.LengthSquared() > 1f);
                float n = RidgedFbm(dir * (freq * c.Radius) + noiseOff); // freq*Radius = 4 → noise in a 4-unit ball
                if (n > bestN)
                {
                    bestN = n;
                    bestDir = dir;
                }
                if (n > thresh)
                    break; // good enough — sits on a filament
            }

            float strength = Mathf.Clamp((bestN - thresh) * 2.2f + 0.35f, 0.15f, 1f); // filament core → 1
            // Lobed boundary: dense filament directions push out past the nominal radius, sparse ones sit
            // in tighter, so the cloud's edge is ragged. Then tilt + squash it into the per-cloud ellipsoid.
            float reach = 0.5f + 0.85f * Mathf.Clamp(bestN - thresh + 0.4f, 0f, 1f);
            Vector3 shaped = cloudBasis * ((bestDir * reach) * axisScale) * c.Radius;
            float rFrac = Mathf.Min(shaped.Length() / c.Radius, 1.4f); // 0 = centre, ~1 = rim (clamped for the fade)
            Vector3 pos = center + shaped;

            float size = c.Radius * PuffSizeFrac * (0.5f + 0.7f * strength) * rng.RandfRange(0.8f, 1.15f);
            float alpha = Mathf.Max(0f,
                Mathf.Min(c.Density * PuffAlphaGain * rng.RandfRange(0.75f, 1.2f), PuffAlphaMax)
                * strength * (1f - 0.5f * rFrac) * opacity); // fractal weight × rim fade × opacity (see BuildDust)

            // Sun shading (baked): puffs on the sun-facing side of the cloud are brighter than the far
            // side. Two-tone colour blend + brightness jitter give the per-puff colour variation.
            float sunDot = shaped.LengthSquared() > 1e-4f ? shaped.Normalized().Dot(toSun) : 0f;
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

        // A real cloud-sized AABB so Godot frustum-culls clouds that are off-screen or behind the camera
        // — instead of the old effectively-infinite box that forced every sector cloud to draw every
        // frame. Sized for the WORST-CASE puff centre: a lobed rim reach (~1.35) × the max ellipsoid
        // axis (~1.45) ≈ 1.9R, plus a rim puff's ~0.5R half-size — so ±2.6R never pops a soft cloud.
        float ext = c.Radius * 2.6f;
        return new MultiMeshInstance3D
        {
            Name = "DustBillboards",
            Multimesh = mm,
            MaterialOverride = _puffMat,
            CustomAabb = new Aabb(center - Vector3.One * ext, Vector3.One * (2f * ext)),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // A uniformly-random unit vector (rejection-sampled in the ball), used as the tilt axis that gives
    // each cloud its own ellipsoid orientation. Drawn from the per-cloud rng so it's deterministic.
    private static Vector3 RandomUnitAxis(RandomNumberGenerator rng)
    {
        Vector3 v;
        float l2;
        do
        {
            v = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f));
            l2 = v.LengthSquared();
        } while (l2 < 1e-4f || l2 > 1f);
        return v / Mathf.Sqrt(l2);
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
    // colour (a plain StandardMaterial silently dropped instance RGB), soft-falls-off to the rim, adds
    // per-puff fbm NOISE so the dust has cloudy internal texture, and adds a view-dependent
    // FORWARD-SCATTER term (per-instance phase lobe) so dust ignites with the sun colour when the camera
    // looks sunward through it. Unshaded because the static sun shading is baked into the instance colour
    // (BuildCloudBillboards) — only the view-dependent scatter is live.
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
uniform vec3 sun_dir = vec3(0.0, 0.0, 1.0);   // world unit vector toward the sun
uniform vec3 sun_color : source_color = vec3(1.0, 0.85, 0.6);
uniform float backlight = 0.0;                 // forward-scatter gain (0.5*god-rays + floor; 0 = off)

varying vec4 v_col;
varying vec2 v_seed;
varying float v_phase; // forward-scatter lobe: 1 when the camera looks straight sunward through the puff

void vertex() {
	v_col = COLOR; // MultiMesh per-instance colour (sun-shaded, colour-varied, + alpha)
	v_seed = vec2(float(INSTANCE_ID) * 1.37, float(INSTANCE_ID) * 0.71);
	// Per-instance is plenty: puffs are small against the camera-to-puff distances involved.
	vec3 vd = normalize(MODEL_MATRIX[3].xyz - INV_VIEW_MATRIX[3].xyz);
	v_phase = pow(clamp(dot(vd, sun_dir) * 0.5 + 0.5, 0.0, 1.0), 6.0);
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
	float a = v_col.a * fall * clamp(0.7 + 0.4 * n, 0.0, 1.1);
	if (a < 0.003) discard;                        // skip near-transparent rim: kill fill-rate overdraw
	// Noise gently breaks up the flat colour; the additive forward-scatter glow keeps authored-dark
	// dust dark off-sun and ignites it with the sun colour when backlit.
	ALBEDO = v_col.rgb * (0.85 + 0.28 * n) + sun_color * (v_phase * backlight * fall * v_col.a);
	ALPHA = a;
}
";

    // Spin-tracking shadow-VOLUME shader. The mesh is the occluder's convex hull baked in LOCAL space
    // (ShadowVolume.Build) and parented to the rock, so MODEL_MATRIX carries the rock's current spin. Per
    // vertex we transform to world, then push it the full shaft length along the world-space `downsun`
    // axis IFF its (world) face normal turns away from the sun — sunward faces stay as the near cap,
    // anti-sun faces translate to the far tip, and the silhouette fins stretch between them. The result is
    // the hull swept downsun = a closed convex volume, so cull_back keeps the camera-facing shell only and
    // the multiply lands exactly ONCE. `dark` is the multiply factor at the shaft core (1.0 = no change);
    // the length fade (0 at the rock, 1 at the tip) softens the shaft out into the dust.
    private const string ShaftShaderCode =
        @"
shader_type spatial;
render_mode blend_mul, unshaded, cull_back, depth_draw_never, shadows_disabled;

uniform float dark = 0.88;
uniform vec3 downsun = vec3(0.0, -1.0, 0.0); // world-space unit axis, away from the sun
uniform float shaft_len = 3500.0;
uniform float fade_dist = 450.0; // metres downsun over which the shadow fades to nothing

varying float v_t; // 0 at the near (occluder) cap, 1 at the far (downsun) tip

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 wn = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz); // rock's CURRENT world face normal
	float ext = step(0.0, dot(wn, downsun));                    // 1 = faces away from sun → extrude
	world += downsun * (shaft_len * ext);
	v_t = ext;
	MODELVIEW_MATRIX = VIEW_MATRIX; // treat `world` as already world-space (extrusion done above)
	VERTEX = world;
}

void fragment() {
	// Fade by ACTUAL world distance downsun (v_t*shaft_len metres), not by a fraction of the shaft, so the
	// shadow is full-dark at the rock and gone within `fade_dist` metres regardless of shaft length.
	float dist = v_t * shaft_len;
	float len_fade = 1.0 - smoothstep(0.0, fade_dist, dist); // 1 at the rock → 0 a few hundred metres out
	if (len_fade < 0.004) discard;                           // past the fade → skip the multiply entirely
	ALBEDO = mix(vec3(1.0), vec3(dark), len_fade);
	ALPHA = 1.0;
}
";

    // Screen-space crepuscular ""god rays"": march from each pixel toward the sun's screen position,
    // accumulating only the BRIGHT pixels along the way (the sun disc + sunlit dust) with distance decay,
    // and add the result. Dark geometry contributes nothing, so it never flat-tints the scene.
    //
    // The ray SOURCE is masked to a disc/halo around the sun (sun_pos + sun_mask_*): a sampled bright pixel
    // only seeds shafts if it sits near the sun on screen, so engine glow (or any other bright emitter)
    // elsewhere in the frame no longer casts its own crepuscular shafts — only the sun does.
    private const string GodRayShaderCode =
        @"
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform vec2 sun_pos = vec2(0.5, 0.5);
uniform float intensity = 0.9;
uniform vec3 ray_color : source_color = vec3(1.0, 0.9, 0.7);
uniform float present = 0.0;
uniform float aspect = 1.777;      // viewport w/h, so the sun mask is a true circle in screen space
uniform float sun_mask_core = 0.16; // UV radius (screen-height units) of full-strength ray source
uniform float sun_mask_edge = 0.42; // UV radius at which the sun's ray source has faded to nothing

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
			// Distance from THIS sample to the sun on screen (aspect-corrected → circular). Only bright
			// pixels within the sun's disc/halo seed shafts; a bright engine plume off to the side is masked.
			vec2 d = (s - sun_pos) * vec2(aspect, 1.0);
			float sun_mask = 1.0 - smoothstep(sun_mask_core, sun_mask_edge, length(d));
			rays += smp * smoothstep(0.55, 1.1, lum) * illum * sun_mask; // bright AND near the sun shafts
			illum *= DECAY;
		}
		rays = rays / float(SAMPLES) * intensity * present;
	}
	COLOR = vec4(base + rays * ray_color, 1.0);
}
";
}
