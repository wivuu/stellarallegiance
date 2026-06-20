using Godot;

// Local, persistent player preferences — the project's first use of user:// storage. Backed by a
// Godot ConfigFile at user://settings.cfg (a real on-disk path per platform, e.g. macOS
// ~/Library/Application Support/Godot/app_userdata/<project>/). Today it holds only the pilot name
// the player typed on the start screen so it pre-fills next launch; audio / mouse settings (the
// "deferred" settings UI) can park their keys here later.
public static class UserPrefs
{
    private const string Path = "user://settings.cfg";
    private const string PlayerSection = "player";
    private const string NameKey = "name";

    // A pilot name is sent in MsgHello with a single-byte length prefix and floats above the ship as
    // a nameplate, so keep it short.
    public const int MaxNameLength = 24;

    private static ConfigFile? _cfg;

    private static ConfigFile Cfg
    {
        get
        {
            if (_cfg is not null) return _cfg;
            _cfg = new ConfigFile();
            // Load is best-effort: a missing file (first run) just leaves an empty config.
            _cfg.Load(Path);
            return _cfg;
        }
    }

    // The saved pilot name, or "" if none has been stored yet.
    public static string PilotName => (string)Cfg.GetValue(PlayerSection, NameKey, "");

    // Persist the pilot name (trimmed + clamped). Writes through to disk immediately so it survives
    // even if the game is force-quit before a clean shutdown.
    public static void SetPilotName(string name)
    {
        Cfg.SetValue(PlayerSection, NameKey, Clamp(name));
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            GD.PrintErr($"[UserPrefs] failed to save {Path}: {err}");
    }

    // Trim surrounding whitespace and cap the length so it fits the wire format and the nameplate.
    public static string Clamp(string name)
    {
        name = (name ?? "").Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }
}
