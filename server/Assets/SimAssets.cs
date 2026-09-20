using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StellarAllegiance.Shared;

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
    private static readonly Lock _lock = new();
    private static bool _resolved;
    private static string? _dir;

    // Assigned once at boot (Program.cs) after the host's ILoggerFactory exists; NullLogger keeps
    // the pre-host --pregen-assets path (which prints its own Console summary) a safe no-op.
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    // Root dir containing bases/ and asteroids/, or null if it can't be located.
    public static string? AssetsDir
    {
        get
        {
            lock (_lock)
            {
                if (!_resolved)
                {
                    _dir = Resolve();
                    _resolved = true;
                }
                return _dir;
            }
        }
    }

    // Writable cache dir for the baked .simmodel hulls — deliberately NOT under the asset/Godot
    // tree. Defaults beside the binary; point SIM_CACHE_DIR at a volume for a read-only app dir.
    // Public so other writable-beside-the-binary state (the lobby credential file, WP2.1) can
    // anchor itself relative to the same resolved location without duplicating the env lookup.
    //
    // A PACKAGED server (Velopack, docs/adr/0005) is the exception to "beside the binary": on Linux
    // the install is one AppImage, and the binary's own directory is inside it - read-only when the
    // AppImage is mounted, thrown away and re-extracted on every update when it is unpacked (the release
    // image). So the default moves BESIDE THE APPIMAGE FILE, the one location that belongs to this
    // install and survives its updates. Everything durable follows, because it all chains off this
    // directory: the lobby credential (LobbyCredentialStore), the match-report spool and the update
    // marker are its siblings. The cache baked into the package is still used - see BakedCacheDir.
    public static string CacheDir => ResolveCacheDir(Environment.GetEnvironmentVariable, AppContext.BaseDirectory);

    // The folder a packaged server keeps beside its AppImage (one folder, so the state does not pile
    // up loose next to it).
    public const string PackagedStateDirName = "stellar-server-data";

    public static string ResolveCacheDir(Func<string, string?> env, string baseDirectory)
    {
        string? explicitDir = env("SIM_CACHE_DIR");
        if (!string.IsNullOrEmpty(explicitDir))
            return explicitDir;
        string? appImage = env("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage) && Path.GetDirectoryName(Path.GetFullPath(appImage)) is { Length: > 0 } dir)
            return Path.Combine(dir, PackagedStateDirName, "sim-cache");
        return Path.Combine(baseDirectory, "sim-cache");
    }

    // The hull cache that SHIPS with the build (the Dockerfile and the package script run
    // `--pregen-assets`). Read-only seed for SimModelCache.Load: when CacheDir points somewhere else -
    // a fresh volume, the folder beside an AppImage - a cold start still loads the baked hulls instead
    // of recomputing every one of them.
    public static string BakedCacheDir => Path.Combine(AppContext.BaseDirectory, "sim-cache");

    // Load+cache the SimModel for an asset relative to the dir (e.g. "bases/base.glb"),
    // or null if the dir/file is missing or the GLB fails to parse. `pre` is an optional rigid
    // pre-rotation baked into the model (bases pass CollisionConfig.BaseModelRotation to correct
    // their authored orientation; ships/asteroids pass the default identity).
    public static SimModel? TryLoad(string relPath, Quat pre = default)
    {
        string? dir = AssetsDir;
        if (dir is null)
            return null;
        string full = Path.Combine(dir, relPath);
        if (!File.Exists(full))
            return null;
        try
        {
            return SimModelCache.Load(full, CacheDir, pre, BakedCacheDir);
        }
        catch (Exception e)
        {
            Log.AssetLoadFailed(Logger, relPath, e);
            return null;
        }
    }

    private static string? Resolve()
    {
        string? env = Environment.GetEnvironmentVariable("SIM_ASSETS_DIR");
        if (!string.IsNullOrEmpty(env) && IsAssetsDir(env))
            return env;

        // Published/built layout: the csproj copies the GLBs to <binary>/assets.
        string local = Path.Combine(AppContext.BaseDirectory, "assets");
        if (IsAssetsDir(local))
            return local;

        // Running from source without a build copy: probe up for the canonical client/assets.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = new DirectoryInfo(start);
            for (int up = 0; up < 8 && d is not null; up++, d = d.Parent)
            {
                foreach (string sub in new[] { "client/assets", "assets" })
                {
                    string cand = Path.Combine(d.FullName, sub);
                    if (IsAssetsDir(cand))
                        return cand;
                }
            }
        }
        Log.AssetsDirNotFound(Logger);
        return null;
    }

    private static bool IsAssetsDir(string dir) => File.Exists(Path.Combine(dir, "bases", "garrison.glb"));
}
