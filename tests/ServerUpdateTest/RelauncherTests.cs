using System.Diagnostics;
using SimServer.Update;

// SIM_UPDATE_RESTART=relaunch (server/Update/Relauncher.cs): the helper that starts the new build once the
// old one is gone. What matters to an operator: it WAITS for the old process, and the new build gets the
// same arguments - byte for byte - in the same working directory. The descriptor half of the story (no
// stale AppImage mount) needs a real FUSE mount and lives in scripts/server-update-e2e.ps1.
// Marker files instead of path comparisons: temp paths are symlinked on macOS (/var -> /private/var).
static class RelauncherTests
{
    public static void Run()
    {
        T.Section("relaunch helper: waits, then starts the new build where and how the old one ran");
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("  skip (needs /bin/sh; the packaged server is Linux-only)");
            return;
        }

        string startDir = T.TempDir("start");
        File.WriteAllText(Path.Combine(startDir, "marker-start"), "");
        string outDir = T.TempDir("out");
        string app = Path.Combine(outDir, "new-build.sh"); // stands in for the swapped AppImage
        File.WriteAllText(
            app,
            """
            #!/bin/sh
            if kill -0 "$SA_TEST_OLD_PID" 2>/dev/null; then echo running; else echo gone; fi > "$SA_TEST_OUT/old-build"
            ls > "$SA_TEST_OUT/cwd"
            for a in "$@"; do printf '[%s]\n' "$a"; done > "$SA_TEST_OUT/args"
            touch "$SA_TEST_OUT/done"

            """.ReplaceLineEndings("\n")
        );
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var oldBuild = Process.Start(new ProcessStartInfo("/bin/sleep", "3") { UseShellExecute = false })!;
        Environment.SetEnvironmentVariable("SA_TEST_OLD_PID", oldBuild.Id.ToString());
        Environment.SetEnvironmentVariable("SA_TEST_OUT", outDir);
        string[] args = ["--port", "8090", "--content", "my content/core.manifest.yaml", "quo\"te", "$HOME", "*"];
        var clock = Stopwatch.StartNew();
        try
        {
            using var helper = Relauncher.Start(oldBuild.Id, app, args, startDir);
            Thread.Sleep(1200);
            T.Check(!File.Exists(Path.Combine(outDir, "done")), "nothing is started while the old build still runs");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(Path.Combine(outDir, "done")) && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            T.Check(File.Exists(Path.Combine(outDir, "done")), "the new build is started");
            T.Check(
                clock.Elapsed > TimeSpan.FromSeconds(2.5),
                $"... only after the old one exited ({clock.Elapsed.TotalSeconds:0.0}s)"
            );
            T.Eq("gone", Read("old-build"), "... which was really gone by then");
            T.Eq("marker-start", Read("cwd"), "in the working directory it was given - not the helper's, not the package's");
            T.Eq(
                string.Join("\n", args.Select(a => $"[{a}]")),
                Read("args"),
                "with the same arguments, spaces, quotes, $ and * untouched"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("SA_TEST_OLD_PID", null);
            Environment.SetEnvironmentVariable("SA_TEST_OUT", null);
        }

        string Read(string name)
        {
            string path = Path.Combine(outDir, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "<missing>";
        }
    }
}
