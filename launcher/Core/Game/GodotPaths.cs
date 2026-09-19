namespace StellarAllegiance.Launcher.Game;

// Where Godot keeps the GAME's user data (`user://`) — needed for exactly one thing: pointing the player
// at the game's log folder after a crash. The launcher never reads or writes anything in there (the
// game's settings.cfg and its auth.json refresh token live there).
//
// Valid while client/project.godot keeps `config/name="stellarallegiance"` and does not switch on
// `use_custom_user_dir` — tests/LauncherTest guards both.
public static class GodotPaths
{
    public const string ProjectName = "stellarallegiance";

    public static string UserDir(HostOs os)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return os switch
        {
            HostOs.Windows => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Godot",
                "app_userdata",
                ProjectName
            ),
            HostOs.MacOs => Path.Combine(home, "Library", "Application Support", "Godot", "app_userdata", ProjectName),
            _ => Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(home, ".local", "share"),
                "godot",
                "app_userdata",
                ProjectName
            ),
        };
    }

    public static string LogDir(HostOs os) => Path.Combine(UserDir(os), "logs");
}
