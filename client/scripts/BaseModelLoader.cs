using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// =====================================================================
//  BaseModelLoader.cs — CLIENT BASE MESH + HARDPOINT LOADER (Phase-1 M5)
//
//  The base mirror of ShipModelLoader (M4): builds a base's visual node from the
//  runtime BaseDef instead of the hard-coded WorldRenderer sphere/floats.
//    - Build(): the existing procedural sphere placeholder (sized from BaseDef.Radius)
//      PLUS a Marker3D child per HardpointDef, named HP_<Kind>_<Index>, at the def's
//      local offset/forward — DockingEntrance/DockingExit are exposed as positioned
//      markers only (docking/spawn-exit LOGIC is a later phase; this phase carries the
//      data and visualizes it).
//    - A simple blinking beacon at every Light hardpoint (the roadmap's "lighting
//      (blinking)"): a team-tinted emissive mote + OmniLight that pulses on/off, each
//      with its own phase so the nav lights don't strobe in lockstep.
//
//  Everything spatial now flows from DefRegistry (the subscribed BaseDef), so an
//  operator's runtime UpsertBaseDef that moves a dock or a light is reflected on the
//  next base insert with NO client rebuild. Radius falls back to a placeholder default
//  only if the def hasn't arrived yet (defs ship in the initial subscription snapshot,
//  before any base; the fallback mirrors the server's BaseRadiusFor).
//
//  FUTURE GLB CONVENTION (same as the ship loader): when a base `.glb` exists, the
//  loader should load it in place of the sphere and read its same-named HP_<Kind>_<Index>
//  nodes to OVERRIDE these procedural markers. The data contract — node name =
//  HP_<Kind>_<Index>, local +Z = the hardpoint forward — is identical either way.
// =====================================================================
public static class BaseModelLoader
{
	// The placeholder sphere radius used until a BaseDef arrives (mirror of the server's
	// BaseRadiusFor fallback and the WorldRenderer constant it replaces).
	public const float FallbackRadius = 45f;

	// Build the base's mesh node: a procedural sphere sized to the type's def, plus a HP_
	// marker child per hardpoint and a blinking beacon at each Light. `team` tints the
	// beacons so friend/foe still reads; `mat` is the team material the caller resolved.
	public static MeshInstance3D Build(DefRegistry defs, byte baseTypeId, byte team, Material mat)
	{
		BaseDef? def = defs.GetBaseDef(baseTypeId);
		float radius = def?.Radius ?? FallbackRadius;

		var mesh = new MeshInstance3D
		{
			Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = 32, Rings = 16 },
			MaterialOverride = mat,
		};

		// Markers (and beacons) are children of the mesh node, so a future .glb that
		// replaces the sphere carries its own HP_ nodes in the same local frame.
		if (def?.Hardpoints != null)
			foreach (HardpointDef hp in def.Hardpoints)
			{
				mesh.AddChild(MakeMarker(hp));
				if (hp.Kind == HardpointKind.Light)
					mesh.AddChild(MakeBeacon(hp, team));
			}

		return mesh;
	}

	// The radius a base of this type renders at — the def's Radius, or the placeholder
	// fallback until the row arrives. WorldRenderer uses it to anchor the floating health
	// bar above the (def-sized) sphere.
	public static float Radius(DefRegistry defs, byte baseTypeId)
		=> defs.GetBaseDef(baseTypeId)?.Radius ?? FallbackRadius;

	// A positioned, oriented marker for one hardpoint: local position from the def's
	// offset, local +Z aligned with the def's forward (the same convention the ship loader
	// and the weapon/muzzle code use).
	private static Marker3D MakeMarker(HardpointDef hp)
	{
		var pos = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
		Basis basis = BasisFacingZ(new Vector3(hp.DirX, hp.DirY, hp.DirZ));
		return new Marker3D
		{
			Name = $"HP_{hp.Kind}_{hp.Index}",
			Transform = new Transform3D(basis, pos),
		};
	}

	// A blinking nav beacon parked at a Light hardpoint's offset, tinted toward the base's
	// team hue. The node owns its blink phase so multiple lights pulse out of lockstep.
	private static BaseBeacon MakeBeacon(HardpointDef hp, byte team)
		=> new BaseBeacon
		{
			Name = $"Beacon_{hp.Index}",
			Position = new Vector3(hp.OffX, hp.OffY, hp.OffZ),
			Color = team == 0 ? new Color(0.45f, 0.7f, 1f) : new Color(1f, 0.55f, 0.35f),
		};

	// Orthonormal basis whose local +Z points along `forward` (game-forward). Falls back to
	// identity for a near-zero direction, and swaps the up reference when forward is nearly
	// parallel to world up so the cross product stays well-conditioned. (Mirror of the ship
	// loader's helper — the two loaders are deliberately independent parallel files.)
	private static Basis BasisFacingZ(Vector3 forward)
	{
		if (forward.LengthSquared() < 1e-8f)
			return Basis.Identity;
		Vector3 z = forward.Normalized();
		Vector3 upRef = Mathf.Abs(z.Dot(Vector3.Up)) > 0.999f ? Vector3.Right : Vector3.Up;
		Vector3 x = upRef.Cross(z).Normalized();
		Vector3 y = z.Cross(x);
		return new Basis(x, y, z);
	}
}

// A simple blinking nav beacon: an emissive billboard mote (drives the bloom pipeline) plus
// a short-range OmniLight, both pulsed on/off on a fixed period. Built procedurally in C#
// like the rest of the world visuals (Sun/EngineGlow), so there's no scene asset to keep in
// sync. Purely cosmetic — the blink is wall-clock, not the sim clock.
public partial class BaseBeacon : Node3D
{
	// Team-tinted beacon colour, set by the loader before AddChild.
	public Color Color = new(0.45f, 0.7f, 1f);

	private const float Period = 1.6f;       // full blink cycle (s)
	private const float OnFraction = 0.25f;  // share of the cycle the light is lit
	private const float Range = 12f;         // OmniLight reach when lit
	private const float MoteSize = 2.4f;     // billboard mote diameter (world units)

	private OmniLight3D _light = null!;
	private StandardMaterial3D _moteMat = null!;
	private float _phase;   // per-instance offset so beacons don't blink in lockstep

	public override void _Ready()
	{
		_phase = GD.Randf() * Period;

		_moteMat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BlendMode = BaseMaterial3D.BlendModeEnum.Add,
			AlbedoTexture = RadialDot(),
			EmissionEnabled = true,
			EmissionTexture = RadialDot(),
			Emission = Color,
			EmissionEnergyMultiplier = 4f,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
			BillboardKeepScale = true,
		};
		AddChild(new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(MoteSize, MoteSize) },
			MaterialOverride = _moteMat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		_light = new OmniLight3D
		{
			OmniRange = Range,
			LightColor = Color,
			LightEnergy = 0f,
			ShadowEnabled = false,
		};
		AddChild(_light);

		ApplyBlink(0f);   // start dark; _Process lights it
	}

	public override void _Process(double delta)
	{
		_phase += (float)delta;
		// A soft on pulse: a half-sine bump during the lit window, dark the rest of the
		// cycle, so it reads as a pulsing nav light rather than a hard strobe.
		float t = Mathf.PosMod(_phase, Period) / Period;
		float lit = t < OnFraction ? Mathf.Sin(t / OnFraction * Mathf.Pi) : 0f;
		ApplyBlink(lit);
	}

	private void ApplyBlink(float lit)
	{
		_moteMat.EmissionEnergyMultiplier = 1f + 6f * lit;
		_moteMat.AlbedoColor = new Color(Color.R, Color.G, Color.B, lit);
		_light.LightEnergy = 3f * lit;
	}

	// Soft round mote: hot centre fading to transparent — drives bloom via emission energy.
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
			Width = 64,
			Height = 64,
			Fill = GradientTexture2D.FillEnum.Radial,
			FillFrom = new Vector2(0.5f, 0.5f),
			FillTo = new Vector2(0.5f, 0f),
		};
	}
}
