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
//  recompute on a cold start; the startup hash-check self-heals if a GLB is edited without a regen.
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
        byte[] glb = File.ReadAllBytes(glbPath);
        byte[] hash = SimModelCodec.KeyHash(glb, pre);
        string fileName = Path.GetFileNameWithoutExtension(glbPath) + ".simmodel";
        string cachePath = Path.Combine(cacheDir, fileName);

        if (TryRead(cachePath, hash, out SimModel? cached))
            return cached!;
        if (
            seedDir is not null
            && !string.Equals(Path.GetFullPath(seedDir), Path.GetFullPath(cacheDir), StringComparison.Ordinal)
            && TryRead(Path.Combine(seedDir, fileName), hash, out SimModel? seeded)
        )
            return seeded!;

        var glbModel = GlbReader.Read(glbPath, pre);
        var hull = ConvexHull.Build(glbModel.Vertices);
        // Pass the authored COL_ parts so a fresh (or self-healed) base bake gets its per-part sub-hulls;
        // ships/asteroids/un-baked GLBs carry none → SimModel aliases the single merged hull as before.
        var model = new SimModel(hull, glbModel.Hardpoints, glbModel.CollisionParts);

        try
        {
            Directory.CreateDirectory(cacheDir);
            using var fs = File.Create(cachePath);
            SimModelCodec.Write(fs, hash, model);
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
