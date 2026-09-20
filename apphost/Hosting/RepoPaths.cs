namespace StellarAllegiance.AppHost.Hosting;

// Well-known paths, all derived from the AppHost directory (apphost/ sits directly under the repo root).
public sealed record RepoPaths(string Root)
{
    public string ClientDir => Path.Combine(Root, "client");
    public string ClientProject => Path.Combine(ClientDir, "stellarallegiance.csproj");
    public string EnvFile => Path.Combine(Root, ".env");

    // Game Launcher (Avalonia): unlike the Godot client, the AppHost runs its BUILT BINARY directly
    // (see Hosting/GameLauncher.cs) rather than a `dotnet run`-style host, so the resource needs the
    // exact output path.
    public string LauncherDir => Path.Combine(Root, "launcher", "App");
    public string LauncherProject => Path.Combine(LauncherDir, "StellarLauncher.csproj");
    public string LauncherExe =>
        Path.Combine(
            LauncherDir,
            "bin",
            "Debug",
            "net10.0",
            OperatingSystem.IsWindows() ? "StellarLauncher.exe" : "StellarLauncher"
        );

    // Gitignored per-checkout state the AppHost owns: the sim server's hull cache and its lobby
    // device-code credential live here so `dotnet clean` / a Debug<->Release switch never re-pairs.
    public string LocalDir => Path.Combine(Root, "apphost", ".local");
    public string ServerLocalDir => Path.Combine(LocalDir, "server");

    // Each Game Launcher instance takes a single-instance lock in its data dir (see launcher/README.md),
    // so `main` (the tracked resource) and each `show`-command view/shot get their OWN subdirectory here.
    public string LauncherLocalDir => Path.Combine(LocalDir, "launcher");

    public static RepoPaths From(string appHostDirectory) => new(Path.GetFullPath(Path.Combine(appHostDirectory, "..")));
}
