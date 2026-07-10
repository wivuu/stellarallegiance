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

    // Per-sector static bodies. We keep the SPAWN pose plus each rock's tumble (axis/speed) and
    // compose the live rotation at query time, so the predicted hull spins in lockstep with the
    // rendered rock and the server (Collide.RockSpin / RockRotationAt — one shared source).
    private readonly record struct Entry(Collide.StaticBody Base, Vec3 SpinAxis, float SpinSpeed);
    private readonly Dictionary<uint, List<Entry>> _src = new();
    private readonly Dictionary<uint, List<Collide.StaticBody>> _live = new(); // reused output buffers
    private static readonly Collide.StaticBody[] Empty = System.Array.Empty<Collide.StaticBody>();

    // Deployed probes are DYNAMIC solid bodies (added/removed mid-match, unlike Welcome-time
    // asteroids/bases), so they live in their own sector→probeId map and merge into BodiesIn's output.
    // The local ship then predicts bouncing off a probe exactly as the server does (ResolveProbeCollisions);
    // only probes this client can see are here, so a fully-fogged enemy probe is a small predict-miss
    // the server reconciles — acceptable for an object you can't see anyway.
    private readonly Dictionary<uint, Dictionary<ulong, Collide.StaticBody>> _probes = new();

    // Bodies in a sector with their rotation advanced to sim time t (seconds = tick * FlightModel.Dt).
    public IReadOnlyList<Collide.StaticBody> BodiesIn(uint sector, float t)
    {
        if (!_src.TryGetValue(sector, out var src))
            return Empty;
        if (!_live.TryGetValue(sector, out var outBuf))
            _live[sector] = outBuf = new List<Collide.StaticBody>(src.Count);
        outBuf.Clear();
        foreach (var e in src)
            outBuf.Add(
                e.SpinSpeed <= 0f
                    ? e.Base
                    : Collide.StaticBody.AsteroidHull(
                        e.Base.Hull!,
                        e.Base.Center,
                        Collide.RockRotationAt(e.Base.Rot, e.SpinAxis, e.SpinSpeed, t),
                        e.Base.Scale
                    )
            );
        if (_probes.TryGetValue(sector, out var probes))
            foreach (var b in probes.Values)
                outBuf.Add(b);
        return outBuf;
    }

    // A deployed probe is on-screen at a fixed position; add it as a solid sphere of its combat hit
    // radius (matching the server). Idempotent per id (a probe resend won't duplicate it).
    public void AddProbe(uint sector, ulong probeId, Vec3 pos, float radius)
    {
        if (radius <= 0f)
            return;
        if (!_probes.TryGetValue(sector, out var m))
            _probes[sector] = m = new Dictionary<ulong, Collide.StaticBody>();
        m[probeId] = Collide.StaticBody.ProbeSphere(pos, radius);
    }

    public void RemoveProbe(uint sector, ulong probeId)
    {
        if (_probes.TryGetValue(sector, out var m))
            m.Remove(probeId);
    }

    public void Clear()
    {
        _src.Clear();
        _live.Clear();
        _probes.Clear();
    }

    public void AddAsteroid(StellarAllegiance.Net.Asteroid row)
    {
        var list = BodyList(row.SectorId);
        var center = new Vec3(row.PosX, row.PosY, row.PosZ);
        SimModel? model = string.IsNullOrEmpty(row.Variant) ? null : VariantModel(row.Variant);
        if (model is null || model.Hull.BoundingRadius <= 1e-3f)
        {
            // Sphere fallback — matches the server's ResolveStaticCollision for a hull-less rock.
            // A sphere is rotation-invariant, so it carries no spin.
            list.Add(new Entry(Collide.StaticBody.AsteroidSphere(center, row.Radius * CollisionConfig.AsteroidCollisionScale), default, 0f));
            return;
        }
        float scale = row.Radius * CollisionConfig.AsteroidCollisionScale / model.Hull.BoundingRadius;
        Quat rot = Collide.RockRotation(row.RotX, row.RotY, row.RotZ);
        var (spinAxis, spinSpeed) = Collide.RockSpin(row.AsteroidId);
        list.Add(new Entry(Collide.StaticBody.AsteroidHull(model.Hull, center, rot, scale), spinAxis, spinSpeed));
    }

    public void AddBase(StellarAllegiance.Net.Base row)
    {
        var list = BodyList(row.SectorId);
        var center = new Vec3(row.PosX, row.PosY, row.PosZ);
        SimModel? model = BaseModel();
        if (model is null || model.LongestAxis <= 1e-3f)
        {
            list.Add(new Entry(Collide.StaticBody.BaseSphere(center, CollisionConfig.BaseRadius, row.Team), default, 0f));
            return;
        }
        // World scale: the client renders the base via NormalizeLongestAxis(radius*2); bake the same.
        float ws = CollisionConfig.BaseRadius * 2f / model.LongestAxis;
        ConvexHull hull = model.Hull.Scaled(ws);
        // Authored compound sub-hulls, world-scaled exactly like the server's World.LoadBase (same GLB
        // bytes → same ConvexHull.Build per part → bit-identical hulls). Partless bases: model.Hulls
        // aliases the merged hull ⇒ a 1-element array. We ALWAYS build the compound StaticBody (same as
        // the server, which always resolves through Collide.SphereVsBody over BaseSubHulls), so the
        // client and server take the identical contact-selection path — 1-element compound and the old
        // single-hull form are numerically the same anyway (SphereVsBody's loop resolves the one hull).
        var subs = new ConvexHull[model.Hulls.Count];
        for (int i = 0; i < model.Hulls.Count; i++)
            subs[i] = model.Hulls[i].Scaled(ws);
        // Docking discs: one per HP_DockingEntrance, in base-local world units (authored * ws),
        // normal radially outward — the own-base carve-out the player docks through.
        var entrances = new List<(Vec3 Pos, Vec3 Normal)>();
        foreach (var hp in model.Hardpoints)
            if (hp.Name.StartsWith("HP_DockingEntrance", System.StringComparison.Ordinal))
            {
                Vec3 p = hp.Pos * ws;
                entrances.Add((p, Normalize(hp.Pos)));
            }
        list.Add(new Entry(Collide.StaticBody.BaseHull(hull, subs, center, row.Team, entrances.ToArray()), default, 0f));
    }

    // First-entry time t of the ray (pos + vel·t) into any BASE hull in the sector, within [0, maxT].
    // WorldRenderer's tier-2 bolt clip uses this when a base rendered its procedural placeholder (no
    // MeshRaycaster): the server-parity hull is far tighter than the coarse BaseDef sphere, so the
    // tracer stops much closer to the real silhouette. With authored COL_ parts we min-t across the
    // compound SUB-HULLS (the real superstructure — a shot threading a gap passes through), falling
    // back to the merged Hull for a partless base. Base bodies are pre-scaled to world with identity
    // rotation and unit scale, so local == world − center; sphere-fallback bases (Hull == null) are
    // simply skipped. Margin 0 — this is cosmetic.
    public bool BaseRayEntry(uint sector, Vec3 pos, Vec3 vel, float maxT, out float t)
    {
        t = maxT;
        bool hit = false;
        foreach (var b in BodiesIn(sector, 0f))
        {
            if (b.BaseTeam < 0 || b.Hull is null)
                continue;
            if (b.SubHulls is ConvexHull[] subs)
            {
                foreach (var hull in subs)
                    if (hull.RayEntry(pos - b.Center, vel, t, 0f, out float th) && th < t)
                    {
                        t = th;
                        hit = true;
                    }
            }
            else if (b.Hull.RayEntry(pos - b.Center, vel, t, 0f, out float th) && th < t)
            {
                t = th;
                hit = true;
            }
        }
        return hit;
    }

    private List<Entry> BodyList(uint sector)
    {
        if (!_src.TryGetValue(sector, out var l))
            _src[sector] = l = new List<Entry>();
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
            Log.Warn($"[CollisionWorld] could not read {resPath} — sphere-collision fallback");
            return null;
        }
        try
        {
            return SimModel.FromGlb(bytes, resPath);
        }
        catch (System.Exception e)
        {
            Log.Warn($"[CollisionWorld] failed to build hull for {resPath}: {e.Message}");
            return null;
        }
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v * (1f / l) : new Vec3(0f, 0f, 1f);
    }
}
