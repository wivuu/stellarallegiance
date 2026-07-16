using System.Collections.Generic;
using Godot;

// =====================================================================
//  MeshRaycaster.cs — CLIENT VISIBLE-MESH SEGMENT HIT TESTING (Phase A)
//
//  A bolt clipped against the base's coarse collision SPHERE (BaseDef.Radius) visually
//  vanishes in empty space out in front of the superstructure — the sphere is fatter than
//  the hull and has nothing to spark against. This tests a shot's line against the base's
//  actual VISIBLE triangles instead, so the tracer terminates (with a spark) exactly on the
//  rendered surface. Cosmetic only: the server still resolves real damage at hull entry.
//
//  Godot's `Mesh.GenerateTriangleMesh()` yields a `TriangleMesh` — a pure math object with an
//  internal BVH and `IntersectSegment(begin, end)`. It needs no physics space or World3D, so
//  it works even when WorldRenderer draws into a SubViewport (no PhysicsDirectSpaceState there).
//
//  Bases never move or rotate, so each base bakes its raycaster ONCE at insert: we walk the
//  GLB hull subtree, cache one shared `TriangleMesh` per `Mesh` resource, and precompute each
//  MeshInstance3D's base→world transform (composed manually from the container position down,
//  so it's valid even before the node enters the scene tree) plus its inverse. Ray queries
//  then just transform the segment into each mesh's local frame and take the nearest hit.
// =====================================================================
public sealed class MeshRaycaster
{
    // One BVH per shared Mesh resource. Every base instantiates the same imported `base.glb`,
    // so all their MeshInstance3D nodes reference the same Mesh — building the TriangleMesh once
    // and caching it here means later base inserts pay nothing.
    private static readonly Dictionary<Mesh, TriangleMesh> _triCache = new();

    // A single hull mesh: its BVH plus the baked base→world placement and that transform's
    // inverse (world→local, for mapping the query segment into the mesh's own frame).
    private readonly record struct Entry(TriangleMesh Tri, Transform3D World, Transform3D Inv);

    private readonly List<Entry> _entries = new();

    // True once at least one visible mesh was found in the subtree — the caller keeps the
    // raycaster only for GLB hulls, so this is effectively always true, but it guards the
    // degenerate case of a hull subtree that carried no MeshInstance3D at all.
    public bool HasGeometry => _entries.Count > 0;

    // Build from the base's GLB hull subtree and the container's world transform. Mirrors the
    // GlbLoader.MeshAabb recursion, but bakes the FULL world transform (container × every child
    // transform, including the hull's NormalizeLongestAxis scale) rather than a hull-local one.
    public MeshRaycaster(Node3D hull, Transform3D baseWorld) => Walk(hull, baseWorld);

    // Startup warm (AssetPreloader): bake a Mesh's BVH into the shared cache up front, so the
    // first in-flight trace against it (IntersectMeshInstance on a big asteroid) pays nothing.
    internal static void WarmMesh(Mesh mesh)
    {
        if (!_triCache.ContainsKey(mesh))
            _triCache[mesh] = mesh.GenerateTriangleMesh();
    }

    private void Walk(Node node, Transform3D xform)
    {
        // COL_* nodes are Phase-B authored collision proxies (rendered invisible): they are not
        // surfaces a tracer should spark on, so skip the node and its whole subtree.
        if (node.Name.ToString().StartsWith("COL_", System.StringComparison.Ordinal))
            return;
        Transform3D world = node is Node3D n3 ? xform * n3.Transform : xform;
        if (node is MeshInstance3D mi && mi.Mesh is Mesh mesh)
        {
            if (!_triCache.TryGetValue(mesh, out TriangleMesh? tri))
                _triCache[mesh] = tri = mesh.GenerateTriangleMesh();
            if (tri != null)
                _entries.Add(new Entry(tri, world, world.AffineInverse()));
        }
        foreach (Node child in node.GetChildren())
            Walk(child, world);
    }

    // Segment [fromW, toW] against ONE MeshInstance3D read at its LIVE GlobalTransform — for meshes
    // that move/scale/spin every frame (an asteroid), where the baked-once model above doesn't fit.
    // The per-Mesh TriangleMesh BVH is still shared through _triCache (asteroid variants reuse a handful
    // of Mesh resources across instances), so this is cheap to call per frame. Returns the nearest hit
    // to `fromW`, or false on a miss. Asteroid node scale is uniform, so the normal maps back through
    // the basis with a re-normalize (no inverse-transpose needed).
    public static bool IntersectMeshInstance(MeshInstance3D mi, Vector3 fromW, Vector3 toW, out Vector3 hitW, out Vector3 normalW)
    {
        hitW = default;
        normalW = default;
        if (mi.Mesh is not Mesh mesh)
            return false;
        if (!_triCache.TryGetValue(mesh, out TriangleMesh? tri))
            _triCache[mesh] = tri = mesh.GenerateTriangleMesh();
        if (tri == null)
            return false;
        Transform3D world = mi.GlobalTransform;
        Transform3D inv = world.AffineInverse();
        Godot.Collections.Dictionary res = tri.IntersectSegment(inv * fromW, inv * toW);
        if (res.Count == 0)
            return false;
        hitW = world * res["position"].AsVector3();
        normalW = (world.Basis * res["normal"].AsVector3()).Normalized();
        return true;
    }

    // Nearest intersection of the world-space segment [fromW, toW] with any hull triangle. Each
    // mesh is queried in its own local frame (endpoints pushed through the baked inverse); the
    // winning hit is the one closest to `fromW`. Hull scale is uniform, so the surface normal maps
    // back to world through the transform's basis and only needs re-normalizing. Returns false —
    // leaving the out params untouched — when the segment misses every mesh.
    public bool IntersectSegment(Vector3 fromW, Vector3 toW, out Vector3 hitW, out Vector3 normalW)
    {
        hitW = default;
        normalW = default;
        float bestSq = float.PositiveInfinity;
        foreach (Entry e in _entries)
        {
            Godot.Collections.Dictionary res = e.Tri.IntersectSegment(e.Inv * fromW, e.Inv * toW);
            if (res.Count == 0)
                continue;
            Vector3 hw = e.World * res["position"].AsVector3();
            float dSq = (hw - fromW).LengthSquared();
            if (dSq < bestSq)
            {
                bestSq = dSq;
                hitW = hw;
                normalW = (e.World.Basis * res["normal"].AsVector3()).Normalized();
            }
        }
        return bestSq < float.PositiveInfinity;
    }
}
