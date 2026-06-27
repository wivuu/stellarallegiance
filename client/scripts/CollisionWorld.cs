using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// Client-side collision world. Builds the SAME convex hulls the server builds — from the SAME GLB
// bytes via the shared GlbReader + ConvexHull — and exposes per-sector static bodies so the local
// ship's prediction (PredictionController) resolves collisions identically to the server. Without
// it the client predicted no collision response: the ship sank into a rock until the server's
// authoritative push-out snapped it back. With it the predicted ship stops/bounces at the surface
// immediately and reconciliation has ~nothing to correct.
//
// Hull parity is the whole point: reading the raw .glb bytes (not the Godot-imported mesh) and
// running the same ConvexHull.Build guarantees the client and server hulls are bit-identical, so
// the predicted bounce matches the authoritative one. Scaling mirrors server World.cs exactly.
public sealed class CollisionWorld
{
    // Cached authored-unit models, built once per asteroid variant / for the base. A null entry
    // marks a GLB that couldn't be read (→ sphere fallback), so we don't retry.
    private readonly Dictionary<string, SimModel?> _variantModels = new();
    private SimModel? _baseModel;
    private bool _baseLoaded;

    // Per-sector static bodies (asteroids + bases).
    private readonly Dictionary<uint, List<Collide.StaticBody>> _bodies = new();
    private static readonly Collide.StaticBody[] Empty = System.Array.Empty<Collide.StaticBody>();

    public IReadOnlyList<Collide.StaticBody> BodiesIn(uint sector) =>
        _bodies.TryGetValue(sector, out var l) ? l : Empty;

    public void Clear() => _bodies.Clear();

    public void AddAsteroid(StellarAllegiance.Net.Asteroid row)
    {
        var list = BodyList(row.SectorId);
        var center = new Vec3(row.PosX, row.PosY, row.PosZ);
        SimModel? model = string.IsNullOrEmpty(row.Variant) ? null : VariantModel(row.Variant);
        if (model is null || model.Hull.BoundingRadius <= 1e-3f)
        {
            // Sphere fallback — matches the server's ResolveStaticCollision for a hull-less rock.
            list.Add(Collide.StaticBody.AsteroidSphere(center, row.Radius * CollisionConfig.AsteroidCollisionScale));
            return;
        }
        float scale = row.Radius * CollisionConfig.AsteroidCollisionScale / model.Hull.BoundingRadius;
        Quat rot = Collide.RockRotation(row.RotX, row.RotY, row.RotZ);
        list.Add(Collide.StaticBody.AsteroidHull(model.Hull, center, rot, scale));
    }

    public void AddBase(StellarAllegiance.Net.Base row)
    {
        var list = BodyList(row.SectorId);
        var center = new Vec3(row.PosX, row.PosY, row.PosZ);
        SimModel? model = BaseModel();
        if (model is null || model.LongestAxis <= 1e-3f)
        {
            list.Add(Collide.StaticBody.BaseSphere(center, CollisionConfig.BaseRadius, row.Team));
            return;
        }
        // World scale: the client renders the base via NormalizeLongestAxis(radius*2); bake the same.
        float ws = CollisionConfig.BaseRadius * 2f / model.LongestAxis;
        ConvexHull hull = model.Hull.Scaled(ws);
        // Docking discs: one per HP_DockingEntrance, in base-local world units (authored * ws),
        // normal radially outward — the own-base carve-out the player docks through.
        var entrances = new List<(Vec3 Pos, Vec3 Normal)>();
        foreach (var hp in model.Hardpoints)
            if (hp.Name.StartsWith("HP_DockingEntrance", System.StringComparison.Ordinal))
            {
                Vec3 p = hp.Pos * ws;
                entrances.Add((p, Normalize(hp.Pos)));
            }
        list.Add(Collide.StaticBody.BaseHull(hull, center, row.Team, entrances.ToArray()));
    }

    private List<Collide.StaticBody> BodyList(uint sector)
    {
        if (!_bodies.TryGetValue(sector, out var l))
            _bodies[sector] = l = new List<Collide.StaticBody>();
        return l;
    }

    private SimModel? VariantModel(string variant)
    {
        if (_variantModels.TryGetValue(variant, out var cached))
            return cached;
        var model = LoadGlb($"res://assets/asteroids/{variant}.glb");
        _variantModels[variant] = model;
        return model;
    }

    private SimModel? BaseModel()
    {
        if (!_baseLoaded)
        {
            _baseModel = LoadGlb("res://assets/bases/base.glb");
            _baseLoaded = true;
        }
        return _baseModel;
    }

    // Read the raw .glb bytes from res:// and build the shared SimModel (same path the server takes
    // from disk). Returns null if the file can't be read so the caller falls back to a sphere.
    // ponytail: needs the raw .glb included in exported builds (export filter); editor reads it from
    // disk fine. If a build ships without it, collision degrades to spheres, not a crash.
    private static SimModel? LoadGlb(string resPath)
    {
        byte[] bytes = FileAccess.GetFileAsBytes(resPath);
        if (bytes is null || bytes.Length == 0)
        {
            GD.PushWarning($"[CollisionWorld] could not read {resPath} — sphere-collision fallback");
            return null;
        }
        try
        {
            return SimModel.FromGlb(bytes, resPath);
        }
        catch (System.Exception e)
        {
            GD.PushWarning($"[CollisionWorld] failed to build hull for {resPath}: {e.Message}");
            return null;
        }
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v * (1f / l) : new Vec3(0f, 0f, 1f);
    }
}
