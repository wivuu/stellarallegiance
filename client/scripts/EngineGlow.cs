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

	private const float IdleThrottle = 0.12f;   // engines never go fully black while alive
	private const float EaseRate = 9f;           // how fast shown throttle/boost track the target (1/s)
	private static readonly Color BoostColor = new(0.85f, 0.92f, 1f); // blue-white afterburner

	// Per-nozzle visuals we modulate every frame. Materials/particles are unique
	// per EngineGlow instance so one ship's throttle never bleeds into another's.
	private readonly System.Collections.Generic.List<StandardMaterial3D> _plumeMats = new();
	private readonly System.Collections.Generic.List<Node3D> _plumeHolders = new();
	private readonly System.Collections.Generic.List<StandardMaterial3D> _coreMats = new();
	private readonly System.Collections.Generic.List<GpuParticles3D> _exhausts = new();
	private readonly System.Collections.Generic.List<ParticleProcessMaterial> _exhaustMats = new();
	private OmniLight3D _light = null!;

	private float _target;       // requested throttle (0..1)
	private float _shown;        // eased throttle actually rendered
	private float _targetBoost;  // requested afterburner (0..1)
	private float _shownBoost;   // eased afterburner actually rendered
	private float _flicker;      // small per-instance phase so engines don't pulse in lockstep

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

		// Soft radial mote shared by the cores and the exhaust billboards: an 8-bit
		// shape only — the HDR brightness that drives bloom comes from emission energy.
		var dot = RadialDot();

		var avg = Vector3.Zero;
		foreach (var n in Nozzles)
		{
			avg += n;
			BuildNozzle(n, dot);
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

		ApplyVisual(IdleThrottle, 0f);
	}

	private void BuildNozzle(Vector3 pos, Texture2D dot)
	{
		// Holder sits at the nozzle, axis-aligned with the hull, so scaling its Z
		// stretches the plume backward (afterburner) while pinning the wide end to
		// the hull. 90° child rotations keep the basis axis-aligned, so this
		// non-uniform parent scale stays shear-free.
		var holder = new Node3D { Position = pos };
		AddChild(holder);
		_plumeHolders.Add(holder);

		// Flame cone: wide at the nozzle, tapering to a point behind the ship.
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
		holder.AddChild(new MeshInstance3D
		{
			Mesh = new CylinderMesh
			{
				TopRadius = 0f,                 // tail point
				BottomRadius = NozzleRadius,    // wide at the nozzle
				Height = PlumeLength,
				RadialSegments = 12,
			},
			MaterialOverride = plumeMat,
			// +Y tip -> -Z (backward); offset back by half-length so the wide end sits
			// on the nozzle and growth extends behind the ship.
			RotationDegrees = new Vector3(-90f, 0f, 0f),
			Position = new Vector3(0f, 0f, -PlumeLength * 0.5f),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

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
		AddChild(new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(NozzleRadius * 2.2f, NozzleRadius * 2.2f) },
			MaterialOverride = coreMat,
			Position = pos,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		// GPU-particle exhaust trailing backward. Default global simulation makes the
		// motes hang in space as the ship moves, so they read as a proper trail.
		var procMat = new ParticleProcessMaterial
		{
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
			EmissionSphereRadius = NozzleRadius * 0.5f,   // motes fill the nozzle mouth
			Direction = new Vector3(0f, 0f, -1f),
			Spread = 12f,
			InitialVelocityMin = 14f,
			InitialVelocityMax = 22f,
			Gravity = Vector3.Zero,
			// Born large at the nozzle, then shrink over life (ScaleCurve) so the trail
			// tapers to a wisp instead of reading as a string of identical circles.
			ScaleMin = NozzleRadius * 1.3f,
			ScaleMax = NozzleRadius * 2.0f,
			ScaleCurve = ShrinkCurve(),
			Color = CoreColor,
			ColorRamp = ExhaustRamp(),
		};
		_exhaustMats.Add(procMat);
		var exhaust = new GpuParticles3D
		{
			Amount = 56,
			Lifetime = 0.6,
			ProcessMaterial = procMat,
			DrawPass1 = new QuadMesh { Size = new Vector2(NozzleRadius * 1.4f, NozzleRadius * 1.4f) },
			MaterialOverride = ExhaustDrawMaterial(dot),
			Position = pos,
			Emitting = false,   // lit only while the afterburner is firing (see ApplyVisual)
		};
		_exhausts.Add(exhaust);
		AddChild(exhaust);
	}

	public override void _Process(double delta)
	{
		// Ease the rendered throttle toward the request (frame-rate independent) so
		// power ramps smoothly. Idle keeps a faint pilot glow while the ship is alive.
		float dt = (float)delta;
		float ease = 1f - Mathf.Exp(-EaseRate * dt);
		_shown = Mathf.Lerp(_shown, Mathf.Max(IdleThrottle, _target), ease);
		_shownBoost = Mathf.Lerp(_shownBoost, _targetBoost, ease);
		_flicker += dt * 30f;
		ApplyVisual(_shown, _shownBoost);
	}

	// Map the current throttle + afterburner onto every modulated visual.
	private void ApplyVisual(float throttle, float boost)
	{
		// Tiny brightness flicker so the flame lives a little; deeper at low power.
		float flick = 1f + (0.06f + 0.04f * (1f - throttle)) * Mathf.Sin(_flicker);

		Color plumeColor = CoreColor.Lerp(BoostColor, boost);
		float lengthScale = Mathf.Lerp(0.55f, 1f, throttle) + boost * 0.6f; // afterburner stretch
		float widthScale = Mathf.Lerp(0.7f, 1f, throttle);
		float plumeEnergy = (1.4f + 4.6f * throttle + 2.5f * boost) * flick;
		float coreEnergy = (1f + 5f * throttle + 3f * boost) * flick;

		for (int i = 0; i < _plumeHolders.Count; i++)
		{
			_plumeHolders[i].Scale = new Vector3(widthScale, widthScale, lengthScale);
			_plumeMats[i].Emission = plumeColor;
			_plumeMats[i].AlbedoColor = new Color(plumeColor.R, plumeColor.G, plumeColor.B, Mathf.Lerp(0.35f, 0.8f, throttle));
			_plumeMats[i].EmissionEnergyMultiplier = plumeEnergy;
			_coreMats[i].Emission = Colors.White.Lerp(plumeColor, 0.4f * (1f - boost));
			_coreMats[i].EmissionEnergyMultiplier = coreEnergy;
		}

		// Exhaust smoke is the afterburner's tell: it only streams while boosting.
		bool burning = boost > 0.02f;
		for (int i = 0; i < _exhausts.Count; i++)
		{
			_exhausts[i].Emitting = burning;
			_exhausts[i].AmountRatio = Mathf.Lerp(0.4f, 1f, boost);
			_exhausts[i].SpeedScale = Mathf.Lerp(0.9f, 1.8f, boost);
			_exhaustMats[i].Color = plumeColor;
		}

		_light.LightColor = plumeColor;
		_light.LightEnergy = (0.4f + 2.6f * throttle + 1.5f * boost) * flick;
		_light.OmniRange = LightRange * (0.7f + 0.3f * throttle);
	}

	// Soft round mote: hot centre fading to transparent — drives bloom via emission.
	private static GradientTexture2D RadialDot()
	{
		var gradient = new Gradient
		{
			Offsets = new[] { 0f, 0.5f, 1f },
			Colors = new[]
			{
				new Color(1f, 1f, 1f, 1f),
				new Color(1f, 1f, 1f, 0.4f),
				new Color(1f, 1f, 1f, 0f),
			},
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

	// Exhaust mote: born bright at the nozzle, holds a moment, then fades to nothing.
	private static GradientTexture1D ExhaustRamp()
	{
		var gradient = new Gradient
		{
			Offsets = new[] { 0f, 0.25f, 1f },
			Colors = new[]
			{
				new Color(1f, 1f, 1f, 1f),
				new Color(1f, 1f, 1f, 0.7f),
				new Color(1f, 1f, 1f, 0f),
			},
		};
		return new GradientTexture1D { Gradient = gradient, Width = 64 };
	}

	// Per-particle scale over lifetime: full size at birth, shrinking to a wisp as
	// it trails away. Paired with the larger ScaleMin/Max so motes start big.
	private static CurveTexture ShrinkCurve()
	{
		var curve = new Curve();
		curve.AddPoint(new Vector2(0f, 1f));
		curve.AddPoint(new Vector2(1f, 0.2f));
		return new CurveTexture { Curve = curve };
	}

	private static StandardMaterial3D ExhaustDrawMaterial(Texture2D dot) => new()
	{
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		BlendMode = BaseMaterial3D.BlendModeEnum.Add,
		AlbedoTexture = dot,
		EmissionEnabled = true,
		EmissionTexture = dot,
		Emission = Colors.White,
		EmissionEnergyMultiplier = 1.6f,
		BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
		VertexColorUseAsAlbedo = true,   // honour the per-particle ColorRamp alpha
	};
}
