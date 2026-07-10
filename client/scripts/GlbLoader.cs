using System.Collections.Generic;
using Godot;

// =====================================================================
//  GlbLoader.cs — CLIENT GLB HULL/BASE LOADING HELPERS
//
//  Shared plumbing for the documented GLB mesh convention (see
//  docs/GLB-AND-HARDPOINT-FORMAT.md §4): load an authored `.glb` in place of the
//  procedural placeholder, scale an arbitrary-scale art asset to the silhouette the
//  def-seeded hardpoints expect, and answer "does this GLB already carry its own HP_
//  node?" so a hand-authored mesh's nodes can override the markers. An authored hull keeps
//  its own baked PBR materials (friend/foe is read from the HUD, not a flat team tint).
//
//  ShipModelLoader and BaseModelLoader stay deliberately independent parallel files for
//  their own (placeholder geometry, FX) logic; only this asset-IO plumbing is shared.
// =====================================================================
public static class GlbLoader
{
    // res:// paths we've already failed to load, so a missing asset warns once and then
    // falls back silently (the procedural placeholder) instead of spamming per spawn.
    private static readonly HashSet<string> _missing = [];

    // Instantiate the GLB at `resPath` as a Node3D, or null if it's absent/failed (the caller
    // then builds the procedural placeholder). Each call yields a fresh instance, so per-team
    // MaterialOverride and per-instance scaling don't leak across ships.
    public static Node3D? Load(string resPath)
    {
        if (_missing.Contains(resPath))
            return null;
        if (
            ResourceLoader.Exists(resPath)
            && GD.Load<PackedScene>(resPath) is PackedScene scene
            && scene.Instantiate() is Node3D root
        )
        {
            HideCollisionProxies(root);
            return root;
        }

        _missing.Add(resPath);
        Log.Warn($"[GlbLoader] '{resPath}' unavailable — using procedural placeholder");
        return null;
    }

    // Hide every COL_* MeshInstance3D in a freshly instantiated GLB. Those are the authored
    // convex COLLISION-PROXY parts baked into the .glb by tools/collision-hull (COL_<Name> nodes): the
    // shared/server + client hull pipeline consumes their geometry to build one convex hull per
    // part, but they must NEVER render. Hiding them here (rather than per-loader) covers bases and
    // ships alike — any future COL_-baked mesh is invisible by construction. Godot's importer
    // preserves node names, and only a `-col`/`-colonly` SUFFIX is an import hint, so the COL_
    // PREFIX is a safe, render-only marker. MeshAabb still walks these (visibility-agnostic), so
    // NormalizeLongestAxis is unaffected — the baked parts are validated to stay inside the visual
    // AABB, keeping the scale contract intact.
    private static void HideCollisionProxies(Node node)
    {
        if (node is MeshInstance3D mi && mi.Name.ToString().StartsWith("COL_", System.StringComparison.Ordinal))
            mi.Visible = false;
        foreach (Node child in node.GetChildren())
            HideCollisionProxies(child);
    }

    // Uniform-scale `root` so its longest local axis equals `target` world units. Re-used
    // Allegiance hulls aren't authored at game scale, so this maps each to the placeholder
    // silhouette the fixed def hardpoints (muzzle +Z, nozzles −Z) were sized against. An
    // authored-to-scale mesh measures ≈ target and barely moves.
    public static void NormalizeLongestAxis(Node3D root, float target)
    {
        Aabb box = MeshAabb(root);
        float longest = Mathf.Max(box.Size.X, Mathf.Max(box.Size.Y, box.Size.Z));
        if (longest > 1e-4f && target > 0f)
            root.Scale = Vector3.One * (target / longest);
    }

    // Does the subtree contain a node named exactly `name` (an HP_<Kind>_<Index>)? When a GLB
    // author placed the hardpoint in-mesh, its node overrides the def-seeded marker, so the
    // caller skips adding a duplicate. (Today's re-used hulls carry none, so the markers stand.)
    public static bool HasNode(Node node, string name)
    {
        if (node.Name == name)
            return true;
        foreach (Node child in node.GetChildren())
            if (HasNode(child, name))
                return true;
        return false;
    }

    // Find every node whose name starts with `prefix` (e.g. "HP_Light_"), returning each node's
    // transform relative to `hull` (hull's own transform excluded — same subtree semantics as
    // HasNode). Lets the loaders place beacons/markers at GLB-authored hardpoints instead of
    // def-seeded offsets. Walks the tree accumulating each node's transform, mirroring MeshAabb.
    public static List<(string Name, Transform3D Local)> FindHardpoints(Node hull, string prefix)
    {
        var found = new List<(string, Transform3D)>();

        void Recurse(Node node, Transform3D xform)
        {
            Transform3D local = node != hull && node is Node3D n3 ? xform * n3.Transform : xform;
            if (node != hull && node.Name.ToString().StartsWith(prefix))
                found.Add((node.Name, local));
            foreach (Node child in node.GetChildren())
                Recurse(child, local);
        }

        Recurse(hull, Transform3D.Identity);
        return found;
    }

    // Combined AABB of every mesh in the subtree, in `root`'s local space (root's own transform
    // is ignored — it's the freshly-instantiated, not-yet-scaled hull node). Walks the tree
    // accumulating each node's transform and expands over each mesh AABB's 8 corners.
    private static Aabb MeshAabb(Node root)
    {
        bool found = false;
        Aabb acc = new();

        void Recurse(Node node, Transform3D xform)
        {
            Transform3D local = node != root && node is Node3D n3 ? xform * n3.Transform : xform;
            if (node is MeshInstance3D mi && mi.Mesh is Mesh mesh)
            {
                Aabb b = mesh.GetAabb();
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = local * b.GetEndpoint(i);
                    if (!found)
                    {
                        acc = new Aabb(p, Vector3.Zero);
                        found = true;
                    }
                    else
                        acc = acc.Expand(p);
                }
            }
            foreach (Node child in node.GetChildren())
                Recurse(child, local);
        }

        Recurse(root, Transform3D.Identity);
        return acc;
    }
}
