using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;

// The per-sector shadow-occluder system: picks the bases + camera-near rocks that cast spin-tracking shadow
// VOLUMES into the sector dust, caches their LOCAL-frame hull silhouettes, and drives the SectorEnvironment
// sun/dust/shadow renderer. The coordinator's ApplySectorEnv seeds a sector here (sun/dust + initial
// occluders) and _Process re-gathers as the camera moves. A plain class (uses Godot math/mesh types, not a
// Node). WarmAsteroidVariant is static so AssetPreloader can warm the mesh-hull cache during the splash.
public sealed class EnvironmentRenderer
{
    private readonly SectorEnvironment? _sectorEnv; // sibling; drives per-sector sun + 3D dust clouds
    private readonly BaseRenderer _bases;
    private readonly AsteroidRenderer _rocks;

    public EnvironmentRenderer(SectorEnvironment? sectorEnv, BaseRenderer bases, AsteroidRenderer rocks)
    {
        _sectorEnv = sectorEnv;
        _bases = bases;
        _rocks = rocks;
    }

    // Shadow-casting occluders are chosen by CAMERA DISTANCE, not a flat count: every base in the sector
    // plus the rocks near the camera cast a spin-tracking shadow volume into the dust. The set is
    // re-evaluated as the camera moves (throttled by OccluderRegatherStep). A big rock reaches from farther
    // (its shadow is larger); a generous nearest-N backstop keeps a dense belt from building a thicket.
    private const float ShadowOccluderRadius = 2500f; // base camera-distance cut for a rock to cast (world units)
    private const float OccluderRegatherStep = 150f; // re-select the occluder set only after the camera moves this far
    private const int MaxShadowOccluders = 64; // safety backstop: keep at most the NEAREST this many in range
    private readonly List<(Node3D Node, float D)> _occluderScratch = new(); // D = distance² to camera (bases sort first)
    private readonly List<(Node3D Node, Vector3[] LocalVerts)> _sectorEnvOccluders = new();
    private readonly Dictionary<Node3D, Vector3[]> _hullVertCache = new(); // per-node LOCAL hull verts (base hierarchies), built once

    // Lone-mesh occluders (rocks): the collected local verts collapse to RAW MESH vertices (the root's own
    // transform cancels out), identical for every instance sharing a variant Mesh — so the cache keys on the
    // Mesh, not the node. Keyed per node, spawning into a 60-rock sector re-read the same handful of giant
    // asteroid meshes ~10x each (~1s of SurfaceGetArrays + Extremes on the spawn frame). STATIC so
    // AssetPreloader can warm it per variant at startup.
    private static readonly Dictionary<Mesh, Vector3[]> _meshHullVertCache = new();
    private Vector3 _lastOccluderCamPos = new(float.MaxValue, float.MaxValue, float.MaxValue);

    // Whether the current sector casts shadow volumes (has a sun + dust). The coordinator guards its
    // per-frame re-gather on this so a sunless sector doesn't even compute a camera reference position.
    public bool CastsShadows => _sectorEnv is { CastsSectorShadows: true };

    // Seed a sector: anchor the move-throttle at `refPos` and drive the sun + dust + initial shadow-volume
    // set. Mirrors the coordinator's old ApplySectorEnv body (minus the Starscape backdrop, which stays).
    public void ApplySector(uint sector, SectorEnv? env, Vector3 refPos)
    {
        _lastOccluderCamPos = refPos;
        _sectorEnv?.Apply(sector, env, GatherShadowOccluders(sector, refPos));
    }

    // Camera-distance occluder re-scan, throttled to when the camera has actually moved a meaningful step.
    // Sun + dust are static per sector, so this refreshes ONLY the shadow-volume set (SectorEnvironment
    // builds/frees just the delta). Gated on the sector actually casting shadows so sunless sectors idle.
    public void Tick(Vector3 refPos, uint viewSector)
    {
        if (_sectorEnv is not { CastsSectorShadows: true })
            return;
        if (refPos.DistanceSquaredTo(_lastOccluderCamPos) < OccluderRegatherStep * OccluderRegatherStep)
            return;
        _lastOccluderCamPos = refPos;
        _sectorEnv.UpdateOccluders(GatherShadowOccluders(viewSector, refPos));
    }

    // World teardown (WorldRenderer.Reset): drop the sector-env cache so the fresh Welcome rebuilds the
    // shadow volumes even for the same sector id (they parent to rock nodes Reset frees).
    public void Invalidate() => _sectorEnv?.Invalidate();

    // World teardown: clear the per-node hull cache (keyed by the freed rock nodes) + reset the throttle.
    public void ClearCaches()
    {
        _hullVertCache.Clear();
        _lastOccluderCamPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
    }

    // The shadow-casting occluders for `sector` given a camera/reference position: every base in the sector
    // (few, always worth a shadow) plus the nearest rocks within ShadowOccluderRadius (extended by each
    // rock's own radius so large rocks reach farther). Each is (its node, its LOCAL-frame hull vertices) for
    // SectorEnvironment to bake a spin-tracking shadow volume parented to the node. Nearest-first, backstopped.
    private IReadOnlyList<(Node3D Node, Vector3[] LocalVerts)> GatherShadowOccluders(uint sector, Vector3 refPos)
    {
        _occluderScratch.Clear();
        foreach (var (node, _, _, _) in _bases.List)
            if (InSector(node, sector))
                _occluderScratch.Add((node, 0f)); // bases always cast: sort ahead of every rock
        foreach (var n in _rocks.Nodes.Values)
            if (InSector(n, sector))
            {
                float reach = ShadowOccluderRadius + ShadowRadius(n); // big rocks cast from farther out
                float d2 = n.GlobalPosition.DistanceSquaredTo(refPos);
                if (d2 <= reach * reach)
                    _occluderScratch.Add((n, d2));
            }
        _occluderScratch.Sort((a, b) => a.D.CompareTo(b.D)); // nearest first (bases at 0)

        _sectorEnvOccluders.Clear();
        int take = Mathf.Min(_occluderScratch.Count, MaxShadowOccluders);
        for (int i = 0; i < take; i++)
        {
            var node = _occluderScratch[i].Node;
            var verts = HullVertsFor(node);
            if (verts.Length >= 4)
                _sectorEnvOccluders.Add((node, verts));
        }
        return _sectorEnvOccluders;
    }

    // Cache the (static, LOCAL-frame) hull verts per node so the throttled re-gather doesn't re-walk a
    // rock's meshes every time it re-selects the set. Cleared on world teardown (ClearCaches).
    private Vector3[] HullVertsFor(Node3D node)
    {
        if (node is MeshInstance3D { Mesh: Mesh mesh } && !HasMeshDescendant(node))
        {
            if (_meshHullVertCache.TryGetValue(mesh, out var meshCached))
                return meshCached;
            var meshVerts = CollectHullVerts(node);
            _meshHullVertCache[mesh] = meshVerts;
            return meshVerts;
        }
        if (_hullVertCache.TryGetValue(node, out var cached))
            return cached;
        var verts = CollectHullVerts(node);
        _hullVertCache[node] = verts;
        return verts;
    }

    // A node with any MeshInstance3D below it collects hierarchy-dependent verts and must stay node-keyed;
    // a lone-mesh node (every rock) is safe to share by Mesh.
    private static bool HasMeshDescendant(Node node)
    {
        foreach (Node child in node.GetChildren())
            if (child is MeshInstance3D || HasMeshDescendant(child))
                return true;
        return false;
    }

    private static bool InSector(Node3D n, uint sector) => SectorView.InSector(n, sector);

    private static float ShadowRadius(Node3D n) => n.HasMeta("shadowRadius") ? (float)n.GetMeta("shadowRadius") : 0f;

    // Collect an occluder's silhouette-relevant vertices in the occluder NODE's LOCAL frame, reduced to
    // directional extremes. Local (not world) so the baked shadow volume can parent to the node and tumble
    // with it — the shader re-derives the world silhouette each frame. Walks every MeshInstance3D under
    // `node` (a rock IS one; a base is a small hierarchy) so both come from their actual meshes.
    private static readonly List<Vector3> _hullVertScratch = new();

    private static Vector3[] CollectHullVerts(Node3D node)
    {
        _hullVertScratch.Clear();
        Transform3D rootInv = node.GlobalTransform.AffineInverse();
        CollectMeshVerts(node, rootInv, _hullVertScratch);
        return _hullVertScratch.Count >= 4 ? ShadowVolume.Extremes(_hullVertScratch, 48) : Array.Empty<Vector3>();
    }

    private static void CollectMeshVerts(Node node, Transform3D rootInv, List<Vector3> outVerts)
    {
        if (node is MeshInstance3D mi && mi.Mesh is Mesh mesh)
            // Vertex into the occluder-root's local frame: undo the root, apply the sub-mesh's own world
            // placement. For a lone-mesh rock (mi is the root) this collapses to the raw mesh vertices.
            CollectSurfaceVerts(mesh, rootInv * mi.GlobalTransform, outVerts);
        foreach (var child in node.GetChildren())
            CollectMeshVerts(child, rootInv, outVerts);
    }

    private static void CollectSurfaceVerts(Mesh mesh, Transform3D xform, List<Vector3> outVerts)
    {
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
                continue;
            foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                outVerts.Add(xform * v);
        }
    }

    // Startup warm (AssetPreloader, time-sliced during the splash/browser screen): pull the variant GLB into
    // the static mesh cache (GD.Load is near-free once the threaded load landed) and bake its shadow-occluder
    // extremes, so the first sector reveal does neither on a gameplay frame.
    public static void WarmAsteroidVariant(string variant)
    {
        var (mesh, _, _) = AsteroidRenderer.AsteroidMesh(variant);
        if (mesh is null)
            return;
        MeshRaycaster.WarmMesh(mesh); // beam/impact traces BVH, else baked at first in-flight hit
        if (_meshHullVertCache.ContainsKey(mesh))
            return;
        _hullVertScratch.Clear();
        CollectSurfaceVerts(mesh, Transform3D.Identity, _hullVertScratch);
        _meshHullVertCache[mesh] =
            _hullVertScratch.Count >= 4 ? ShadowVolume.Extremes(_hullVertScratch, 48) : Array.Empty<Vector3>();
    }
}
