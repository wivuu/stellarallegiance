namespace StellarAllegiance.Shared;

// =====================================================================
//  SimModel.cs — convex hull + hardpoints derived from one GLB, in the GLB's AUTHORED units
//  so a single cached hull serves every instance (a base bakes one world scale; an asteroid
//  variant is scaled per rock). Engine-agnostic (Vec3 only) so both the server and the client
//  build/consume it. The server's disk cache (SimModelCache, SHA256 .simmodel sidecars) wraps
//  this in server/Assets/; the client builds a SimModel in-memory from its res:// GLB bytes.
// =====================================================================
public sealed class SimModel
{
    public ConvexHull Hull { get; }
    public IReadOnlyList<(string Name, Vec3 Pos, Vec3 Forward)> Hardpoints { get; }

    // Per-part convex hulls for a COMPOUND body (one ConvexHull.Build per authored COL_ part), so a
    // ship can bounce off the actual concave superstructure instead of the merged shrink-wrap. When
    // the GLB carries no COL_ parts (every ship/asteroid/un-baked model) this is a SINGLE-element list
    // aliasing the SAME `Hull` object ⇒ bit-for-bit zero behaviour change for those callers.
    //
    // `Hull` stays the merged hull over the FULL cloud — metrics (LongestAxis/BoundingRadius),
    // broadphase, and spawn-clearance checks keep using it unchanged.
    public IReadOnlyList<ConvexHull> Hulls { get; }

    public float BoundingRadius => Hull.BoundingRadius;
    public float LongestAxis => Hull.LongestAxis;

    // `parts` is optional so existing callers (server SimModelCache, which reconstructs from cached
    // planes and has no part data) compile untouched — they get the single-hull aliasing path. B3
    // adds a cache v2 that persists parts; until then a partless SimModel is exactly today's model.
    public SimModel(
        ConvexHull hull,
        IReadOnlyList<(string, Vec3, Vec3)> hardpoints,
        IReadOnlyList<(string Name, List<Vec3> Verts)>? parts = null
    )
    {
        Hull = hull;
        Hardpoints = hardpoints;
        if (parts is not null && parts.Count > 0)
        {
            var hulls = new ConvexHull[parts.Count];
            for (int i = 0; i < parts.Count; i++)
                hulls[i] = ConvexHull.Build(parts[i].Verts);
            Hulls = hulls;
        }
        else
        {
            Hulls = new[] { hull }; // alias the merged hull — no separate geometry, no drift
        }
    }

    // Prebuilt-hulls ctor (private; reached via FromPrebuilt). The `subHulls` are ALREADY-BUILT
    // ConvexHulls — no QuickHull is run — because the ONLY caller is the server disk cache, which
    // deserializes stored PLANES via ConvexHull.FromPlanes and must not re-hull. The public vertex
    // ctor above is the authoring path (GLB verts → ConvexHull.Build); this is the cache path.
    private SimModel(ConvexHull hull, IReadOnlyList<(string, Vec3, Vec3)> hardpoints, IReadOnlyList<ConvexHull> subHulls)
    {
        Hull = hull;
        Hardpoints = hardpoints;
        Hulls = subHulls;
    }

    // First hardpoint whose name starts with "HP_<kind>" (e.g. "HP_DockingExit"); null if none.
    public (string Name, Vec3 Pos, Vec3 Forward)? FirstHardpoint(string kindPrefix)
    {
        foreach (var hp in Hardpoints)
            if (hp.Name.StartsWith(kindPrefix, StringComparison.Ordinal))
                return hp;
        return null;
    }

    // Build a SimModel straight from GLB bytes (the client path: no disk cache).
    public static SimModel FromGlb(byte[] glbBytes, string label = "<glb>")
    {
        var glb = GlbReader.Parse(glbBytes, label);
        return new SimModel(ConvexHull.Build(glb.Vertices), glb.Hardpoints, glb.CollisionParts);
    }

    // Reconstruct a SimModel from ALREADY-BUILT hulls — EXISTS SOLELY for the server disk cache
    // (SimModelCache) cache-v2 read path: the cache persists hull PLANES, so it rebuilds each hull
    // via ConvexHull.FromPlanes and must NOT re-run QuickHull. `subHulls` is the per-part list; a
    // null/empty list (a partless sidecar) aliases the merged hull, bit-for-bit the single-hull path
    // the vertex ctor produces. Kept minimal on purpose — the authoring path stays the vertex ctor.
    public static SimModel FromPrebuilt(
        ConvexHull merged,
        IReadOnlyList<(string, Vec3, Vec3)> hardpoints,
        IReadOnlyList<ConvexHull>? subHulls
    ) => new(merged, hardpoints, subHulls is { Count: > 0 } ? subHulls : new[] { merged });
}
