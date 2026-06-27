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
    private const int Version = 1;

    // Load (and cache) the SimModel for a GLB. `cacheDir` holds the committed .simmodel sidecars.
    public static SimModel Load(string glbPath, string cacheDir)
    {
        byte[] glb = File.ReadAllBytes(glbPath);
        byte[] hash = SHA256.HashData(glb);
        string cachePath = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(glbPath) + ".simmodel");

        if (TryRead(cachePath, hash, out SimModel? cached))
            return cached!;

        var glbModel = GlbReader.Read(glbPath);
        var hull = ConvexHull.Build(glbModel.Vertices);
        var model = new SimModel(hull, glbModel.Hardpoints);

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
            model = new SimModel(ConvexHull.FromPlanes(planes, boundingRadius, longestAxis), hps);
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
    }
}
