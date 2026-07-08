using Godot;

// Subtle space dust (.PLAN "more fx"). Empty space gives the eye nothing to gauge
// motion against — distant stars barely shift. This scatters a sparse field of dim
// motes in a box around the local ship; because they live in WORLD space (not local
// to the emitter) the ship flies THROUGH them, so near motes parallax past and sell
// the feeling of movement. Built procedurally in C# like the rest of the visuals.
//
// Deliberately subtle: low count, dim, no HDR (kept below the glow threshold so the
// dust never blooms), so it reads as fine grit at speed without cluttering the view.
public partial class DustField : Node3D
{
    // Half-size of the emission box around the ship. Kept fairly tight so motes are
    // near enough to parallax noticeably; the lifetime cycles them as the ship moves.
    private const float BoxHalf = 90f;

    // Motion-gated visibility: the dust exists to gauge MOVEMENT, so at rest it's just
    // clutter hovering in the void. Below MinSpeed it fades fully out; by FullSpeed it's
    // at full strength, ramping linearly between. Values are u/s (sim speed), picked low
    // so gentle drift already shows some grit and only a true standstill hides it.
    private const float MinSpeed = 2f;
    private const float FullSpeed = 18f;

    // Per-second lerp rate toward the speed-derived target, so the field eases in/out over
    // a fraction of a second instead of snapping when the throttle crosses the threshold.
    private const float FadeRate = 3.5f;

    private WorldRenderer _world = null!;
    private GpuParticles3D _particles = null!;
    private StandardMaterial3D _drawMat = null!;
    private float _fade; // current smoothed 0..1 strength, multiplies particle alpha

    // Audio counterpart to the visual grit. NOT a single loop (that read as an obvious,
    // static drone); instead the dust clip is fired as many short one-shot GRAINS at
    // randomised points scattered around the ship, so it sounds like grit rushing past from
    // all sides. Overlapping grains at jittered pitch/level never repeat audibly. Two things
    // drive it: proximity to a real dust CLOUD's core (SectorEnvironment owns those, streamed
    // per sector) makes it louder AND more frequent, and ship SPEED raises the grain rate and
    // pitch — so faster through denser dust = a thicker, higher rush. Grains go through
    // SfxManager's pooled positional one-shot path (PlayAt), same as weapon/impact SFX.
    private SectorEnvironment? _sectorEnv;
    private double _grainTimer; // counts down to the next grain; 1/rate is added on each emit

    public override void _Ready()
    {
        _world = GetNode<WorldRenderer>("../WorldRenderer");

        var dot = RadialDot();
        var proc = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(BoxHalf, BoxHalf, BoxHalf),
            Direction = Vector3.Zero,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 0f,
            Gravity = Vector3.Zero,
            ScaleMin = 0.12f,
            ScaleMax = 0.3f,
            Color = new Color(0.7f, 0.72f, 0.8f, 0.35f), // dim, faintly cool — no HDR, won't bloom
        };

        _particles = new GpuParticles3D
        {
            Amount = 90,
            Lifetime = 6.0,
            LocalCoords = false, // motes hang in world space so the ship flies through them
            ProcessMaterial = proc,
            DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1f) },
            MaterialOverride = _drawMat = DustDrawMaterial(dot),
            Visible = false,
        };
        AddChild(_particles);

        // Sibling that owns the streamed dust CLOUDS; we query it each frame for how buried in
        // dust the ship is (drives grain loudness/rate). Null-safe — no clouds ⇒ no dust audio.
        _sectorEnv = GetNodeOrNull<SectorEnvironment>("../SectorEnvironment");
    }

    public override void _Process(double delta)
    {
        var ship = _world.LocalShip;
        if (ship == null)
        {
            // No local ship (pre-spawn overview): nothing to gauge motion against, so hide.
            _particles.Visible = false;
            _fade = 0f;
            return;
        }

        // Ease the field's strength toward what the ship's speed calls for: hidden at rest,
        // full once cruising. The temporal lerp keeps threshold crossings from popping.
        float target = Mathf.Clamp((ship.Speed - MinSpeed) / (FullSpeed - MinSpeed), 0f, 1f);
        _fade = Mathf.MoveToward(_fade, target, FadeRate * (float)delta);

        UpdateDustAudio(ship, delta);

        _particles.Visible = _fade > 0.001f;
        if (!_particles.Visible)
            return;

        // Multiply the per-particle alpha uniformly via the draw material's albedo alpha
        // (VertexColorUseAsAlbedo makes final alpha = vertexAlpha × albedoAlpha), so the
        // whole field dims together as it fades rather than culling individual motes.
        var c = _drawMat.AlbedoColor;
        _drawMat.AlbedoColor = new Color(c.R, c.G, c.B, _fade);

        // Recenter the emission box on the ship each frame, nudged forward along the ship's
        // FACING (which is where the chase camera looks) so dust always fills the view —
        // regardless of which way the ship is actually translating. Biasing along velocity
        // instead pushed the box out of frame when strafing, so the dust appeared to vanish.
        // Existing motes stay put (world coords), so ANY movement parallaxes through them.
        Vector3 fwd = ship.GlobalTransform.Basis * Vector3.Back; // ship-local +Z forward
        _particles.GlobalPosition = ship.GlobalPosition + fwd * (BoxHalf * 0.4f);
    }

    // --- Dust-grain audio tuning -------------------------------------------------------------
    // Per-cloud Density is authored ~0.3..0.7 (server World.BuildDust), so we normalise the
    // ship's cloud coverage against this reference to reach full intensity near a dense core.
    private const float DensityRef = 0.7f;
    // Dust present in "open" space even away from a named cloud (the visual motes are everywhere):
    // a small baseline ADDED to cloud coverage so flying through clear space still gives faint grit.
    // The whole effect is gated by SPEED below, so this only sounds while actually moving.
    private const float OpenGrit = 0.01f;
    // Below this combined intensity we emit nothing (parked in clear space = silent).
    private const float EmitGate = 0.04f;
    // Grains per second: sparse at the faint edge, a dense rush deep in a cloud at speed.
    private const float MinRate = 1f;
    private const float MaxRate = 12f;
    // Per-grain level: quiet at the fringe up to clearly-present (but not blaring) at a core.
    private const float QuietDb = -45f;
    private const float LoudDb = -1f;
    // Playback rate rides ship speed: a low rumble at a crawl, a brighter hiss at full tilt.
    private const float SlowPitch = 0.18f;
    private const float FastPitch = 4.35f;
    // Shapes the speed→intensity/pitch CURVE: the speed factor is _fade^SpeedExp. 1 = linear
    // (straight ramp). >1 = slow start / steep near top speed (stays quiet longer, then rushes
    // in). <1 = fast start / early plateau (grit comes up quickly off a standstill).
    private const float SpeedExp = 1.5f;
    // How far around the ship grains are scattered (world units) — wide enough to feel all-around
    // yet inside the visual mote box (BoxHalf) so what you hear sits with what you see.
    private const float GrainSpread = 13f;

    // Fire dust grains around the ship at a rate/level set by how buried in dust it is, and a
    // pitch set by its speed. Coverage comes from the streamed clouds (SectorEnvironment); the
    // motion floor keeps a faint rush alive in open space. No cloud data / no motion ⇒ silent.
    private void UpdateDustAudio(PredictionController ship, double delta)
    {
        Vector3 shipPos = ship.GlobalPosition;
        float coverage = (_sectorEnv?.DustDensityAt(shipPos) ?? 0f) / DensityRef;
        // The sound is grit RUSHING PAST, so it's fundamentally about motion: multiply the dust
        // present here (cloud coverage + a faint open-space baseline) by a speed factor. At a
        // standstill the factor→0, so intensity→0 and we fall silent no matter how thick the dust
        // — slowing down fades it out. SpeedExp shapes how that ramp feels (see const above).
        float speedCurve = Mathf.Pow(_fade, SpeedExp);
        float dustHere = Mathf.Clamp(coverage + OpenGrit, 0f, 1f);
        float intensity = speedCurve * dustHere;

        if (intensity < EmitGate)
        {
            _grainTimer = 0.0; // reset so re-entering dust fires promptly, not after a stale debt
            return;
        }

        // More dust AND more speed ⇒ more grains per second (intensity already folds in speed).
        float rate = Mathf.Lerp(MinRate, MaxRate, intensity);
        _grainTimer -= delta;
        // Cap catch-up so a long frame (or returning from a pause) can't dump a burst of grains.
        int budget = 3;
        while (_grainTimer <= 0.0 && budget-- > 0)
        {
            EmitGrain(shipPos, intensity, speedCurve);
            _grainTimer += 1.0 / rate;
        }
    }

    // One positional grain: the dust clip at a random point in a sphere around the ship, its
    // level set by dust intensity and its pitch by the (curved) speed, both jittered so no two
    // grains match.
    private void EmitGrain(Vector3 shipPos, float intensity, float speedCurve)
    {
        var sfx = SfxManager.Instance;
        if (sfx == null)
            return;
        Vector3 offset = new Vector3(GD.Randf() - 0.5f, GD.Randf() - 0.5f, GD.Randf() - 0.5f) * (2f * GrainSpread);
        float volumeDb = Mathf.Lerp(QuietDb, LoudDb, intensity) + (GD.Randf() - 0.5f) * 3f;
        float pitch = Mathf.Lerp(SlowPitch, FastPitch, speedCurve) * (0.92f + GD.Randf() * 0.16f);
        sfx.PlayAt(SfxManager.SfxId.DustAmbient, shipPos + offset, pitch, volumeDb);
    }

    // Discrete round mote: a small opaque core with a near-vertical edge, so it reads as a
    // hard-edged speck rather than a soft fuzzy flake. The core holds full alpha out to
    // ~20% of the radius, then drops to nothing within one antialiasing step — no halo.
    private static GradientTexture2D RadialDot()
    {
        var gradient = new Gradient
        {
            Offsets = [0f, 0.2f, 0.3f, 1f],
            Colors =
            [
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0f),
                new Color(1f, 1f, 1f, 0f),
            ],
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }

    // Distances (world units from camera) over which a mote fades in as it recedes. Below
    // Near it's fully invisible, ramping to fully opaque by Far. This kills the "giant
    // blurry speck" effect: a mote about to pass the camera shrinks to nothing instead of
    // swelling across the screen. Near sits comfortably outside the chase-camera distance.
    private const float FadeNear = 12f;
    private const float FadeFar = 35f;

    // Plain alpha-blended billboards (NOT additive) so the dust stays muted grit rather
    // than glowing — the per-particle colour alpha keeps it faint. DistanceFade culls
    // motes as they approach the camera so they never balloon up close.
    private static StandardMaterial3D DustDrawMaterial(Texture2D dot) =>
        new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = dot,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            VertexColorUseAsAlbedo = true, // honour the per-particle Color (incl. its alpha)
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha,
            DistanceFadeMinDistance = FadeNear,
            DistanceFadeMaxDistance = FadeFar,
        };
}
