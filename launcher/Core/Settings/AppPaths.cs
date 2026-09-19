namespace StellarAllegiance.Launcher.Settings;

// Where the launcher keeps ITS OWN files (settings, log, single-instance lock).
//
// Deliberately NOT inside the install directory (Velopack swaps that wholesale on every update and
// owns %LocalAppData%\<packId> on Windows) and NOT Godot's `user://` directory (the game's settings
// and its auth.json refresh token live there — the launcher never reads or writes them).
//
//   Windows  %APPDATA%\StellarAllegiance
//   macOS    ~/Library/Application Support/StellarAllegiance
//   Linux    $XDG_CONFIG_HOME/StellarAllegiance  (~/.config/…)
//
// `--launcher-data=<dir>` overrides the root so tests and CI runs are hermetic.
public sealed record AppPaths(string Root)
{
    public const string FolderName = "StellarAllegiance";

    public string SettingsFile => Path.Combine(Root, "launcher.json");
    public string LockFile => Path.Combine(Root, "launcher.lock");
    public string GamePidFile => Path.Combine(Root, "game.pid");
    public string LogDir => Path.Combine(Root, "logs");
    public string LogFile => Path.Combine(LogDir, "launcher.log");

    public static AppPaths Resolve(string? overrideRoot)
    {
        string root = !string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.GetFullPath(overrideRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return new AppPaths(root);
    }
}
