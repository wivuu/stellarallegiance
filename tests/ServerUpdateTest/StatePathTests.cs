using SimServer.Assets;
using SimServer.Net;
using SimServer.Update;

// Where a PACKAGED server keeps what must outlive an update. On Linux the install is one AppImage; the
// binary's own directory is inside it (read-only when mounted, re-extracted on every update in the
// release image), so "beside the binary" - the default everywhere else - would lose the lobby credential
// (a new device-code approval after EVERY update) and crash the match-report spool on a read-only dir.
static class StatePathTests
{
    public static void Run()
    {
        T.Section("state paths: where durable state lives");
        static Func<string, string?> Env(params (string Key, string? Value)[] vars) =>
            key => vars.FirstOrDefault(v => v.Key == key).Value;

        T.Eq(
            Path.Combine("/srv/sim", "sim-cache"),
            SimAssets.ResolveCacheDir(Env(), "/srv/sim"),
            "plain publish: beside the binary, as ever"
        );
        T.Eq(
            Path.Combine("/opt/stellar", "stellar-server-data", "sim-cache"),
            SimAssets.ResolveCacheDir(
                Env(("APPIMAGE", "/opt/stellar/StellarAllegianceServer.AppImage")),
                "/opt/stellar/run/usr/bin"
            ),
            "packaged: beside the APPIMAGE FILE, in one folder - not inside the package"
        );
        T.Eq(
            "/data/cache",
            SimAssets.ResolveCacheDir(Env(("APPIMAGE", "/opt/stellar/x.AppImage"), ("SIM_CACHE_DIR", "/data/cache")), "/x"),
            "an explicit SIM_CACHE_DIR always wins"
        );
        T.Eq(
            Path.Combine("/x", "sim-cache"),
            SimAssets.ResolveCacheDir(Env(("APPIMAGE", "")), "/x"),
            "an empty $APPIMAGE is not an install"
        );

        // The credential, the report spool and the update marker all chain off the cache dir.
        string? cacheBefore = Environment.GetEnvironmentVariable("SIM_CACHE_DIR");
        string? authBefore = Environment.GetEnvironmentVariable("SIM_AUTH_FILE");
        string? appImageBefore = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            Environment.SetEnvironmentVariable("SIM_CACHE_DIR", null);
            Environment.SetEnvironmentVariable("SIM_AUTH_FILE", null);
            Environment.SetEnvironmentVariable("APPIMAGE", "/opt/stellar/StellarAllegianceServer.AppImage");
            string data = Path.Combine("/opt/stellar", SimAssets.PackagedStateDirName);
            T.Eq(
                Path.Combine(data, "lobby-auth.json"),
                LobbyCredentialStore.ResolveDefaultPath(),
                "the lobby credential follows (no re-pairing after an update)"
            );
            T.Eq(
                data,
                LobbyMatchReporter.ResolveDefaultSpoolDir() is { } spool ? Path.GetDirectoryName(spool) : null,
                "the match-report spool follows"
            );
            T.Eq(
                Path.Combine(data, "update-attempt.json"),
                UpdateAttemptStore.ResolveDefaultPath(),
                "the update marker follows"
            );

            Environment.SetEnvironmentVariable("SIM_AUTH_FILE", "/data/lobby-auth.json");
            T.Eq(
                Path.Combine("/data", "update-attempt.json"),
                UpdateAttemptStore.ResolveDefaultPath(),
                "release image (SIM_AUTH_FILE=/data/…): the marker sits on the durable volume too"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIM_CACHE_DIR", cacheBefore);
            Environment.SetEnvironmentVariable("SIM_AUTH_FILE", authBefore);
            Environment.SetEnvironmentVariable("APPIMAGE", appImageBefore);
        }

        T.Section("state paths: the hull cache that ships with the build");
        string? assets = SimAssets.AssetsDir;
        string? glb = assets is null
            ? null
            : Directory
                .GetFiles(Path.Combine(assets, "ships"), "*.glb")
                .OrderBy(f => new FileInfo(f).Length)
                .FirstOrDefault();
        if (glb is null)
        {
            T.Check(false, "found a ship GLB to load (client/assets/ships)");
            return;
        }
        string root = T.TempDir("cache");
        try
        {
            string baked = Path.Combine(root, "baked");
            string writable = Path.Combine(root, "writable");
            string sidecar = Path.GetFileNameWithoutExtension(glb) + ".simmodel";

            var first = SimModelCache.Load(glb, baked);
            T.Check(File.Exists(Path.Combine(baked, sidecar)), "a cold load bakes the hull into the cache dir");

            var seeded = SimModelCache.Load(glb, writable, default, seedDir: baked);
            T.Check(
                !File.Exists(Path.Combine(writable, sidecar)),
                "a hit in the READ-ONLY seed dir is used as is: nothing recomputed, nothing copied"
            );
            T.Eq(first.Hull.Planes.Length, seeded.Hull.Planes.Length, "…and it is the same hull");

            var same = SimModelCache.Load(glb, baked, default, seedDir: baked);
            T.Eq(first.Hull.Planes.Length, same.Hull.Planes.Length, "seed dir == cache dir is fine");

            if (!OperatingSystem.IsWindows())
            {
                string locked = Path.Combine(root, "locked");
                Directory.CreateDirectory(locked);
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try
                {
                    var model = SimModelCache.Load(glb, Path.Combine(locked, "sim-cache"));
                    T.Eq(
                        first.Hull.Planes.Length,
                        model.Hull.Planes.Length,
                        "an UNWRITABLE cache dir still returns the model (it used to end in silent sphere collision)"
                    );
                }
                catch (Exception e)
                {
                    T.Check(false, $"an unwritable cache dir must not throw ({e.GetType().Name})");
                }
                finally
                {
                    File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        T.Section("update marker file");
        string dir = T.TempDir("marker");
        try
        {
            var store = new UpdateAttemptStore(Path.Combine(dir, "nested", "update-attempt.json"));
            T.Check(store.Load() is null, "no file = no memory of an attempt");
            store.Save(new UpdateAttempt("1.1.0", "1.0.0", 1));
            T.Eq(new UpdateAttempt("1.1.0", "1.0.0", 1), store.Load(), "round-trips (and creates its directory)");
            File.WriteAllText(Path.Combine(dir, "nested", "update-attempt.json"), "{ not json");
            T.Check(store.Load() is null, "a corrupt marker reads as none (worst case: one extra try)");
            store.Clear();
            store.Clear();
            T.Check(!File.Exists(Path.Combine(dir, "nested", "update-attempt.json")), "Clear is idempotent");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
