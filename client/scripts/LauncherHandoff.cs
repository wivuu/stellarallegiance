using System;
using System.IO;
using System.Linq;
using Godot;
using StellarAllegiance.Shared;

// The game's side of the Game Launcher handoff (contract: shared/LauncherContract.cs).
//
// Installed builds are started by the launcher (launcher/, the Avalonia app that owns Velopack installs
// and updates). It tells us so through the environment, and we tell it things through our EXIT CODE —
// the game itself has no Velopack dependency and never touches its own installation.
//
// Absent env = a dev run, the Godot editor or a plain zip build: every member here is inert and the
// server browser keeps its notify-only "download" banner.
public static class LauncherHandoff
{
    public static bool UnderLauncher => OS.GetEnvironment(LauncherContract.EnvFlag).Length > 0;

    // What the launcher already learned from the release feed, so we don't spend a second GitHub API
    // call (60/hour/IP, shared with the launcher) asking the same question:
    //   a version → that release is available · "none" → checked, nothing newer · null → it doesn't know
    public static string? KnownUpdate =>
        UnderLauncher && OS.GetEnvironment(LauncherContract.EnvUpdate) is { Length: > 0 } v ? v : null;

    public static bool UpdateKnownAbsent => KnownUpdate == LauncherContract.EnvUpdateNone;

    public static string? KnownUpdateVersion => KnownUpdate is { } v && v != LauncherContract.EnvUpdateNone ? v : null;

    // A Dock icon ("Keep in Dock") or a taskbar pin made while PLAYING points at the game binary inside
    // the package, not at the launcher — and would silently skip updates forever. If we find ourselves
    // started that way, hand the same command line to the launcher and step aside.
    //
    // Returns true when the redirect was started (the caller should quit). Never fires in the editor, in
    // dev runs, when SA_NO_LAUNCHER_REDIRECT=1 (the escape hatch for debugging the packaged binary), or
    // under `--verify-assets`: that run is the packaging gate asking THIS binary about ITS OWN files, from
    // inside the package stage — which looks exactly like a Dock-icon start. Redirecting there opened the
    // staged launcher's window on the build machine (against the developer's real launcher state) while
    // the check it was supposed to be ran to completion beside it.
    public static bool TryRedirectToLauncher()
    {
        if (
            UnderLauncher
            || OS.HasFeature("editor")
            || AssetVerify.Requested
            || OS.GetEnvironment(LauncherContract.EnvNoRedirect) == "1"
        )
            return false;

        string exe = OS.GetExecutablePath().Replace('\\', '/');
        string[] args =
        [
            .. OS.GetCmdlineArgs(),
            .. OS.GetCmdlineUserArgs() is { Length: > 0 } user ? new[] { "--" }.Concat(user) : [],
        ];

        // macOS: <Outer>.app/Contents/Helpers/<Game>.app/Contents/MacOS/<binary> → relaunch <Outer>.app
        const string nested = ".app/Contents/Helpers/";
        int helpers = exe.IndexOf(nested, StringComparison.Ordinal);
        if (OS.GetName() == "macOS" && helpers > 0)
        {
            string outer = exe[..(helpers + ".app".Length)];
            GD.Print($"[LauncherHandoff] started without the launcher — relaunching through {outer}");
            return OS.CreateProcess("/usr/bin/open", ["-n", outer, "--args", .. args]) > 0;
        }

        // Windows: <root>\current\game\<game>.exe → relaunch <root>\current\StellarLauncher.exe
        if (OS.GetName() == "Windows")
        {
            string? gameDir = Path.GetDirectoryName(exe);
            string? parent = gameDir is null ? null : Path.GetDirectoryName(gameDir);
            if (parent is not null && Path.GetFileName(gameDir) == "game")
            {
                string launcher = Path.Combine(parent, "StellarLauncher.exe");
                if (File.Exists(launcher))
                {
                    GD.Print($"[LauncherHandoff] started without the launcher — relaunching through {launcher}");
                    return OS.CreateProcess(launcher, args) > 0;
                }
            }
        }
        return false;
    }
}
