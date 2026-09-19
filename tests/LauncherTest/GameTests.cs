using StellarAllegiance.Launcher.Game;
using StellarAllegiance.Shared;

static class GameTests
{
    public static void Run()
    {
        T.Section("GameExitCodes");
        T.Eq(GameExitKind.Quit, GameExitCodes.Classify(0, HostOs.Windows), "0 = quit");
        T.Eq(
            GameExitKind.UpdateRequested,
            GameExitCodes.Classify(LauncherContract.UpdateExitCode, HostOs.Linux),
            "85 = UPDATE NOW"
        );
        T.Eq(
            85,
            LauncherContract.UpdateExitCode,
            "the contract value is pinned (the shipped game and launcher must agree forever)"
        );
        T.Eq(GameExitKind.Crashed, GameExitCodes.Classify(1, HostOs.MacOs), "1 = crash (harness failure paths use Quit(1))");
        T.Eq(GameExitKind.Crashed, GameExitCodes.Classify(139, HostOs.Linux), "SIGSEGV = crash");
        T.Eq(GameExitKind.Quit, GameExitCodes.Classify(130, HostOs.MacOs), "SIGINT on Unix = told to stop, not a crash");
        T.Eq(GameExitKind.Quit, GameExitCodes.Classify(143, HostOs.Linux), "SIGTERM (logout) on Unix = not a crash");
        T.Eq(GameExitKind.Crashed, GameExitCodes.Classify(143, HostOs.Windows), "…but 143 means nothing on Windows");
        T.Eq(
            GameExitKind.Quit,
            GameExitCodes.Classify(unchecked((int)0xC000013A), HostOs.Windows),
            "STATUS_CONTROL_C_EXIT on Windows = not a crash"
        );
        T.Eq(
            GameExitKind.Crashed,
            GameExitCodes.Classify(unchecked((int)0xC0000005), HostOs.Windows),
            "access violation = crash"
        );

        T.Section("GameLocator");
        string sep = Path.DirectorySeparatorChar.ToString();
        T.Eq(
            Path.Combine("C:" + sep + "app" + sep + "current", "game", "stellarallegiance.exe"),
            GameLocator.DefaultExePath("C:" + sep + "app" + sep + "current", HostOs.Windows),
            "Windows: current\\game\\stellarallegiance.exe"
        );
        T.Eq(
            Path.Combine("/tmp/x/usr/bin", "game", "stellarallegiance.x86_64"),
            GameLocator.DefaultExePath("/tmp/x/usr/bin", HostOs.Linux),
            "Linux: usr/bin/game/stellarallegiance.x86_64"
        );
        if (!OperatingSystem.IsWindows())
        {
            T.Eq(
                "/Applications/Stellar Allegiance.app/Contents/Helpers/Stellar Allegiance.app/Contents/MacOS/stellarallegiance",
                GameLocator.DefaultExePath("/Applications/Stellar Allegiance.app/Contents/MacOS", HostOs.MacOs),
                "macOS: the pristine game bundle nested under Contents/Helpers"
            );
        }
        T.Check(
            GameLocator.IsTranslocated(
                "/private/var/folders/xx/T/AppTranslocation/ABC/d/Stellar Allegiance.app/Contents/MacOS"
            ),
            "a translocated path is recognised"
        );
        T.Check(!GameLocator.IsTranslocated("/Applications/Stellar Allegiance.app/Contents/MacOS"), "a normal path is not");

        string dir = T.TempDir("locator");
        try
        {
            string exe = Path.Combine(dir, "mygame");
            File.WriteAllText(exe, "");
            var found = GameLocator.Resolve(dir, GameLocator.CurrentOs, exe);
            T.Check(
                found.Exists && found.ExePath == exe && found.WorkingDir == dir,
                "--launcher-game=<path> overrides the location; working dir = its folder"
            );
            T.Check(!GameLocator.Resolve(dir, GameLocator.CurrentOs, null).Exists, "a missing game is reported, not thrown");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
