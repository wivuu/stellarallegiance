using System.Runtime.InteropServices;

namespace StellarAllegiance.Launcher.Game;

public enum HostOs
{
    Windows,
    MacOs,
    Linux,
}

public sealed record GameLocation(string ExePath, string WorkingDir, bool Exists);

// Where the game lives relative to the launcher inside one Velopack package. The Godot export is
// shipped UNTOUCHED (Godot finds `<exe-basename>.pck` and its `data_*` .NET folder next to the
// binary), so these paths are a packaging contract — scripts/package-clients.ps1 assembles exactly this:
//
//   Windows  current\StellarLauncher.exe             → current\game\stellarallegiance.exe
//   Linux    usr/bin/StellarLauncher (in AppImage)   → usr/bin/game/stellarallegiance.x86_64
//   macOS    Stellar Allegiance.app/Contents/MacOS/StellarLauncher
//              → Contents/Helpers/Stellar Allegiance.app/Contents/MacOS/stellarallegiance
//            (a pristine, separately-signed Godot .app nested in the launcher's bundle; exec'ing its
//            inner binary directly gives the game its own Dock identity AND gives us its exit code —
//            `open -W` would discard the code).
public static class GameLocator
{
    public const string GameExeBase = "stellarallegiance";
    public const string MacGameBundle = "Stellar Allegiance.app";

    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsMacOS() ? HostOs.MacOs
        : HostOs.Linux;

    public static string DefaultExePath(string launcherDir, HostOs os) =>
        os switch
        {
            HostOs.Windows => Path.Combine(launcherDir, "game", GameExeBase + ".exe"),
            HostOs.Linux => Path.Combine(launcherDir, "game", GameExeBase + ".x86_64"),
            _ => Path.GetFullPath(
                Path.Combine(launcherDir, "..", "Helpers", MacGameBundle, "Contents", "MacOS", GameExeBase)
            ),
        };

    public static GameLocation Resolve(string launcherDir, HostOs os, string? overridePath)
    {
        string exe = !string.IsNullOrWhiteSpace(overridePath)
            ? ResolveOverride(overridePath)
            : DefaultExePath(launcherDir, os);
        string workDir = Path.GetDirectoryName(exe) is { Length: > 0 } d ? d : launcherDir;
        return new GameLocation(exe, workDir, File.Exists(exe));
    }

    // macOS runs a quarantined app that was never moved out of Downloads from a read-only, randomised
    // mount ("App Translocation"). Velopack cannot replace a bundle there, so updates must be disabled
    // and the player told to move the app — playing still works.
    public static bool IsTranslocated(string path) => path.Contains("/AppTranslocation/", StringComparison.Ordinal);

    // `--launcher-game=` accepts a path or a bare command on PATH (dev: `--launcher-game=godot-mono --path client`).
    private static string ResolveOverride(string value)
    {
        if (
            value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || File.Exists(value)
        )
            return Path.GetFullPath(value);
        string[] exts = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["", ".exe", ".cmd", ".bat"] : [""];
        foreach (
            var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        foreach (var ext in exts)
        {
            string candidate = Path.Combine(dir, value + ext);
            if (File.Exists(candidate))
                return candidate;
        }
        return value;
    }
}
