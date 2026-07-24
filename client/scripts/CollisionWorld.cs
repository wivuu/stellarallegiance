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
    private readonly Dictionary<string, SimModel?> _baseModels = new(); // v37: per-base-type mesh cache

    // Per-sector static bodies. We keep the SPAWN pose plus each rock's tumble (axis/speed) and
    // compose the live rotation at query time, so the predicted hull spins in lockstep with the
    // rendered rock and the server (Collide.RockSpin / RockRotationAt — one shared source).
    private readonly record struct Entry(Collide.StaticBody Base, Vec3 SpinAxis, float SpinSpeed);

    private readonly Dictionary<uint, List<Entry>> _src = new();
    private readonly Dictionary<uint, List<Collide.StaticBody>> _live = new(); // reused output buffers
    private static readonly Collide.StaticBody[] Empty = System.Array.Empty<Collide.StaticBody>();

    // Per-rock rebuild info so a live mining shrink (MsgRockUpdate) can rescale a rock's body ABSOLUTELY
    // to the new radius (matching the server's absolute rescale), not by compounding a factor. Index is
    // stable (rocks are only appended to _src until Clear). Model null ⇒ the sphere-fallback rock.
    private readonly record struct RockRef(
        uint Sector,
        int Index,
        SimModel? Model,
        Vec3 Center,
        Quat Rot,
        Vec3 SpinAxis,
        float SpinSpeed
    );

    private readonly Dictionary<ulong, RockRef> _rockRefs = new();

    // Deployed probes are DYNAMIC solid bodies (added/removed mid-match, unlike Welcome-time
    // asteroids/bases), so they live in their own sector→probeId map and merge into BodiesIn's output.
    // The local ship then predicts bouncing off a probe exactly as the server does (ResolveProbeCollisions);
    // only probes this client can see are here, so a fully-fogged enemy probe is a small predict-miss
    // the server reconciles — acceptable for an object you can't see anyway.
    private readonly Dictionary<uint, Dictionary<ulong, Collide.StaticBody>> _probes = new();

    // Growing base-construction shells (Simulation.Constructors.cs build spheres) that are currently
    // SOLID (their Building phase). Like probes these are DYNAMIC — created/grown/removed mid-match by
    // WorldRenderer.UpdateBuildSpheres, keyed sector→rockId — so the local ship predicts the same bounce
    // the server enforces (ResolveBuildSphereCollisions) instead of sinking into the shell. A sphere
    // matching a rock the client can't see is a small predict-miss the server reconciles; anchored on
    // the rock, whose sector never changes for the life of a build, so _sphereSector never goes stale.
    private readonly Dictionary<uint, Dictionary<ulong, Collide.StaticBody>> _buildSpheres = new();
    private readonly Dictionary<ulong, uint> _sphereSector = new(); // rockId → sector (for removal)

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
        if (_buildSpheres.TryGetValue(sector, out var spheres))
            foreach (var b in spheres.Values)
                outBuf.Add(b);
        return outBuf;
    }

    // Add/update a solid build-sphere barrier for a rock under construction (idempotent per rock id; a
    // radius update just replaces the body). Radius is the client's rendered phase-2 envelop, matching
    // the server's ConstructorBuildSphereRadius so prediction and authority agree.
    public void SetBuildSphere(uint sector, ulong rockId, Vec3 center, float radius)
    {
        if (radius <= 0f)
        {
            RemoveBuildSphere(rockId);
            return;
        }
        if (!_buildSpheres.TryGetValue(sector, out var m))
            _buildSpheres[sector] = m = new Dictionary<ulong, Collide.StaticBody>();
        m[rockId] = Collide.StaticBody.BuildSphere(center, radius);
        _sphereSector[rockId] = sector;
    }

    // Drop a build-sphere barrier (construction finished/cancelled). No-op for an unknown rock id.
    public void RemoveBuildSphere(ulong rockId)
    {
        if (_sphereSector.TryGetValue(rockId, out uint sector) && _buildSpheres.TryGetValue(sector, out var m))
            m.Remove(rockId);
        _sphereSector.Remove(rockId);
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
        _buildSpheres.Clear();
        _sphereSector.Clear();
        _rockRefs.Clear();
        _shipHulls.Clear(); // a world rebuild may stream retuned defs (ModelName/ModelLength)
    }

    public void AddAsteroid(StellarAllegiance.Net.Asteroid row)
    {
        var list = BodyList(row.SectorId);
        var center = new Vec3(row.PosX, row.PosY, row.PosZ);
        // Build at the CURRENT (possibly already-mined) radius so a rock first seen shrunk collides at
        // its true size; UpdateAsteroidRadius later rescales it absolutely as it is mined further.
        float rad = row.CurrentRadius > 0f ? row.CurrentRadius : row.Radius;
        SimModel? model = string.IsNullOrEmpty(row.Variant) ? null : VariantModel(row.Variant);
        int index = list.Count;
        Quat rot = Collide.RockRotation(row.RotX, row.RotY, row.RotZ);
        var (spinAxis, spinSpeed) = Collide.RockSpin(row.AsteroidId);
        if (model is null || model.Hull.BoundingRadius <= 1e-3f)
        {
            // Sphere fallback — matches the server's ResolveStaticCollision for a hull-less rock.
            // A sphere is rotation-invariant, so it carries no spin.
            list.Add(
                new Entry(
                    Collide.StaticBody.AsteroidSphere(center, rad * CollisionConfig.AsteroidCollisionScale),
                    default,
                    0f
                )
            );
            _rockRefs[row.AsteroidId] = new RockRef(row.SectorId, index, null, center, rot, default, 0f);
            return;
        }
        float scale = rad * CollisionConfig.AsteroidCollisionScale / model.Hull.BoundingRadius;
        list.Add(new Entry(Collide.StaticBody.AsteroidHull(model.Hull, center, rot, scale), spinAxis, spinSpeed));
        _rockRefs[row.AsteroidId] = new RockRef(row.SectorId, index, model, center, rot, spinAxis, spinSpeed);
    }

    // A rock was mined: rebuild its body at the new radius, ABSOLUTELY (radius → scale), the same way
    // the server recomputes RockBody.Scale from its immutable spawn scale — so the predicted hull tracks
    // the shrunk rock and the ship never bounces off empty space where the rock used to be. No-op for an
    // unknown id (a rock this client never received, e.g. still fogged).
    public void UpdateAsteroidRadius(ulong id, float newRadius)
    {
        if (!_rockRefs.TryGetValue(id, out var rr))
            return;
        if (!_src.TryGetValue(rr.Sector, out var list) || rr.Index >= list.Count)
            return;
        if (rr.Model is null || rr.Model.Hull.BoundingRadius <= 1e-3f)
        {
            list[rr.Index] = new Entry(
                Collide.StaticBody.AsteroidSphere(rr.Center, newRadius * CollisionConfig.AsteroidCollisionScale),
                default,
                0f
            );
        }
        else
        {
            float scale = newRadius * CollisionConfig.AsteroidCollisionScale / rr.Model.Hull.BoundingRadius;
            list[rr.Index] = new Entry(
                Collide.StaticBody.AsteroidHull(rr.Model.Hull, rr.Center, rr.Rot, scale),
                rr.SpinAxis,
                rr.SpinSpeed
            );
        }
    }

    // Fully drop a rock from local prediction (a finished constructor base consumed the asteroid). The
    // per-sector body list is index-addressed by RockRef.Index, so compacting it would reindex every
    // other rock's ref — instead neutralize this rock's slot to a zero-radius sphere (never the deepest
    // contact, so it can't bounce the ship) and forget its ref. The base that replaces it brings its own
    // collision. No-op for an unknown id (a rock this client never had).
    public void RemoveAsteroid(ulong id)
    {
        if (!_rockRefs.TryGetValue(id, out var rr))
            return;
        if (_src.TryGetValue(rr.Sector, out var list) && rr.Index < list.Count)
            list[rr.Index] = new Entry(Collide.StaticBody.AsteroidSphere(rr.Center, 0f), default, 0f);
        _rockRefs.Remove(id);
    }

    public void AddBase(DefRegistry defs, StellarAllegiance.Net.Base row)
    {
        var list = BodyList(row.SectorId);
        var center = new Vec3(row.PosX, row.PosY, row.PosZ);
        // v37: per-base-type mesh + radius (mirrors the server's World.LoadBaseModel), so an outpost
        // predicts its own hull, not the garrison's. Falls back to the garrison model/radius.
        BaseDef? def = defs.GetBaseDef(row.BaseTypeId);
        string modelName = string.IsNullOrEmpty(def?.ModelName) ? "garrison" : def!.ModelName;
        float radius = def is { Radius: > 0f } ? def.Radius : CollisionConfig.BaseRadius;
        SimModel? model = BaseModel(modelName);
        if (model is null || model.LongestAxis <= 1e-3f)
        {
            list.Add(new Entry(Collide.StaticBody.BaseSphere(center, radius, row.Team), default, 0f));
            return;
        }
        // World scale: the client renders the base via NormalizeLongestAxis(radius*2); bake the same.
        float ws = radius * 2f / model.LongestAxis;
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
        // Docking doors: the SHARED DockFaceParser turns the grouped HP_DockingEntrance markers into
        // rectangular faces from the SAME GLB bytes + SAME world scale the server uses — so the
        // client's DockFace[] is bit-identical to World.LoadBase's and prediction agrees with the
        // server at the bay mouth (no rubber-banding). N doors supported (each a group of 5 markers).
        DockFace[] faces = DockFaceParser.Build(model.Hardpoints, ws, msg => Log.Warn($"[CollisionWorld] {msg}"));
        // Station class + largest door (2026-07-21 launch-station-classes): the dock carve-out
        // inputs for restricted hulls — same catalog map + same DockRules pick as the server.
        list.Add(
            new Entry(
                Collide.StaticBody.BaseHull(
                    hull,
                    subs,
                    center,
                    row.Team,
                    faces,
                    defs.StationClassOfBaseType(row.BaseTypeId),
                    DockRules.LargestFaceIndex(faces)
                ),
                default,
                0f
            )
        );
    }

    // Per-class ship collision hulls, mirroring server World.LoadShipBodies: each ship def's GLB
    // pre-scaled to its authored ModelLength (the same length the renderer normalizes to), from the
    // SAME GLB bytes → bit-identical hulls, so the local ship's predicted ship-ship bounce matches
    // the server's Pass C. Keyed by the def actually flown (a pod uses the reserved Pod class id).
    // A missing/degenerate GLB caches null → the caller falls back to the ShipRadius sphere, exactly
    // like the server. A not-yet-streamed def returns null WITHOUT caching (it may still arrive).
    private readonly Dictionary<byte, (ConvexHull Hull, float Bound)?> _shipHulls = new();

    public (ConvexHull Hull, float Bound)? ShipHull(DefRegistry defs, byte cls, bool isPod)
    {
        byte key = isPod ? GameContent.PodClassId : cls;
        if (_shipHulls.TryGetValue(key, out var cached))
            return cached;
        if (!defs.TryGetShipDef(key, out var def))
            return null; // def not streamed yet — retry next query
        (ConvexHull, float)? built = null;
        if (!string.IsNullOrEmpty(def.ModelName) && def.ModelLength > 1e-3f)
        {
            SimModel? model = LoadGlb($"res://assets/ships/{def.ModelName}.glb");
            if (model is not null && model.LongestAxis > 1e-3f)
            {
                float ws = def.ModelLength / model.LongestAxis;
                built = (model.Hull.Scaled(ws), model.Hull.BoundingRadius * ws);
            }
        }
        _shipHulls[key] = built;
        return built;
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

    private SimModel? BaseModel(string modelName)
    {
        if (_baseModels.TryGetValue(modelName, out var cached))
            return cached;
        // Correct the base mesh's authored +90°-off orientation with the SAME rotation the server
        // bakes (World.LoadBaseModel) and the visual renders (BaseModelLoader), so the predicted hull +
        // docking faces stay bit-identical to the server's and aligned with the rendered base.
        var model = LoadGlb($"res://assets/bases/{modelName}.glb", CollisionConfig.BaseModelRotation);
        _baseModels[modelName] = model;
        return model;
    }

    // Read the raw .glb bytes from res:// and build the shared SimModel (same path the server takes
    // from disk). Returns null if the file can't be read so the caller falls back to a sphere.
    // ponytail: needs the raw .glb included in exported builds (export filter); editor reads it from
    // disk fine. If a build ships without it, collision degrades to spheres, not a crash.
    //
    // AssetPreloader builds the asteroid-variant models OFF-THREAD at startup; a hit skips the
    // QuickHull rebuild (~60ms per variant, previously on the join frame). The shared cache is
    // safe because the pre-rotation is a pure function of the path's category (only bases pass
    // CollisionConfig.BaseModelRotation, and every base load comes through here with it). A build
    // done here is stored back so a world rebuild's fresh CollisionWorld reuses it too.
    private static SimModel? LoadGlb(string resPath, Quat pre = default)
    {
        if (AssetPreloader.TryGetSimModel(resPath, out SimModel? warm))
            return warm;
        byte[] bytes = FileAccess.GetFileAsBytes(resPath);
        SimModel? model = null;
        if (bytes is null || bytes.Length == 0)
            Log.Warn($"[CollisionWorld] could not read {resPath} — sphere-collision fallback");
        else
            try
            {
                model = SimModel.FromGlb(bytes, resPath, pre);
            }
            catch (System.Exception e)
            {
                Log.Warn($"[CollisionWorld] failed to build hull for {resPath}: {e.Message}");
            }
        AssetPreloader.StoreSimModel(resPath, model);
        return model;
    }
}
