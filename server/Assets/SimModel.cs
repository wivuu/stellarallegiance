using StellarAllegiance.Shared;

namespace SimServer.Assets;

// =====================================================================
//  SimModelCache — SERVER-SIDE DISK CACHE for the shared SimModel (convex hull + hardpoints).
//
//  The hull (expensive: QuickHull) is cached to disk keyed by the GLB's content hash, so it is
//  computed ONCE per GLB change and reused across server runs:
//    - hash matches  → load the cached .simmodel
//    - missing/stale → parse the GLB, build the hull, extract hardpoints, write the cache
//  The release image bakes its .simmodel files at build time (--pregen-assets) so containers don't
//  recompute on a cold start; the hash-check self-heals if a GLB is edited without a regen.
//
//  Hashing means reading the whole GLB, which also leaves it in the page cache that a container host
//  bills as memory. So each .simmodel gets a .stamp beside it recording the key together with the GLB's
//  size and modified time: while those are unchanged, the remembered key is used and the GLB is never
//  opened. An edit that kept both exactly the same would go unnoticed; any other edit falls back to
//  the hash.
//  The SimModel/ConvexHull/GlbReader types live in shared/ so the client builds identical hulls, and
//  so does the BYTE FORMAT (shared/Collision/SimModelCodec.cs): the same files, written at export time
//  next to each client GLB, are the only collision data a packaged client has.
// =====================================================================
public static class SimModelCache
{
    // Load (and cache) the SimModel for a GLB. `cacheDir` holds the .simmodel files.
    // `pre` is an optional rigid pre-rotation baked into the parsed model (e.g. the base mesh's
    // orientation correction, CollisionConfig.BaseModelRotation); it is folded into the cache key so
    // changing the rotation self-heals a stale file. A default (identity) pre keeps the key equal
    // to the bare GLB hash, so existing un-rotated ship/asteroid files stay valid untouched.
    //
    // `seedDir` is an optional READ-ONLY second place to look: the cache that shipped with the build.
    // A hit there is returned as is (nothing is copied - it is already on disk, and stays valid exactly
    // as long as the GLB hash matches).
    public static SimModel Load(string glbPath, string cacheDir, Quat pre = default, string? seedDir = null)
    {
        string fileName = Path.GetFileNameWithoutExtension(glbPath) + ".simmodel";
        string cachePath = Path.Combine(cacheDir, fileName);
        string? seedPath =
            seedDir is not null
            && !string.Equals(Path.GetFullPath(seedDir), Path.GetFullPath(cacheDir), StringComparison.Ordinal)
                ? Path.Combine(seedDir, fileName)
                : null;

        string stamp = StampOf(glbPath, pre);
        if (TryReadStamped(cachePath, stamp, cachePath, seedPath, out SimModel? stamped))
            return stamped!;
        if (seedPath is not null && TryReadStamped(seedPath, stamp, cachePath, seedPath, out stamped))
            return stamped!;

        byte[] glb = File.ReadAllBytes(glbPath);
        byte[] hash = SimModelCodec.KeyHash(glb, pre);

        if (TryRead(cachePath, hash, out SimModel? cached) || (seedPath is not null && TryRead(seedPath, hash, out cached)))
        {
            WriteStamp(cacheDir, cachePath, stamp, hash);
            return cached!;
        }

        var glbModel = GlbReader.Read(glbPath, pre);
        var hull = ConvexHull.Build(glbModel.Vertices);
        // Pass the authored COL_ parts so a fresh (or self-healed) base bake gets its per-part sub-hulls;
        // ships/asteroids/un-baked GLBs carry none → SimModel aliases the single merged hull as before.
        var model = new SimModel(hull, glbModel.Hardpoints, glbModel.CollisionParts);

        try
        {
            Directory.CreateDirectory(cacheDir);
            using (var fs = File.Create(cachePath))
                SimModelCodec.Write(fs, hash, model);
            WriteStamp(cacheDir, cachePath, stamp, hash);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only fs or a directory this user may not write (a container, a mounted AppImage):
            // recompute next run, no crash. UnauthorizedAccessException is NOT an IOException - letting
            // it escape used to reach SimAssets.TryLoad's catch-all, which returns null, and the sim
            // then SILENTLY fell back to sphere collision for that model.
        }
        return model;
    }

    private const string StampSuffix = ".stamp";

    // What a GLB looks like without opening it. `pre` is part of it because it is part of the key.
    private static string StampOf(string glbPath, Quat pre)
    {
        var glb = new FileInfo(glbPath);
        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        return $"{glb.Length:x} {glb.LastWriteTimeUtc.Ticks:x} {Bits(pre.X):x} {Bits(pre.Y):x} {Bits(pre.Z):x} {Bits(pre.W):x}";
    }

    // The model for this exact stamp, if `stampOwner` remembers its key and either cache holds that key.
    private static bool TryReadStamped(
        string stampOwner,
        string stamp,
        string cachePath,
        string? seedPath,
        out SimModel? model
    )
    {
        model = null;
        byte[]? key = ReadStampedKey(stampOwner + StampSuffix, stamp);
        return key is not null
            && (TryRead(cachePath, key, out model) || (seedPath is not null && TryRead(seedPath, key, out model)));
    }

    private static byte[]? ReadStampedKey(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            string[] lines = File.ReadAllLines(path);
            return lines.Length == 2 && lines[0] == stamp ? Convert.FromHexString(lines[1]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    // Best effort: on a read-only filesystem the next load just hashes again.
    private static void WriteStamp(string cacheDir, string cachePath, string stamp, byte[] key)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            File.WriteAllLines(cachePath + StampSuffix, [stamp, Convert.ToHexString(key)]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A cached model, if `path` holds a current-version file whose key is `expectHash` (= it was built
    // from these exact GLB bytes and this pre-rotation). Anything else - missing, stale, an older
    // version, truncated, unreadable - is a miss, and the caller rebuilds from the GLB.
    private static bool TryRead(string path, byte[] expectHash, out SimModel? model)
    {
        model = null;
        if (!File.Exists(path))
            return false;
        try
        {
            using var fs = File.OpenRead(path);
            return SimModelCodec.TryRead(fs, expectHash, out _, out model);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
