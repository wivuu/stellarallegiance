using Godot;

// =====================================================================
//  CloakFx.cs — THE CLOAK SHIMMER (equipment PR)
//
//  How a cloaking ship looks, to EVERY viewer that receives it (fog decides whether an enemy is
//  streamed at all — this is only the look), driven by the cloaked fraction of its signature
//  (ShipResources.CloakFraction of the pools' u16 level):
//    - the whole external model under "ShipModel" — hull, exhaust plume + smoke, team trail, nav
//      beacon motes, turret barrels — goes translucent (GeometryInstance3D.Transparency, the
//      per-instance fade NodeFx.DimNode uses, which spans a GLB's baked materials without touching
//      them), capped by the caller so a hull never vanishes outright: the pilot's own chase cam
//      must still read it (OwnMaxTransparency), everyone else sees it ghost further;
//    - the HULL meshes get an additive, unshaded fresnel rim with slow drifting bands as a
//      MaterialOverlay, tinted with the ship's FACTION colour (DesignTokens.Faction — team
//      identity, never the cyan chrome accent), so a cloaked hull reads as bent light, not a
//      faded one;
//    - nav beacons dim with it (their blink drives its own light every frame, so it is the
//      beacon's Intensity knob that scales); EngineGlow.SetCloak dims the engine-wash light.
//  Level 0 puts everything back: opaque, no overlay pass, beacons at their authored intensity.
//
//  ONE compiled shader and ONE overlay material shared by every ship (AssetPreloader warms both,
//  ShieldFlash's pattern): the level and the faction tint are `instance uniform`s set per hull mesh
//  (GeometryInstance3D.SetInstanceShaderParameter), so two ships on the same material cloak
//  independently. Verified on Godot 4.7.2 Forward+ (Metal): an instance uniform declared by a
//  MaterialOverlay's shader gets its own per-instance slot, and two meshes sharing the overlay
//  render with their own values. Callers apply only when the shown level moves (Transparency forces
//  the alpha pipeline — a translucent hull may show a few inner faces).
// =====================================================================
public static class CloakFx
{
    // Transparency at a FULL (1.0) cloaked fraction: the own ship stays readable to its chase cam,
    // other viewers see it ghost further. A part's MaxCloaking scales both (Sig Cloak 1 = 0.625).
    public const float OwnMaxTransparency = 0.75f;
    public const float OtherMaxTransparency = 0.85f;

    private const string BeaconBaseMeta = "cloak_base_intensity"; // a beacon's authored Intensity

    // Fresnel rim + drifting interference bands, additive and unshaded: dark (zero contribution)
    // across the hull's face, lit along the silhouette, all scaled by `cloak` so level 0 draws
    // nothing (the overlay is also removed then). World-space bands so every hull's scale reads alike.
    private const string ShaderCode = """
        shader_type spatial;
        render_mode unshaded, blend_add, depth_draw_never, cull_back, shadows_disabled;

        instance uniform vec4 tint : source_color = vec4(1.0);
        instance uniform float cloak = 0.0; // cloaked fraction of the signature, 0..1

        varying vec3 world_pos;

        void vertex() {
            world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        }

        void fragment() {
            float facing = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
            float rim = pow(1.0 - facing, 2.5);                      // silhouette edge
            float band = 0.5 + 0.5 * sin(world_pos.y * 2.2 + world_pos.x * 0.9 - TIME * 3.0);
            float glow = rim * (0.55 + 0.45 * band) + 0.06 * band;   // the rim crawls, the face barely shimmers
            ALBEDO = tint.rgb;
            ALPHA = clamp(cloak * glow * 1.4, 0.0, 1.0);
        }
        """;

    private static readonly Shader SharedShader = new() { Code = ShaderCode };
    private static readonly ShaderMaterial SharedOverlay = new() { Shader = SharedShader };

    internal static void WarmShaders() => _ = SharedOverlay;

    // Show `shipModel` (the ship's "ShipModel" container) at cloaked fraction `level` (0 = fully
    // visible), with `maxTransparency` the transparency a full cloak would reach and `tint` the
    // ship's faction colour. Idempotent; cheap enough to re-apply every half second while cloaked.
    public static void Apply(Node3D shipModel, float level, float maxTransparency, Color tint)
    {
        level = Mathf.Clamp(level, 0f, 1f);
        NodeFx.DimNode(shipModel, level * maxTransparency);
        foreach (var child in shipModel.GetChildren())
        {
            if (child is Node3D hull && hull.HasMeta(ShipModelLoader.HullMeta))
                SetOverlay(hull, level, tint);
            DimBeacons(child, level);
        }
    }

    // The overlay goes on the hull's own meshes only — never the additive plume, the trail ribbon or
    // a beacon mote, which would each grow a rim of their own. Removed outright at level 0, so an
    // uncloaked hull pays for no extra pass.
    private static void SetOverlay(Node node, float level, Color tint)
    {
        if (node is MeshInstance3D mi)
        {
            if (level > 0f)
            {
                mi.MaterialOverlay = SharedOverlay;
                mi.SetInstanceShaderParameter("cloak", level);
                mi.SetInstanceShaderParameter("tint", tint);
            }
            else
                mi.MaterialOverlay = null;
        }
        foreach (var child in node.GetChildren())
            SetOverlay(child, level, tint);
    }

    private static void DimBeacons(Node node, float level)
    {
        if (node is BaseBeacon beacon)
        {
            if (!beacon.HasMeta(BeaconBaseMeta))
                beacon.SetMeta(BeaconBaseMeta, beacon.Intensity);
            beacon.Intensity = beacon.GetMeta(BeaconBaseMeta).AsSingle() * (1f - level);
            return;
        }
        foreach (var child in node.GetChildren())
            DimBeacons(child, level);
    }
}
