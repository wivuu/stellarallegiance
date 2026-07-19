using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// The narrow seam AsteroidRenderer needs into the (still-coordinator-resident) build-sphere state, so a
// rock consumed by a finishing constructor can stash its last radius for the sphere to keep growing.
// Implemented by the coordinator; moves onto ConstructionRenderer when that extracts (Milestone C).
public interface IBuildRockSink
{
    bool HasBuildSphere(ulong id);
    void StashRockRadius(ulong id, float radius);
}

// Asteroids: the streamed rock meshes + their spin/shrink animation, the per-variant mesh cache, the
// regolith per-rock tint, and the target-readout row lookup. Rocks tumble to an ABSOLUTE pose off the sim
// clock (lockstep with the collision hull) and ease their scale on a mining shrink. Owns its nodes under
// the coordinator's Asteroids container; pushes occlusion geometry to ClipCache + collision hulls to
// CollisionWorld, and applies sector visibility through SectorView.
public sealed class AsteroidRenderer
{
    // Mirror of the module's AsteroidCollisionScale (Lib.cs): the fraction of a rock's authored radius the
    // collision sphere / bolt clip uses (a tight fit inside the silhouette).
    private const float AsteroidCollisionScale = 0.82f;

    // Rock-gone dissolve length — matches the fog lost-contact fade (ContactFadeSec) so a rock consumed
    // under a build sphere slips out at the same cadence.
    private const float RockGoneFadeSec = 0.5f;

    private readonly Node3D _container;
    private readonly StandardMaterial3D _asteroidMat;
    private readonly CollisionWorld _collisionWorld;
    private readonly ClipCache _clip;
    private readonly SectorView _sectors;
    private readonly MatchClock _clock;
    private readonly WarpState _warp;
    private readonly IBuildRockSink _buildSink;

    public AsteroidRenderer(
        Node3D container,
        StandardMaterial3D asteroidMat,
        CollisionWorld collisionWorld,
        ClipCache clip,
        SectorView sectors,
        MatchClock clock,
        WarpState warp,
        IBuildRockSink buildSink
    )
    {
        _container = container;
        _asteroidMat = asteroidMat;
        _collisionWorld = collisionWorld;
        _clip = clip;
        _sectors = sectors;
        _clock = clock;
        _warp = warp;
        _buildSink = buildSink;
    }

    private readonly Dictionary<ulong, Node3D> _nodes = new();

    // Purely cosmetic lazy tumble: each rock spins slowly about a fixed pseudo-random axis, derived once
    // from its id (stable across frames; the sim treats rocks as static spheres). Applied each Tick.
    private readonly Dictionary<ulong, (Node3D Node, Quaternion Base, Vector3 Axis, float Speed)> _spins = new();

    // Decoded rock rows kept by id so a live MsgRockUpdate (mining shrink) can update the stored
    // CurrentRadius/OrePct and the target display can read the rock's class/depletion.
    private readonly Dictionary<ulong, Asteroid> _rows = new();

    // Absolute scale basis per rock: node scale One == (radius / Divisor). Populated at Insert.
    private readonly Dictionary<ulong, (Node3D Node, float Divisor)> _scaleBasis = new();

    // Target radius a shrinking rock is easing toward (drives Tick); + a scratch list of rocks that
    // finished easing this frame.
    private readonly Dictionary<ulong, float> _shrinkTarget = new();
    private readonly List<ulong> _shrinkDone = new();

    // Scratch reused by InView() so the per-frame Tab-cycle / F3-pick pass allocates nothing.
    private readonly List<(ulong Id, Node3D Node)> _viewScratch = new();

    // The rock nodes — read by the coordinator's shadow-occluder gather, ambience, mining beams, and
    // build spheres (until those extract in Milestone C/D).
    public IReadOnlyDictionary<ulong, Node3D> Nodes => _nodes;

    public void NetAdd(Asteroid row) => Insert(row);

    // The decoded rock row for a target readout (class name + depletion), or null. Read-only.
    public Asteroid? GetAsteroid(ulong id) => _rows.TryGetValue(id, out var a) ? a : null;

    // MsgRockUpdate: a rock was mined — ease its rendered mesh + client collision toward the new radius (no
    // pop) and refresh the stored CurrentRadius/OrePct. The collision + clip caches update to the ABSOLUTE
    // new size (as the server's absolute rescale) so local prediction never bounces off empty space.
    public void NetUpdateRock(ulong id, float radius, int orePct)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            row.CurrentRadius = radius;
            row.OrePct = orePct;
        }
        // Ease the mesh scale toward the new radius over the next frames (see Tick).
        if (_scaleBasis.ContainsKey(id))
            _shrinkTarget[id] = radius;
        // Client collision hull/sphere — rescaled absolutely so prediction tracks the shrunk rock.
        _collisionWorld.UpdateAsteroidRadius(id, radius);
        // Cheap cosmetic caches keyed on radius: the bolt/sun-occlusion clip sphere + the shadow reach.
        _clip.SetAsteroidRadius(id, radius * AsteroidCollisionScale);
        if (_nodes.TryGetValue(id, out var n))
            n.SetMeta("shadowRadius", radius);
    }

    // MsgRockGone: a rock was fully consumed by a finished constructor base — delete it outright. Frees the
    // mesh node (a brief fade, hidden under the build sphere's opaque core), drops every id-keyed cache, and
    // neutralizes its collision + occlusion. Its last-known radius is stashed first so an active build
    // sphere keeps growing after the node is gone. A no-op for an unknown id.
    public void NetRemoveRock(ulong id)
    {
        // If a build sphere is mid-flight on this rock, stash its last radius so the sphere keeps growing
        // after the node is gone (the prune loop clears the entry when the build ends).
        if (_buildSink.HasBuildSphere(id) && _rows.TryGetValue(id, out var row))
            _buildSink.StashRockRadius(id, row.CurrentRadius > 0f ? row.CurrentRadius : row.Radius);

        if (_nodes.TryGetValue(id, out var node))
        {
            _nodes.Remove(id);
            NodeFx.QuietFade(node, RockGoneFadeSec); // slips out under the opaque build sphere instead of popping
        }
        _rows.Remove(id);
        _scaleBasis.Remove(id);
        _shrinkTarget.Remove(id);
        _spins.Remove(id);
        // Zero this rock's clip sphere so it stops occluding bolts/sun where it used to be. The cache is
        // index-addressed, so the slot is left in place (no compaction).
        _clip.RemoveAsteroid(id);
        _collisionWorld.RemoveAsteroid(id);
    }

    private void Insert(Asteroid row)
    {
        if (_nodes.ContainsKey(row.AsteroidId))
            return;

        // Spawn at the CURRENT (possibly already-mined) radius so a rock seen for the first time in its
        // shrunk state reads correctly; Radius stays the immutable spawn baseline. `divisor` converts a
        // target radius to a uniform node scale (mesh authored bound, or the baked sphere radius).
        float rad = row.CurrentRadius > 0f ? row.CurrentRadius : row.Radius;
        MeshInstance3D node;
        float divisor;
        var (mesh, authored, baseMat) = string.IsNullOrEmpty(row.Variant) ? (null, 0f, null) : AsteroidMesh(row.Variant);
        if (mesh is not null)
        {
            node = new MeshInstance3D
            {
                Name = $"Asteroid_{row.AsteroidId}",
                Mesh = mesh,
                Position = new Vector3(row.PosX, row.PosY, row.PosZ),
                Rotation = new Vector3(row.RotX, row.RotY, row.RotZ),
                Scale = Vector3.One * (rad / authored),
            };
            divisor = authored;
            // Regolith is the common filler rock and its whole class shares just a handful of baked meshes,
            // so a field of them reads as identical clones. Give each instance a MUTED, deterministic
            // per-rock albedo tint (grey <-> tan <-> olive + a brightness wobble) by duplicating the baked
            // material and multiplying its AlbedoColor. Only regolith gets this: the valuable classes keep
            // their pinned single-colour identity.
            if ((RockClass)row.RockClass == RockClass.Regolith && baseMat is StandardMaterial3D sm)
                node.SetSurfaceOverrideMaterial(0, TintedRegolithMaterial(sm, row.AsteroidId));
        }
        else
        {
            // Fallback: missing/failed variant renders as the old grey sphere. The SphereMesh is baked at
            // `rad`, so node.Scale One = that radius and shrink eases the scale down from there.
            node = new MeshInstance3D
            {
                Name = $"Asteroid_{row.AsteroidId}",
                Mesh = new SphereMesh
                {
                    Radius = rad,
                    Height = rad * 2f,
                    RadialSegments = 12,
                    Rings = 6,
                },
                MaterialOverride = _asteroidMat,
                Position = new Vector3(row.PosX, row.PosY, row.PosZ),
            };
            divisor = rad;
        }
        _container.AddChild(node);
        _nodes[row.AsteroidId] = node;
        _rows[row.AsteroidId] = row;
        _scaleBasis[row.AsteroidId] = (node, divisor > 1e-6f ? divisor : 1f);
        // Capture the spawn pose as the spin base, then tumble absolutely off the shared sim clock so the
        // rendered rock stays in lockstep with its collision hull (shared Collide.RockSpin).
        var (sa, sp) = Collide.RockSpin(row.AsteroidId);
        _spins[row.AsteroidId] = (node, node.Quaternion, new Vector3(sa.X, sa.Y, sa.Z), sp);
        _clip.AddAsteroid(
            row.AsteroidId,
            new Vector3(row.PosX, row.PosY, row.PosZ),
            rad * AsteroidCollisionScale,
            row.SectorId
        );
        _collisionWorld.AddAsteroid(row);
        node.SetMeta("shadowRadius", rad); // extends its shadow-caster reach (big rocks cast from farther)

        // A rock landing in the sector we just warped into arrives UNDER the held WarpFlash: snap it in (no
        // fade) and push the settle window out so the flash holds until the field stops streaming.
        if (_warp.Settling && row.SectorId == _sectors.LocalSector)
        {
            _warp.LastRockSec = Time.GetTicksMsec() / 1000.0;
            node.SetMeta("sector", (int)row.SectorId);
            _sectors.ShowNodeInstant(node, row.SectorId == _sectors.ViewSector);
        }
        else
        {
            _sectors.SetNodeSectorFading(node, row.SectorId);
        }
    }

    // Per-frame tumble + mining-shrink easing. Called by the coordinator's _Process in the same pipeline
    // position the inline block held.
    public void Tick(double delta)
    {
        // Tumble each rock to its ABSOLUTE pose at the shared sim clock (not a per-frame increment), so the
        // rendered rock matches the predicted + authoritative collision hull exactly.
        if (_spins.Count > 0)
        {
            float t = _clock.Seconds;
            foreach (var (node, baseQ, axis, speed) in _spins.Values)
                node.Quaternion = new Quaternion(axis, speed * t) * baseQ;
        }

        // Mining shrink: ease each changed rock's mesh scale toward its new radius (smooth, no pop), then
        // drop it from the active set once it settles. Absolute node.Scale from the rock's basis (render
        // radius / divisor), so repeated shrinks never compound. Empty in a non-mining world.
        if (_shrinkTarget.Count > 0)
        {
            float k = 1f - Mathf.Exp(-(float)delta * 10f); // ~exponential ease toward target
            _shrinkDone.Clear();
            foreach (var (id, target) in _shrinkTarget)
            {
                if (!_scaleBasis.TryGetValue(id, out var basis))
                {
                    _shrinkDone.Add(id);
                    continue;
                }
                float want = target / basis.Divisor;
                float have = basis.Node.Scale.X;
                float next = Mathf.Lerp(have, want, k);
                if (Mathf.Abs(next - want) < want * 0.002f)
                {
                    next = want;
                    _shrinkDone.Add(id);
                }
                basis.Node.Scale = Vector3.One * next;
            }
            foreach (var id in _shrinkDone)
                _shrinkTarget.Remove(id);
        }
    }

    // Asteroids in the currently-visible sector, as (id, node). Sector visibility already drives each rock
    // node's Visible flag, so this mirrors the ship/base accessors' filter. Shared scratch — read now.
    public IReadOnlyList<(ulong Id, Node3D Node)> InView()
    {
        _viewScratch.Clear();
        foreach (var (id, node) in _nodes)
            if (node.Visible)
                _viewScratch.Add((id, node));
        return _viewScratch;
    }

    public void Reset()
    {
        _nodes.Clear();
        _spins.Clear();
        _rows.Clear();
        _scaleBasis.Clear();
        _shrinkTarget.Clear();
    }

    // ---- Per-variant mesh cache (static: instance-independent, warmed by AssetPreloader) --------

    // Loaded asteroid meshes keyed by variant name (GLB stem). The generated .glb carries its PBR material
    // on the mesh surface, so reusing one Mesh across instances keeps the colour/normal/ORM maps.
    // AuthoredRadius is the mesh's bounding radius at author scale. A null Mesh marks a variant that failed
    // to load so we don't retry and fall back to a sphere.
    private static readonly Dictionary<string, (Mesh? Mesh, float AuthoredRadius, Material? BaseMat)> _meshes = new();

    // Load (and cache) the mesh + authored radius for a variant, or (null, 0) if unavailable.
    internal static (Mesh? Mesh, float AuthoredRadius, Material? BaseMat) AsteroidMesh(string variant)
    {
        if (_meshes.TryGetValue(variant, out var cached))
            return cached;

        (Mesh? Mesh, float AuthoredRadius, Material? BaseMat) result = (null, 0f, null);
        var scene = GD.Load<PackedScene>($"res://assets/asteroids/{variant}.glb");
        if (scene?.Instantiate() is Node root)
        {
            if (FindMeshInstance(root) is MeshInstance3D mi && mi.Mesh is Mesh mesh)
            {
                // True bounding radius = farthest vertex from the mesh origin (meshes are authored as
                // radial star-fields centred on the origin). Scaling each instance by row.Radius / authored
                // then makes the collision sphere tightly circumscribe the silhouette.
                float authored = MeshBoundingRadius(mesh);
                if (authored > 0.001f)
                {
                    // Keep the baked GLB material (albedo/normal/ORM textures) so instances that want a
                    // per-rock albedo tint can duplicate it and multiply AlbedoColor.
                    var baseMat = mi.GetSurfaceOverrideMaterial(0) ?? mesh.SurfaceGetMaterial(0);
                    result = (mesh, authored, baseMat);
                }
            }
            root.QueueFree();
        }
        if (result.Mesh is null)
            Log.Warn($"[WorldRenderer] asteroid variant '{variant}' unavailable — using sphere fallback");
        _meshes[variant] = result;
        return result;
    }

    // Number of distinct per-rock regolith shades. The per-instance tint is quantised into this many
    // buckets and each (base material, bucket) pair shares one duplicated material.
    private const int RegolithTintBuckets = 48;
    private readonly Dictionary<(ulong BaseMatId, int Bucket), StandardMaterial3D> _regolithTintCache = new();

    // Deterministic, cached per-rock tint for a regolith instance: duplicates the baked material once per
    // shade bucket and multiplies its AlbedoColor. The spread stays muted (grey <-> tan <-> olive +
    // brightness) so every rock still reads as the same dull dust, just not a cloned one.
    private StandardMaterial3D TintedRegolithMaterial(StandardMaterial3D baseMat, ulong asteroidId)
    {
        int bucket = (int)(Hash64(asteroidId) % RegolithTintBuckets);
        var key = (baseMat.GetInstanceId(), bucket);
        if (_regolithTintCache.TryGetValue(key, out var cached))
            return cached;

        float t1 = Hash01((ulong)bucket * 3UL + 0UL);
        float t2 = Hash01((ulong)bucket * 3UL + 1UL);
        float t3 = Hash01((ulong)bucket * 3UL + 2UL);
        // AlbedoColor MULTIPLIES the baked albedo, so keep the spread at/below 1.0 — a darken-biased range
        // preserves the full variety instead of clamping the bright end to white.
        float bright = 0.58f + t1 * 0.42f; // 0.58 .. 1.00 — darker/lighter dust
        float warm = -0.09f + t2 * 0.16f; // + tan, - cool grey (R up / B down)
        float grn = -0.05f + t3 * 0.10f; // + olive, - cool grey
        var tint = new Color(
            Mathf.Clamp(bright * (1f + warm), 0f, 1f),
            Mathf.Clamp(bright * (1f + grn), 0f, 1f),
            Mathf.Clamp(bright * (1f - warm), 0f, 1f)
        );

        var mat = (StandardMaterial3D)baseMat.Duplicate();
        mat.AlbedoColor = tint;
        _regolithTintCache[key] = mat;
        return mat;
    }

    // splitmix64 finaliser — a cheap well-mixed hash of a 64-bit key.
    private static ulong Hash64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private static float Hash01(ulong x) => (Hash64(x) >> 40) / (float)(1UL << 24);

    private static MeshInstance3D? FindMeshInstance(Node node)
    {
        if (node is MeshInstance3D mi)
            return mi;
        foreach (var child in node.GetChildren())
            if (FindMeshInstance(child) is MeshInstance3D found)
                return found;
        return null;
    }

    // Farthest vertex distance from the mesh origin, across all surfaces. This is the tight bounding-sphere
    // radius for an origin-centred mesh; falls back to the AABB half-diagonal if a surface exposes no verts.
    private static float MeshBoundingRadius(Mesh mesh)
    {
        float maxSq = 0f;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            var arrays = mesh.SurfaceGetArrays(s);
            if (arrays.Count <= (int)Mesh.ArrayType.Vertex)
                continue;
            foreach (var v in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                maxSq = Mathf.Max(maxSq, v.LengthSquared());
        }
        return maxSq > 0f ? Mathf.Sqrt(maxSq) : mesh.GetAabb().Size.Length() * 0.5f;
    }
}
