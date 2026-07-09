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
//      with its own phase so the nav lights don't strobe in lockstep. Beacons follow the
//      hull's own HP_Light_* nodes when the authored base.glb carries them, falling back to
//      the def-seeded Light offsets only for the procedural sphere placeholder.
//
//  Everything spatial now flows from DefRegistry (the subscribed BaseDef), so an
//  operator's runtime UpsertBaseDef that moves a dock or a light is reflected on the
//  next base insert with NO client rebuild. Radius falls back to a placeholder default
//  only if the def hasn't arrived yet (defs ship in the initial subscription snapshot,
//  before any base; the fallback mirrors the server's BaseRadiusFor).
//
//  GLB CONVENTION (now wired, same as the ship loader; see docs/GLB-AND-HARDPOINT-FORMAT.md
//  §4): when `res://assets/bases/base.glb` exists it is loaded in place of the sphere,
//  uniform-scaled (via GlbLoader) to the def radius, keeping its own baked PBR materials
//  (friend/foe is read from the HUD, not a hull tint). Any HP_<Kind>_<Index> node the glb
//  author placed in-mesh OVERRIDES the equivalent procedural marker, and every HP_Light_* node
//  gets its own blinking beacon at the authored position. The data contract — node name =
//  HP_<Kind>_<Index>, local +Z = the hardpoint forward — is identical either way, so the
//  markers/beacons code keeps working unchanged.
// =====================================================================
public static class BaseModelLoader
{
    // The placeholder sphere radius used until a BaseDef arrives. Single-sourced from the
    // shared collision config (CollisionConfig.BaseRadius) rather than a hand-mirrored
    // literal, so this can't drift from the server's own base-radius fallback.
    public const float FallbackRadius = CollisionConfig.BaseRadius;

    // DEBUG: render a faint cone at each docking hardpoint so the dock geometry is visible against the
    // rendered hull. The server reads these same GLB nodes and treats each green cone's BASE DISC
    // (radius DebugConeRadius, at the hardpoint, facing outward) as the only place a ship can dock —
    // fly your ship into a green disc to dock; the rest of the base is a solid hull. Flip to false to
    // hide. Entry = green, exit = magenta; each cone points radially outward from the base center.
    // DebugConeRadius MUST match the server's World.DockDiscRadius.
    public const bool ShowHardpointDebug = false;
    private const float DebugConeRadius = 9f;
    private const float DebugConeHeight = 34f;

    // --- Nav-light palette + base-beacon sizing (shared source; ShipModelLoader references these) ---
    // Universal position-light colours — not team-tinted, so a green/red light reads the same on any
    // hull the way real aircraft/runway lights do (friend/foe still reads from the hull HUD tint).
    // Bases colour a light by its nearest dock feature; ships colour by wing side.
    public static readonly Color NavGreen = new(0.15f, 1f, 0.35f); // starboard / docking entrance
    public static readonly Color NavRed = new(1f, 0.18f, 0.18f); // port / docking exit
    public static readonly Color NavWhite = new(1f, 0.96f, 0.9f); // everything else (slightly warm)
    // Base beacon size (world units). Ships scale their own down off the hull length.
    public const float BeaconMoteSize = 0.9f; // billboard mote diameter
    public const float BeaconRange = 4.5f; // OmniLight reach when lit

    // Build the base's model node: the authored `base.glb` hull if one is present (else a
    // procedural sphere sized to the type's def), plus a HP_ marker per hardpoint and a blinking
    // beacon at each Light. `team` tints both the hull and the beacons so friend/foe still reads;
    // `mat` is the team material the caller resolved.
    //
    // Like the ship loader, the returned node is a container in the base's UNSCALED local frame:
    // the visual hull is a child that may be independently scaled, while the markers and beacons
    // stay on the container at the def's true world-unit offsets (the Light beacons sit on the
    // sphere/hull surface at ±radius).
    public static Node3D Build(DefRegistry defs, byte baseTypeId, byte team, Material mat)
    {
        BaseDef? def = defs.GetBaseDef(baseTypeId);
        float radius = def?.Radius ?? FallbackRadius;

        var root = new Node3D { Name = "BaseModel" };
        Node3D hull = LoadHull(radius) ?? BuildPlaceholderSphere(radius, mat);
        root.AddChild(hull);

        if (def?.Hardpoints != null)
            foreach (HardpointDef hp in def.Hardpoints)
                // A GLB-authored HP_ node overrides the procedural marker; otherwise it stands.
                if (!GlbLoader.HasNode(hull, $"HP_{hp.Kind}_{hp.Index}"))
                    root.AddChild(MakeMarker(hp));

        // A blinking beacon at every Light hardpoint. Prefer the hull's own HP_Light_* nodes (an
        // authored mesh places far more lights, at meaningful hull positions, than the def's
        // placeholder pair) and fall back to the def-seeded Lights only when the hull carries none
        // (e.g. the procedural sphere). The GLB node lives inside the uniform-scaled hull, so
        // hull.Transform maps its local offset into the unscaled BaseModel root the beacons sit on.
        // Each light is coloured by the docking feature it marks: green next to a DockingEntrance,
        // red next to a DockingExit, white otherwise (generalises to any base layout — no
        // per-model hardcoding). "Nearest" is measured in the horizontal footprint (X/Z, ignoring
        // the +Y up-axis that HP_DockingExit points along), because a base stacks each dock's
        // lights in a vertical column sharing that dock's X/Z channel — full-3D distance would let
        // a low entrance node bleed into a taller exit column. Cutoff keeps stray hull lights white.
        float dockCutoff = radius * 0.5f;
        var glbLights = GlbLoader.FindHardpoints(hull, $"HP_{HardpointKind.Light}_");
        if (glbLights.Count > 0)
        {
            var entrances = DockPositions(hull, HardpointKind.DockingEntrance);
            var exits = DockPositions(hull, HardpointKind.DockingExit);
            int i = 0;
            foreach ((string _, Transform3D local) in glbLights)
            {
                Vector3 pos = (hull.Transform * local).Origin;
                root.AddChild(MakeBeacon(pos, DockLightColor(pos, entrances, exits, dockCutoff), i++));
            }
        }
        else if (def?.Hardpoints != null)
        {
            var entrances = DefDockPositions(def.Hardpoints, HardpointKind.DockingEntrance);
            var exits = DefDockPositions(def.Hardpoints, HardpointKind.DockingExit);
            int i = 0;
            foreach (HardpointDef hp in def.Hardpoints)
                if (hp.Kind == HardpointKind.Light)
                {
                    var pos = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
                    root.AddChild(MakeBeacon(pos, DockLightColor(pos, entrances, exits, dockCutoff), i++));
                }
        }

        // // DEBUG visualization of the docking hardpoints (entry = green, exit = magenta). Read from
        // // the hull's own HP_ nodes — the exact positions the server scales by the same world scale
        // // Each green cone's base disc IS the dock zone, so this shows exactly where you must fly to
        // // dock. hull.Transform maps the GLB-local node into the unscaled
        // // BaseModel root the cones sit on, same as the beacons above.
        // if (ShowHardpointDebug)
        // {
        // 	foreach ((string name, Transform3D local) in GlbLoader.FindHardpoints(hull, "HP_DockingEntrance"))
        // 		root.AddChild(MakeHardpointCone((hull.Transform * local).Origin, new Color(0.2f, 1f, 0.35f), name));
        // 	foreach ((string name, Transform3D local) in GlbLoader.FindHardpoints(hull, "HP_DockingExit"))
        // 		root.AddChild(MakeHardpointCone((hull.Transform * local).Origin, new Color(1f, 0.25f, 0.95f), name));
        // }

        return root;
    }

    // Load `res://assets/bases/base.glb` and ready it as a hull: scaled so its longest axis spans
    // the def diameter (it visually replaces the radius-sized sphere), keeping the GLB's own baked
    // PBR materials (friend/foe reads from the HUD, not a hull tint). Null when no asset exists,
    // so Build falls back to the procedural sphere.
    private static Node3D? LoadHull(float radius)
    {
        Node3D? hull = GlbLoader.Load("res://assets/bases/base.glb");
        if (hull == null)
            return null;
        GlbLoader.NormalizeLongestAxis(hull, radius * 2f);
        return hull;
    }

    // The procedural sphere placeholder, sized to the type's def radius. Used until a base.glb
    // is present (and as the fallback if it fails to load).
    private static MeshInstance3D BuildPlaceholderSphere(float radius, Material mat) =>
        new()
        {
            Mesh = new SphereMesh
            {
                Radius = radius,
                Height = radius * 2f,
                RadialSegments = 32,
                Rings = 16,
            },
            MaterialOverride = mat,
        };

    // The radius a base of this type renders at — the def's Radius, or the placeholder
    // fallback until the row arrives. WorldRenderer uses it to anchor the floating health
    // bar above the (def-sized) sphere.
    public static float Radius(DefRegistry defs, byte baseTypeId) => defs.GetBaseDef(baseTypeId)?.Radius ?? FallbackRadius;

    // A positioned, oriented marker for one hardpoint: local position from the def's
    // offset, local +Z aligned with the def's forward (the same convention the ship loader
    // and the weapon/muzzle code use).
    private static Marker3D MakeMarker(HardpointDef hp)
    {
        var pos = new Vector3(hp.OffX, hp.OffY, hp.OffZ);
        Basis basis = BasisFacingZ(new Vector3(hp.DirX, hp.DirY, hp.DirZ));
        return new Marker3D { Name = $"HP_{hp.Kind}_{hp.Index}", Transform = new Transform3D(basis, pos) };
    }

    // DEBUG cone parked at a docking hardpoint, pointing radially outward from the base center (the
    // server's exitDir/entryAxis convention). Bright, unshaded, double-sided and shadow-free so it
    // reads clearly from any angle while troubleshooting the dock geometry. Position is in the
    // unscaled BaseModel root frame (true world units), matching MakeMarker/MakeBeacon.
    private static Node3D MakeHardpointCone(Vector3 pos, Color color, string name)
    {
        Vector3 outward = pos.LengthSquared() > 1e-6f ? pos.Normalized() : Vector3.Forward;
        var node = new Node3D { Name = $"DebugHP_{name}", Transform = new Transform3D(BasisFacingZ(outward), pos) };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(color.R, color.G, color.B, 0.1f), // 90% transparent
            EmissionEnabled = true,
            Emission = color,
            EmissionEnergyMultiplier = 0.6f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var cone = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 0f,
                BottomRadius = DebugConeRadius,
                Height = DebugConeHeight,
                RadialSegments = 16,
            },
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // CylinderMesh runs along +Y; rotate so its tip points along the node's +Z (outward),
            // then push it out so the base sits at the hardpoint and the tip points away from the hull.
            Transform = new Transform3D(Basis.Identity, Vector3.Zero)
                .RotatedLocal(Vector3.Right, Mathf.Pi / 2f)
                .Translated(new Vector3(0f, 0f, DebugConeHeight * 0.5f)),
        };
        node.AddChild(cone);
        return node;
    }

    // A blinking nav beacon parked at a Light hardpoint's local position, in the given nav colour.
    // The node owns its blink phase so multiple lights pulse out of lockstep.
    private static BaseBeacon MakeBeacon(Vector3 pos, Color color, int index) =>
        new BaseBeacon
        {
            Name = $"Beacon_{index}",
            Position = pos,
            Color = color,
        };

    // GLB dock-hardpoint positions in the unscaled BaseModel root frame (same mapping the beacons
    // use: hull.Transform maps a scaled GLB-local node into the root the beacons sit on).
    private static System.Collections.Generic.List<Vector3> DockPositions(Node3D hull, HardpointKind kind)
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach ((string _, Transform3D local) in GlbLoader.FindHardpoints(hull, $"HP_{kind}_"))
            list.Add((hull.Transform * local).Origin);
        return list;
    }

    // Def-seeded dock positions (procedural-sphere fallback path, where there is no GLB tree).
    private static System.Collections.Generic.List<Vector3> DefDockPositions(
        System.Collections.Generic.IEnumerable<HardpointDef> hardpoints,
        HardpointKind kind
    )
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == kind)
                list.Add(new Vector3(hp.OffX, hp.OffY, hp.OffZ));
        return list;
    }

    // Colour a light by the dock feature it marks: NavGreen next to an entrance, NavRed next to an
    // exit, NavWhite when no dock is within `cutoff`. Distance is the horizontal-footprint (X/Z)
    // distance — see the beacon block's comment for why the +Y vertical component is dropped.
    private static Color DockLightColor(
        Vector3 pos,
        System.Collections.Generic.List<Vector3> entrances,
        System.Collections.Generic.List<Vector3> exits,
        float cutoff
    )
    {
        float dEntrance = NearestFootprint(pos, entrances);
        float dExit = NearestFootprint(pos, exits);
        float nearest = Mathf.Min(dEntrance, dExit);
        if (nearest > cutoff)
            return NavWhite;
        return dEntrance <= dExit ? NavGreen : NavRed;
    }

    // Smallest horizontal-footprint (X/Z, ignoring Y) distance from `pos` to any point in `pts`,
    // or +inf when the list is empty.
    private static float NearestFootprint(Vector3 pos, System.Collections.Generic.List<Vector3> pts)
    {
        float best = float.PositiveInfinity;
        foreach (Vector3 p in pts)
        {
            float dx = pos.X - p.X,
                dz = pos.Z - p.Z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < best)
                best = d;
        }
        return best;
    }

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
    // Beacon colour, set by the loader before AddChild. (Nav palette lives on BaseModelLoader.)
    public Color Color = new(0.45f, 0.7f, 1f);

    // Cosmetic sizing knobs, promoted to public fields so callers can scale a beacon to a
    // ship-scale hull (ShipModelLoader) while the base loader's own beacons keep these exact
    // defaults — base visuals are unchanged unless a caller opts in by setting them.
    public float MoteSize = BaseModelLoader.BeaconMoteSize; // billboard mote diameter (world units)
    public float Range = BaseModelLoader.BeaconRange; // OmniLight reach when lit
    // Multiplies both the mote's emission energy and the OmniLight's lit energy — a single
    // brightness knob for callers that want a dimmer/brighter blink than the base default.
    public float Intensity = 1f;

    private const float Period = 1.6f; // full blink cycle (s)
    private const float OnFraction = 0.25f; // share of the cycle the light is lit

    private OmniLight3D _light = null!;
    private StandardMaterial3D _moteMat = null!;
    private float _phase; // per-instance offset so beacons don't blink in lockstep

    public override void _Ready()
    {
        _phase = GD.Randf() * Period;

        var dot = RadialDot();
        _moteMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoTexture = dot,
            EmissionEnabled = true,
            EmissionTexture = dot,
            Emission = Color,
            EmissionEnergyMultiplier = 4f,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            BillboardKeepScale = true,
        };
        AddChild(
            new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(MoteSize, MoteSize) },
                MaterialOverride = _moteMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            }
        );

        _light = new OmniLight3D
        {
            OmniRange = Range,
            LightColor = Color,
            LightEnergy = 0f,
            ShadowEnabled = false,
        };
        AddChild(_light);

        ApplyBlink(0f); // start dark; _Process lights it
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
        _moteMat.EmissionEnergyMultiplier = (1f + 6f * lit) * Intensity;
        _moteMat.AlbedoColor = new Color(Color.R, Color.G, Color.B, lit);
        _light.LightEnergy = 3f * lit * Intensity;
    }

    // Soft round mote: hot centre fading to transparent — drives bloom via emission energy.
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
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }
}
