using Godot;

// Dynamic engine glow for a ship (.PLAN "Engine glow"). Built procedurally in C#
// like the rest of the world visuals (see Sun/AlephView), so there's no scene
// asset to keep in sync. Each ship node owns one EngineGlow and feeds it a
// throttle value every frame; the glow eases toward it so power ramps in/out
// smoothly instead of popping.
//
// A single throttle (0..1) drives the whole effect: per engine nozzle there's an
// emissive flame plume (the engine's HDR bloom — the existing WorldEnvironment
// glow pipeline does the actual blur/shader pass), a billowing GPU-particle
// exhaust trail, and a hot inner core; an OmniLight3D washes nearby surfaces with
// engine light. Near full throttle an AFTERBURNER ("boost") kicks in: the plume
// stretches and shifts to a hotter blue-white, the exhaust speeds up, and the
// wash brightens — the visual payoff for holding the throttle pinned. The flight
// model has no separate boost input yet (that's the future "booster" backlog
// item); when it lands, route its level into SetThrottle's boost and the same
// afterburner path lights up on demand.
//
// Coordinates: the ship mesh points local +Z (FlightModel forward), so the
// engines sit at the rear (-Z) and the exhaust streams backward along -Z.
public partial class EngineGlow : Node3D
{
    // --- Per-ship configuration, set by WorldRenderer before AddChild. ---

    // Local positions of each engine nozzle (rear face of the hull). One for a
    // Scout's single thruster, two for a Fighter's heavier twin engines.
    public Vector3[] Nozzles = { Vector3.Zero };

    // Flame plume size at full throttle (before the afterburner stretch).
    public float NozzleRadius = 0.9f;
    public float PlumeLength = 5.5f;

    // Engine-light reach; scaled by throttle so an idling ship barely glows.
    public float LightRange = 14f;

    // Hot exhaust colour, tinted toward the ship's team hue so friend/foe still
    // reads at a glance. The afterburner blends this toward BoostColor.
    public Color CoreColor = new(0.6f, 0.8f, 1f);

    // --- Tuning ---------------------------------------------------------

    private const float IdleThrottle = 0.12f; // engines never go fully black while alive
    private const float EaseRate = 9f; // spool-UP rate: engines ramp in smoothly (1/s)
    private static readonly Color BoostColor = new(0.85f, 0.92f, 1f); // blue-white afterburner

    // ========================================================================
    //  SMOKE PLUME TUNING  —  every knob for the exhaust smoke, in one place.
    // ========================================================================
    // The plume is a stream of soft, mix-blended motes driven by a custom particle SHADER
    // (SmokeShaderCode) and world-anchored, so motes hang in space once emitted and the ship
    // trails smoke behind it. Two mathematical curves, both peaking partway along each mote's
    // life, sculpt the plume (all parameters below; *_At in 0..1 = birth..death):
    //   • MOVEMENT (the bell): each mote drifts back along the nozzle axis AND swings out
    //     sideways by SmokeBell, peaking at SmokeBellAt, then converging back to the axis —
    //     so the cloud fans into a bell near the exhaust and re-merges toward the tail.
    //   • SIZE (the billow): each mote is born small (SmokeTip), GROWS to SmokeGrow× at
    //     SmokeGrowAt, then SHRINKS back to a wisp; ×SmokeSizeVar random per-mote base.
    private const int SmokeAmount = 180; // mote count — density/fill of the trail
    private const float SmokeLifetime = 1.5f; // mote lifespan (sec); also how long smoke lingers after boost
    private const float SmokeSize = 2.5f; // base mote DIAMETER, in NozzleRadius units  ← overall bigness
    private const float SmokeSizeVar = 0.6f; // ± random per-mote size variation (0 = all identical, 0.6 = 40%–160%)
    private const float SmokeSpeed = 4f; // backward drift speed; with Lifetime sets plume LENGTH
    private const float SmokeSpeedVar = 0.5f; // ± random drift spread
    private const float SmokeBell = 2f; // lateral fan-out radius, in NozzleRadius units  ← how wide the bell
    private const float SmokeBellAt = 0.4f; // where the bell is widest along the life (0..1)
    private const float SmokeBoostSpeedUp = 1.05f; // particle SpeedScale at full boost (keep ~1 so the shape holds)
    private const float SmokeGrow = 2.2f; // life-curve PEAK: a mote swells to this × its base size  ← billow amount
    private const float SmokeGrowAt = 0.1f; // where the size peak sits along the life (0..1)
    private const float SmokeTip = 0.4f; // size at birth & death (curve ends)  ← how small it starts/ends
    private const float SmokeOpacity = 0.75f; // peak per-mote alpha (mix-blended coverage)

    // Per-nozzle visuals we modulate every frame. Materials/particles are unique
    // per EngineGlow instance so one ship's throttle never bleeds into another's.
    private readonly System.Collections.Generic.List<StandardMaterial3D> _plumeMats = new();
    private readonly System.Collections.Generic.List<StandardMaterial3D> _innerMats = new();
    private readonly System.Collections.Generic.List<Node3D> _plumeHolders = new();
    private readonly System.Collections.Generic.List<Node3D> _innerHolders = new();
    private readonly System.Collections.Generic.List<StandardMaterial3D> _coreMats = new();
    private readonly System.Collections.Generic.List<GpuParticles3D> _exhausts = new();
    private readonly System.Collections.Generic.List<ShaderMaterial> _exhaustMats = new();

    // Custom particle-process shader, compiled once and shared by every nozzle's emitter
    // (per-nozzle uniforms live on each ShaderMaterial). Built lazily so it's only paid for
    // the first time a ship spawns an engine.
    private static Shader? _smokeShaderCached;
    private static Shader SmokeShader => _smokeShaderCached ??= new Shader { Code = SmokeShaderCode };
    private OmniLight3D _light = null!;

    private float _target; // requested throttle (0..1)
    private float _shown; // eased throttle actually rendered
    private float _targetBoost; // requested afterburner (0..1)
    private float _shownBoost; // eased afterburner actually rendered
    private float _flicker; // small per-instance phase so engines don't pulse in lockstep
    private float _smokeFade; // seconds of exhaust smoke still potentially in flight; keeps the node drawn while it ages out

    // Spatial engine audio. Both loops are children of this node, so they ride the
    // ship's world transform for free (this covers local, remote, and AI ships —
    // everything flows through SetThrottle). Driven off the EASED throttle/boost in
    // _Process so the sound spools with the glow rather than snapping on input.
    private AudioStreamPlayer3D? _engineSfx;
    private AudioStreamPlayer3D? _boostSfx;
    private AudioStreamPlayer3D? _boostStartSfx;
    private const float EngineUnitSize = 50f;
    private const float EngineMaxDistance = 1400f;

    // Rising/falling-edge detection on the eased boost level, so the ignition one-shot
    // fires exactly once per afterburner engagement and the sustain loop gets a brief
    // release fade instead of an instant cut. See _Process/BuildAudio.
    private const float BoostEdgeThreshold = 0.05f;
    private const float BoostReleaseRate = 12f; // release fade rate (1/s); ~0.25s to settle near -80dB
    private bool _boosting;
    private float _boostAudioDb = -80f; // actual played volume for _boostSfx: tracks DriveToDb on the
    // way up (riding the existing spool-up ramp) but eases independently on the way down, since
    // _shownBoost itself snaps straight to 0 (EaseToward cuts down instantly for the visual flame).

    // Feed the current drive each frame. throttle (0..1) is forward thrust and
    // always glows the engines; boost (0..1) is the afterburner — a SEPARATE input
    // (the local pilot's afterburner key, a PIG's turn-burst, a remote ship near
    // top speed) that stretches/brightens the flame AND is the only thing that
    // lights the exhaust smoke. Both clamped; reverse thrust simply reads as idle.
    public void SetThrottle(float throttle, float boost)
    {
        _target = Mathf.Clamp(throttle, 0f, 1f);
        _targetBoost = Mathf.Clamp(boost, 0f, 1f);
    }

    public override void _Ready()
    {
        _flicker = GD.Randf() * Mathf.Tau;

        // Soft radial mote for the hot cores: an 8-bit shape only — the HDR brightness
        // that drives bloom comes from emission energy.
        var dot = RadialDot();
        // Soft-edged mote for the exhaust: a dense pile of these, mix-blended, builds one
        // continuous smoke volume; the particle shader moves/sizes each mote into the plume.
        var smoke = SmokeMote();

        var avg = Vector3.Zero;
        foreach (var n in Nozzles)
        {
            avg += n;
            BuildNozzle(n, dot, smoke);
        }
        avg /= Nozzles.Length;

        // One engine-wash light for the whole ship, parked just behind the nozzles.
        _light = new OmniLight3D
        {
            Position = avg + new Vector3(0f, 0f, -PlumeLength * 0.3f),
            OmniRange = LightRange,
            LightColor = CoreColor,
            LightEnergy = 0f,
            ShadowEnabled = false,
        };
        AddChild(_light);

        ApplyVisual(0f, 0f); // start dark; _Process lights it once throttle is fed

        BuildAudio();
    }

    // Engine hum + afterburner whoosh, both looping and parked behind the hull with
    // the wash light. Volume/pitch are modulated every frame in _Process. Guarded:
    // if the audio service or its streams aren't up, the ship simply runs silent.
    private void BuildAudio()
    {
        var sfx = SfxManager.Instance;
        if (sfx == null)
            return;
        Vector3 rear = new(0f, 0f, -PlumeLength * 0.3f);
        var engine = sfx.GetStream(SfxManager.SfxId.EngineLoop);
        if (engine != null)
        {
            _engineSfx = new AudioStreamPlayer3D
            {
                Stream = engine,
                Bus = "Engines",
                UnitSize = EngineUnitSize,
                MaxDistance = EngineMaxDistance,
                Position = rear,
                VolumeDb = -80f, // silent until throttle drives it up
            };
            AddChild(_engineSfx);
            _engineSfx.Play();
        }
        var boost = sfx.GetStream(SfxManager.SfxId.BoosterLoop);
        if (boost != null)
        {
            _boostSfx = new AudioStreamPlayer3D
            {
                Stream = boost,
                Bus = "Engines",
                UnitSize = EngineUnitSize,
                MaxDistance = EngineMaxDistance,
                Position = rear,
                VolumeDb = -80f, // silent until the afterburner fires
            };
            AddChild(_boostSfx);
            _boostSfx.Play();
        }
        // Ignition one-shot: a second, non-looping player so ramping up the afterburner
        // gets a distinct "kick" (the recorded attack transient) instead of the sustain
        // loop's steady middle re-firing that attack every loop restart. Fired once per
        // rising edge in _Process; the sustain loop (_boostSfx) keeps ramping in underneath
        // via the existing DriveToDb mapping.
        var boostStart = sfx.GetStream(SfxManager.SfxId.BoosterStart);
        if (boostStart != null)
        {
            _boostStartSfx = new AudioStreamPlayer3D
            {
                Stream = boostStart,
                Bus = "Engines",
                UnitSize = EngineUnitSize,
                MaxDistance = EngineMaxDistance,
                Position = rear,
            };
            AddChild(_boostStartSfx);
        }
    }

    // Map an eased 0..1 drive level onto a playable VolumeDb, fading to silence at 0.
    private static float DriveToDb(float level) =>
        level <= 0.001f ? -80f : Mathf.Lerp(-26f, -2f, Mathf.Clamp(level, 0f, 1f));

    private void BuildNozzle(Vector3 pos, Texture2D dot, Texture2D smoke)
    {
        // Holder sits at the nozzle, axis-aligned with the hull, so scaling its Z
        // stretches the plume backward (afterburner) while pinning the wide end to
        // the hull. 90° child rotations keep the basis axis-aligned, so this
        // non-uniform parent scale stays shear-free.
        var holder = new Node3D { Position = pos };
        AddChild(holder);
        _plumeHolders.Add(holder);

        // Flame plume: wide at the nozzle, narrowing to a CUT-OFF tail (a frustum, not a
        // sharp spike) so it reads as a jet-exhaust mouth rather than a needle. ApplyVisual
        // jitters its length/width every frame for the licking-flame turbulence.
        var plumeMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = CoreColor,
            EmissionEnabled = true,
            Emission = CoreColor,
            EmissionEnergyMultiplier = 3f,
        };
        _plumeMats.Add(plumeMat);
        holder.AddChild(
            new MeshInstance3D
            {
                Mesh = new CylinderMesh
                {
                    TopRadius = NozzleRadius * 0.22f, // cut-off tail (exhaust mouth, not a point)
                    BottomRadius = NozzleRadius, // wide at the nozzle
                    Height = PlumeLength,
                    RadialSegments = 12,
                },
                MaterialOverride = plumeMat,
                // +Y wide-end -> -Z (backward); offset back by half-length so the wide end sits
                // on the nozzle and growth extends behind the ship.
                RotationDegrees = new Vector3(-90f, 0f, 0f),
                Position = new Vector3(0f, 0f, -PlumeLength * 0.5f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );

        // Hotter, shorter INNER plume in its OWN holder so ApplyVisual can stretch it
        // out of phase with the outer flame — the two layers writhe independently, which
        // is what sells a fluid jet instead of a single rigid cone.
        var innerHolder = new Node3D { Position = pos };
        AddChild(innerHolder);
        _innerHolders.Add(innerHolder);

        var innerMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = CoreColor,
            EmissionEnabled = true,
            Emission = CoreColor,
            EmissionEnergyMultiplier = 5f,
        };
        _innerMats.Add(innerMat);
        float innerLen = PlumeLength * 0.55f;
        innerHolder.AddChild(
            new MeshInstance3D
            {
                Mesh = new CylinderMesh
                {
                    TopRadius = NozzleRadius * 0.1f,
                    BottomRadius = NozzleRadius * 0.55f,
                    Height = innerLen,
                    RadialSegments = 12,
                },
                MaterialOverride = innerMat,
                RotationDegrees = new Vector3(-90f, 0f, 0f),
                Position = new Vector3(0f, 0f, -innerLen * 0.5f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );

        // Hot inner core right at the nozzle mouth (a bright billboarded mote).
        var coreMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoTexture = dot,
            EmissionEnabled = true,
            EmissionTexture = dot,
            Emission = Colors.White,
            EmissionEnergyMultiplier = 4f,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
        };
        _coreMats.Add(coreMat);
        // Core + exhaust hang off the EngineGlow (not the holder) so the afterburner
        // length-scale only stretches the flame cone, never the billboard or emitter.
        AddChild(
            new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(NozzleRadius * 2.2f, NozzleRadius * 2.2f) },
                MaterialOverride = coreMat,
                Position = pos,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );

        // GPU-particle exhaust driven by the custom SmokeShaderCode. World-anchored (NOT local
        // coords): the shader bakes the nozzle's facing into each mote's VELOCITY at birth, so
        // once emitted a mote keeps its own world-space path and the ship flies away from it —
        // a real trail that stays put when the ship turns. The shader applies the lateral BELL
        // (fan out then re-merge) and the grow/shrink SIZE billow directly, so there's no
        // ParticleProcessMaterial; the draw quad is unit-sized and the shader sets full scale.
        // All tuning is fed in as uniforms from the SMOKE PLUME TUNING block above.
        var procMat = new ShaderMaterial { Shader = SmokeShader };
        procMat.SetShaderParameter("life_s", SmokeLifetime);
        procMat.SetShaderParameter("speed", SmokeSpeed);
        procMat.SetShaderParameter("speed_var", SmokeSpeedVar);
        procMat.SetShaderParameter("spawn_radius", NozzleRadius * 0.3f);
        procMat.SetShaderParameter("bell_amp", NozzleRadius * SmokeBell);
        procMat.SetShaderParameter("bell_at", SmokeBellAt);
        procMat.SetShaderParameter("size_base", NozzleRadius * SmokeSize);
        procMat.SetShaderParameter("size_var", SmokeSizeVar);
        procMat.SetShaderParameter("grow", SmokeGrow);
        procMat.SetShaderParameter("grow_at", SmokeGrowAt);
        procMat.SetShaderParameter("tip", SmokeTip);
        procMat.SetShaderParameter("opacity", SmokeOpacity);
        procMat.SetShaderParameter("smoke_color", CoreColor);
        _exhaustMats.Add(procMat);
        var exhaust = new GpuParticles3D
        {
            Amount = SmokeAmount,
            Lifetime = SmokeLifetime,
            LocalCoords = false, // world-anchored: motes keep their own path once emitted
            ProcessMaterial = procMat,
            DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1f) }, // shader sets the real size
            MaterialOverride = ExhaustDrawMaterial(smoke),
            Position = pos,
            Emitting = false, // lit only while the afterburner is firing (see ApplyVisual)
        };
        _exhausts.Add(exhaust);
        AddChild(exhaust);
    }

    public override void _Process(double delta)
    {
        // Ease the rendered throttle toward the request (frame-rate independent) so
        // power ramps smoothly. A ship sitting at zero throttle eases to true zero and
        // goes fully dark (ApplyVisual) — no idle pilot glow on a parked engine.
        float dt = (float)delta;
        _shown = EaseToward(_shown, _target, dt);
        _shownBoost = EaseToward(_shownBoost, _targetBoost, dt);
        _flicker += dt * 12f; // slow enough that turbulence reads as flow, not a strobe
        _smokeFade = Mathf.Max(0f, _smokeFade - dt); // count down the lingering-smoke window
        ApplyVisual(_shown, _shownBoost);

        // Engine pitch rises with throttle; both loops fade in with their drive level
        // (boost only audible while the afterburner is lit).
        if (_engineSfx != null)
        {
            _engineSfx.VolumeDb = DriveToDb(_shown);
            _engineSfx.PitchScale = Mathf.Lerp(0.8f, 1.4f, _shown);
        }
        if (_boostSfx != null)
        {
            // Rising/falling edge on the eased boost level: fire the ignition one-shot once
            // per engagement, and let the sustain loop release with a brief fade rather than
            // snapping silent the instant the pilot lets off (EaseToward cuts _shownBoost down
            // instantly for the visual flame, so the loop's volume can't just follow it).
            bool boostingNow = _shownBoost > BoostEdgeThreshold;
            if (boostingNow && !_boosting)
            {
                _boostStartSfx?.Play();
            }
            else if (!boostingNow && _boosting)
            {
                if (_boostStartSfx is { Playing: true })
                    _boostStartSfx.Stop();
            }
            _boosting = boostingNow;

            float targetDb = DriveToDb(_shownBoost);
            _boostAudioDb =
                targetDb > _boostAudioDb
                    ? targetDb // ride the existing spool-up ramp on the way in
                    : Mathf.Lerp(_boostAudioDb, targetDb, 1f - Mathf.Exp(-BoostReleaseRate * dt)); // ease out (~0.25s)
            _boostSfx.VolumeDb = _boostAudioDb;
            _boostSfx.PitchScale = Mathf.Lerp(0.9f, 1.3f, _shownBoost);
        }
    }

    // Spool UP smoothly; cut DOWN instantly. The flame must die the frame the pilot drops
    // throttle, not linger bright on a hull coasting under drag, so a falling target snaps
    // straight through (no ease) while a rising one ramps in.
    private static float EaseToward(float cur, float target, float dt) =>
        target <= cur ? target : Mathf.Lerp(cur, target, 1f - Mathf.Exp(-EaseRate * dt));

    // Map the current throttle + afterburner onto every modulated visual.
    private void ApplyVisual(float throttle, float boost)
    {
        // Master on/off envelope: the engine is fully dark at rest and ramps to full
        // glow by IdleThrottle, so a parked ship (throttle 0, no boost) shows nothing.
        // Folds into every emission energy, every additive-plume ALBEDO alpha, and the wash
        // light, so at glow 0 the flame draws nothing even though the node stays alive.
        float glow = Mathf.Clamp(Mathf.Max(throttle / IdleThrottle, boost), 0f, 1f);

        // Smoke must outlive the flame. The instant boost drops, the hot plume cuts out
        // (EaseToward snaps boost down), but the exhaust already in the air should keep
        // drifting and fading over its full lifetime — not blink out. So while boosting we
        // keep topping up a lingering-smoke window, and the node stays drawn until BOTH the
        // flame is dark AND that window has elapsed. (Hard-hiding the node on `glow` is
        // exactly what used to kill the live smoke particles mid-flight.)
        bool burning = boost > 0.02f;
        if (burning)
            _smokeFade = SmokeLifetime;
        Visible = glow > 0.001f || _smokeFade > 0f;
        if (!Visible)
        {
            _light.LightEnergy = 0f;
            foreach (var ex in _exhausts)
                ex.Emitting = false;
            return;
        }

        // Tiny brightness flicker so the flame lives a little; deeper at low power.
        float flick = 1f + (0.04f + 0.03f * (1f - throttle)) * Mathf.Sin(_flicker);

        // Turbulence: layered out-of-phase sines breathe the plume's length (and a touch
        // of width) so the exhaust licks like a fluid jet instead of holding a rigid cone.
        // Amplitude is kept gentle and the harmonics low so it flows rather than blinks;
        // it still grows with throttle so an idling engine stays calm. The inner layer
        // uses a different phase/frequency so the two cones writhe independently — the
        // offset between them is what reads as turbulent flow.
        float turbAmp = 0.08f + 0.2f * throttle;
        float outerTurb = 0.6f * Mathf.Sin(_flicker * 1.7f) + 0.4f * Mathf.Sin(_flicker * 3.1f + 1.3f);
        float innerTurb = 0.6f * Mathf.Sin(_flicker * 2.3f + 2.1f) + 0.4f * Mathf.Sin(_flicker * 3.9f);
        float widthTurb = 1f + turbAmp * 0.4f * Mathf.Sin(_flicker * 2.5f + 0.7f);

        Color plumeColor = CoreColor.Lerp(BoostColor, boost);
        // Short base plume; the afterburner is the only thing that really stretches it.
        float baseLen = Mathf.Lerp(0.3f, 0.7f, throttle) + boost * 0.55f;
        float lengthScale = baseLen * (1f + turbAmp * outerTurb);
        float innerLength = baseLen * 0.8f * (1f + turbAmp * 1.4f * innerTurb); // shorter + livelier
        float widthScale = Mathf.Lerp(0.7f, 1f, throttle) * widthTurb;
        float plumeEnergy = (1.4f + 4.6f * throttle + 2.5f * boost) * flick * glow;
        float coreEnergy = (1f + 5f * throttle + 3f * boost) * flick * glow;
        // Inner jet runs hotter and biases toward white; brightest under afterburner.
        Color innerColor = plumeColor.Lerp(Colors.White, 0.5f);
        float innerEnergy = (2f + 6f * throttle + 4f * boost) * flick * glow;

        for (int i = 0; i < _plumeHolders.Count; i++)
        {
            // Wispier outer flame (lower alpha) so it reads as gas, not a solid shell. All
            // albedo alphas are scaled by `glow` so the additive flame fully stops drawing
            // at glow 0 (the node may still be alive for lingering smoke).
            _plumeHolders[i].Scale = new Vector3(widthScale, widthScale, lengthScale);
            _plumeMats[i].Emission = plumeColor;
            _plumeMats[i].AlbedoColor = new Color(
                plumeColor.R,
                plumeColor.G,
                plumeColor.B,
                Mathf.Lerp(0.15f, 0.45f, throttle) * glow
            );
            _plumeMats[i].EmissionEnergyMultiplier = plumeEnergy;
            _innerHolders[i].Scale = new Vector3(widthScale * 0.9f, widthScale * 0.9f, innerLength);
            _innerMats[i].Emission = innerColor;
            _innerMats[i].AlbedoColor = new Color(
                innerColor.R,
                innerColor.G,
                innerColor.B,
                Mathf.Lerp(0.25f, 0.6f, throttle) * glow
            );
            _innerMats[i].EmissionEnergyMultiplier = innerEnergy;
            _coreMats[i].Emission = Colors.White.Lerp(plumeColor, 0.4f * (1f - boost));
            _coreMats[i].AlbedoColor = new Color(1f, 1f, 1f, glow);
            _coreMats[i].EmissionEnergyMultiplier = coreEnergy;
        }

        // Exhaust smoke is the afterburner's tell: it only EMITS while boosting, but motes
        // already in the air keep aging out and fading after boost ends (see _smokeFade).
        for (int i = 0; i < _exhausts.Count; i++)
        {
            _exhausts[i].Emitting = burning;
            _exhausts[i].AmountRatio = Mathf.Lerp(0.55f, 1f, boost);
            // Keep the motes near their base speed even at full boost: speeding them up just
            // stretches the plume and washes the spindle out, so stay close to 1.
            _exhausts[i].SpeedScale = Mathf.Lerp(1f, SmokeBoostSpeedUp, boost);
            _exhaustMats[i].SetShaderParameter("smoke_color", plumeColor);
        }

        _light.LightColor = plumeColor;
        _light.LightEnergy = (0.4f + 2.6f * throttle + 1.5f * boost) * flick * glow;
        _light.OmniRange = LightRange * (0.7f + 0.3f * throttle);
    }

    // Soft round mote: hot centre fading to transparent — drives bloom via emission.
    private static GradientTexture2D RadialDot()
    {
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.5f, 1f },
            Colors = new[] { new Color(1f, 1f, 1f, 1f), new Color(1f, 1f, 1f, 0.4f), new Color(1f, 1f, 1f, 0f) },
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 128,
            Height = 128,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }

    // Custom particle-process shader for the exhaust smoke. It owns the whole motion+look of a
    // mote so the shape is explicit and reliable (no ParticleProcessMaterial curve quirks):
    //
    //   • At birth (start): bake the nozzle's world-space backward axis into VELOCITY (so the
    //     mote drifts straight back in WORLD space and keeps that path as the ship flies on),
    //     and stash that axis + the birth TIME in CUSTOM. Per-mote randomness (angle, size,
    //     speed) is hashed from the stable particle NUMBER, so it can be recomputed each frame
    //     without storing it.
    //   • Each frame (process): bump(t, *_At) is a smooth 0→1→0 hump peaking at *_At.
    //       - BELL: add a lateral offset = bell_amp * bump(t, bell_at) along a fixed per-mote
    //         sideways direction — the mote fans out from the axis then re-merges by death.
    //         (Applied incrementally on top of the engine's VELOCITY integration so the
    //         world-space backward drift is preserved.)
    //       - SIZE: scale = base × random × mix(tip, grow, bump(t, grow_at)) — grow then shrink.
    //       - ALPHA: fade in fast, hold, fade out so the tail dissipates.
    private const string SmokeShaderCode =
        @"
shader_type particles;

uniform float life_s;
uniform float speed;
uniform float speed_var;
uniform float spawn_radius;
uniform float bell_amp;
uniform float bell_at;
uniform float size_base;
uniform float size_var;
uniform float grow;
uniform float grow_at;
uniform float tip;
uniform float opacity;
uniform vec4 smoke_color : source_color;

float hash11(float p) {
	p = fract(p * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

// Smooth hump: 0 at t=0 and t=1, 1 at t=at (sine ease on each side).
float bump(float t, float at) {
	at = clamp(at, 0.001, 0.999);
	float u = t < at ? t / at : (1.0 - t) / (1.0 - at);
	return sin(clamp(u, 0.0, 1.0) * 1.5707963);
}

// Stable per-mote sideways unit vector, perpendicular to the backward axis.
vec3 lateral_dir(vec3 backward, float n) {
	float ang = hash11(n * 1.7) * 6.2831853;
	vec3 up = abs(backward.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
	vec3 side = normalize(cross(backward, up));
	vec3 fwd = cross(backward, side);
	return side * cos(ang) + fwd * sin(ang);
}

void start() {
	float n = float(NUMBER);
	vec3 backward = normalize(mat3(EMISSION_TRANSFORM) * vec3(0.0, 0.0, -1.0));
	float spv = (hash11(n * 5.1) * 2.0 - 1.0) * speed_var;

	TRANSFORM[3].xyz = EMISSION_TRANSFORM[3].xyz + lateral_dir(backward, n) * spawn_radius * hash11(n * 7.3);
	VELOCITY = backward * (speed + spv);

	CUSTOM = vec4(backward, TIME);
	COLOR = smoke_color;
}

void process() {
	vec3 backward = CUSTOM.xyz;
	float n = float(NUMBER);
	float age = TIME - CUSTOM.w;
	float t = clamp(age / life_s, 0.0, 1.0);
	float tp = clamp((age - DELTA) / life_s, 0.0, 1.0);

	// Lateral bell, added incrementally so the engine's backward VELOCITY integration stands.
	float d_off = bell_amp * (bump(t, bell_at) - bump(tp, bell_at));
	TRANSFORM[3].xyz += lateral_dir(backward, n) * d_off;

	// Grow-then-shrink size (uniform scale on the billboarded quad).
	float sr = 1.0 + (hash11(n * 3.3) * 2.0 - 1.0) * size_var;
	float s = mix(tip, grow, bump(t, grow_at)) * sr * size_base;
	TRANSFORM[0].xyz = vec3(s, 0.0, 0.0);
	TRANSFORM[1].xyz = vec3(0.0, s, 0.0);
	TRANSFORM[2].xyz = vec3(0.0, 0.0, s);

	float fade = smoothstep(0.0, 0.12, t) * (1.0 - smoothstep(0.55, 1.0, t));
	COLOR = vec4(smoke_color.rgb, opacity * fade);
}
";

    // Exhaust mote shape: a fuller soft-edged puff (vs. the cores' tighter RadialDot). The
    // solid-ish core gives each mote enough presence that the dense pile resolves a defined
    // plume silhouette, while the feathered rim still blends the seams into continuous smoke.
    private static GradientTexture2D SmokeMote()
    {
        var gradient = new Gradient
        {
            Offsets = [0f, 0.45f, 1f],
            Colors =
            [
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0.8f), // fuller core so the mote reads as a defined puff...
                new Color(1f, 1f, 1f, 0f), // ...then feathers to nothing for a soft but real edge
            ],
        };

        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 128,
            Height = 128,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }

    // MIX (alpha) blend, NOT additive: additive sums to a featureless bright blob (reads as
    // fire/energy), whereas alpha-over lets overlapping translucent puffs build a real
    // COVERAGE silhouette — the only way the spindle shape (and "smoke" at all) reads. No
    // emission: smoke is lit translucent matter, the hot glow is the separate flame/core.
    private static StandardMaterial3D ExhaustDrawMaterial(Texture2D smoke) =>
        new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Mix,
            AlbedoTexture = smoke,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            // CRITICAL: particle billboards normalise away the per-mote transform SCALE unless this
            // is set — without it every mote renders at the draw quad's base size and the shader's
            // grow/shrink (and SmokeSize itself) has no visible effect at all.
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true, // honour the shader's per-mote COLOR (tint + fade alpha)
        };
}
