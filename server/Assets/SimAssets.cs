namespace SimServer.Assets;

// =====================================================================
//  SimAssets.cs — LOCATE THE ASSET DIR + LOAD CACHED SIM MODELS
//
//  The GLBs are shipped NEXT TO the binary by the csproj (Content → output/assets/...), so a
//  plain `dotnet publish` is self-contained; the canonical source stays client/assets/ (the
//  Godot client's copy). Resolution order:
//    1. $SIM_ASSETS_DIR  2. <binary>/assets (published layout)  3. probe up for client/assets
//       (running from source without a build copy).
//  The expensive per-model convex hulls are cached to a SEPARATE writable dir (NOT under the
//  assets / Godot tree): $SIM_CACHE_DIR, else <binary>/sim-cache. Everything is best-effort —
//  if the assets can't be found, callers get null and the sim falls back to sphere collision.
// =====================================================================
public static class SimAssets
{
    private static readonly object _lock = new();
    private static bool _resolved;
    private static string? _dir;

    // Root dir containing bases/ and asteroids/, or null if it can't be located.
    public static string? AssetsDir
    {
        get
        {
            lock (_lock)
            {
                if (!_resolved) { _dir = Resolve(); _resolved = true; }
                return _dir;
            }
        }
    }

    // Writable cache dir for the baked .simmodel hulls — deliberately NOT under the asset/Godot
    // tree. Defaults beside the binary; point SIM_CACHE_DIR at a volume for a read-only app dir.
    private static string CacheDir
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("SIM_CACHE_DIR");
            return !string.IsNullOrEmpty(env) ? env : Path.Combine(AppContext.BaseDirectory, "sim-cache");
        }
    }

    // Load+cache the SimModel for an asset relative to the dir (e.g. "bases/base.glb"),
    // or null if the dir/file is missing or the GLB fails to parse.
    public static SimModel? TryLoad(string relPath)
    {
        string? dir = AssetsDir;
        if (dir is null) return null;
        string full = Path.Combine(dir, relPath);
        if (!File.Exists(full)) return null;
        try { return SimModelCache.Load(full, CacheDir); }
        catch (Exception e) { Console.WriteLine($"[SimAssets] failed to load {relPath}: {e.Message}"); return null; }
    }

    private static string? Resolve()
    {
        string? env = Environment.GetEnvironmentVariable("SIM_ASSETS_DIR");
        if (!string.IsNullOrEmpty(env) && IsAssetsDir(env)) return env;

        // Published/built layout: the csproj copies the GLBs to <binary>/assets.
        string local = Path.Combine(AppContext.BaseDirectory, "assets");
        if (IsAssetsDir(local)) return local;

        // Running from source without a build copy: probe up for the canonical client/assets.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            for (int up = 0; up < 8 && d is not null; up++, d = d.Parent)
            {
                foreach (string sub in new[] { "client/assets", "assets" })
                {
                    string cand = Path.Combine(d.FullName, sub);
                    if (IsAssetsDir(cand)) return cand;
                }
            }
        }
        Console.WriteLine("[SimAssets] assets dir not found — collision falls back to spheres");
        return null;
    }

    private static bool IsAssetsDir(string dir) => File.Exists(Path.Combine(dir, "bases", "base.glb"));
}
