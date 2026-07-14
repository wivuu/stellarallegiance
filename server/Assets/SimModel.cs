using System.Security.Cryptography;
using StellarAllegiance.Shared;

namespace SimServer.Assets;

// =====================================================================
//  SimModelCache — SERVER-SIDE DISK CACHE for the shared SimModel (convex hull + hardpoints).
//
//  The hull (expensive: QuickHull) is cached to disk keyed by the GLB's content hash, so it is
//  computed ONCE per GLB change and reused across server runs:
//    - hash matches  → load the cached .simmodel
//    - missing/stale → parse the GLB, build the hull, extract hardpoints, write the cache
//  The .simmodel files are committed so containers don't recompute on a cold start; the startup
//  hash-check self-heals if a GLB is edited without a regen. The SimModel/ConvexHull/GlbReader
//  types themselves live in shared/ so the client builds identical hulls in-memory.
// =====================================================================
public static class SimModelCache
{
    private const uint Magic = 0x4C444D53; // "SMDL"
    // Version 2 appends compound sub-hulls after the v1 blocks (hullCount + per-part planes). A v1
    // sidecar fails this version gate in TryRead → the SHA self-heal rebuilds from the GLB (which now
    // carries the authored COL_ parts), so an old cache never crashes — it's just recomputed once.
    private const int Version = 2;

    // Load (and cache) the SimModel for a GLB. `cacheDir` holds the committed .simmodel sidecars.
    // `pre` is an optional rigid pre-rotation baked into the parsed model (e.g. the base mesh's
    // orientation correction, CollisionConfig.BaseModelRotation); it is folded into the cache key so
    // changing the rotation self-heals a stale sidecar. A default (identity) pre keeps the key equal
    // to the bare GLB hash, so existing un-rotated ship/asteroid sidecars stay valid untouched.
    public static SimModel Load(string glbPath, string cacheDir, Quat pre = default)
    {
        byte[] glb = File.ReadAllBytes(glbPath);
        byte[] hash = KeyHash(glb, pre);
        string cachePath = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(glbPath) + ".simmodel");

        if (TryRead(cachePath, hash, out SimModel? cached))
            return cached!;

        var glbModel = GlbReader.Read(glbPath, pre);
        var hull = ConvexHull.Build(glbModel.Vertices);
        // Pass the authored COL_ parts so a fresh (or self-healed) base bake gets its per-part sub-hulls;
        // ships/asteroids/un-baked GLBs carry none → SimModel aliases the single merged hull as before.
        var model = new SimModel(hull, glbModel.Hardpoints, glbModel.CollisionParts);

        try
        {
            Directory.CreateDirectory(cacheDir);
            Write(cachePath, hash, model);
        }
        catch (IOException)
        { /* read-only fs (e.g. container) — recompute next run, no crash */
        }
        return model;
    }

    // Cache key = SHA256 of the GLB bytes, with a non-identity pre-rotation's 4 quaternion floats
    // mixed in so a rotation change invalidates the sidecar. An identity `pre` hashes the bare bytes
    // exactly like before, so committed un-rotated ship/asteroid sidecars keep their existing keys.
    private static byte[] KeyHash(byte[] glb, Quat pre)
    {
        if (pre.X == 0f && pre.Y == 0f && pre.Z == 0f && (pre.W == 0f || pre.W == 1f))
            return SHA256.HashData(glb);
        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ih.AppendData(glb);
        Span<byte> q = stackalloc byte[16];
        BitConverter.TryWriteBytes(q.Slice(0, 4), pre.X);
        BitConverter.TryWriteBytes(q.Slice(4, 4), pre.Y);
        BitConverter.TryWriteBytes(q.Slice(8, 4), pre.Z);
        BitConverter.TryWriteBytes(q.Slice(12, 4), pre.W);
        ih.AppendData(q);
        return ih.GetHashAndReset();
    }

    private static bool TryRead(string path, byte[] expectHash, out SimModel? model)
    {
        model = null;
        if (!File.Exists(path))
            return false;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version)
                return false;
            byte[] hash = r.ReadBytes(32);
            if (hash.Length != 32 || !hash.AsSpan().SequenceEqual(expectHash))
                return false;

            float boundingRadius = r.ReadSingle();
            float longestAxis = r.ReadSingle();
            int planeCount = r.ReadInt32();
            var planes = new ConvexHull.Plane[planeCount];
            for (int i = 0; i < planeCount; i++)
                planes[i] = new ConvexHull.Plane(new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle());

            int hpCount = r.ReadInt32();
            var hps = new List<(string, Vec3, Vec3)>(hpCount);
            for (int i = 0; i < hpCount; i++)
            {
                string name = r.ReadString();
                var pos = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var fwd = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                hps.Add((name, pos, fwd));
            }
            var merged = ConvexHull.FromPlanes(planes, boundingRadius, longestAxis);

            // v2: compound sub-hulls. 0 ⇒ a partless model — reconstruct via FromPrebuilt(null), which
            // aliases [merged] exactly like a fresh partless SimModel (single-hull, zero drift). >0 ⇒
            // rebuild each authored part from its stored planes (NO QuickHull — planes are already the hull).
            int hullCount = r.ReadInt32();
            ConvexHull[]? subHulls = null;
            if (hullCount > 0)
            {
                subHulls = new ConvexHull[hullCount];
                for (int h = 0; h < hullCount; h++)
                {
                    float subBr = r.ReadSingle();
                    float subLa = r.ReadSingle();
                    int subPlaneCount = r.ReadInt32();
                    var subPlanes = new ConvexHull.Plane[subPlaneCount];
                    for (int i = 0; i < subPlaneCount; i++)
                        subPlanes[i] = new ConvexHull.Plane(new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle());
                    subHulls[h] = ConvexHull.FromPlanes(subPlanes, subBr, subLa);
                }
            }
            model = SimModel.FromPrebuilt(merged, hps, subHulls);
            return true;
        }
        catch (IOException)
        {
            return false;
        } // EndOfStreamException ⊂ IOException — truncated/garbled cache
    }

    private static void Write(string path, byte[] hash, SimModel model)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Magic);
        w.Write(Version);
        w.Write(hash);
        w.Write(model.Hull.BoundingRadius);
        w.Write(model.Hull.LongestAxis);
        w.Write(model.Hull.Planes.Length);
        foreach (var p in model.Hull.Planes)
        {
            w.Write(p.N.X);
            w.Write(p.N.Y);
            w.Write(p.N.Z);
            w.Write(p.D);
        }
        w.Write(model.Hardpoints.Count);
        foreach (var (name, pos, fwd) in model.Hardpoints)
        {
            w.Write(name);
            w.Write(pos.X);
            w.Write(pos.Y);
            w.Write(pos.Z);
            w.Write(fwd.X);
            w.Write(fwd.Y);
            w.Write(fwd.Z);
        }

        // v2: compound sub-hulls. A partless model's Hulls aliases the single merged hull (same object
        // reference) → persist 0 so ship/asteroid sidecars stay minimal and the reader re-aliases
        // [Hull]. A baked base persists each authored part's planes (already-built hulls, not verts).
        bool partless = model.Hulls.Count == 1 && ReferenceEquals(model.Hulls[0], model.Hull);
        w.Write(partless ? 0 : model.Hulls.Count);
        if (!partless)
            foreach (var h in model.Hulls)
            {
                w.Write(h.BoundingRadius);
                w.Write(h.LongestAxis);
                w.Write(h.Planes.Length);
                foreach (var p in h.Planes)
                {
                    w.Write(p.N.X);
                    w.Write(p.N.Y);
                    w.Write(p.N.Z);
                    w.Write(p.D);
                }
            }
    }
}
