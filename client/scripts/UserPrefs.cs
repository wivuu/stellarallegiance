using Godot;

// Local, persistent player preferences — the project's first use of user:// storage. Backed by a
// Godot ConfigFile at user://settings.cfg (a real on-disk path per platform, e.g. macOS
// ~/Library/Application Support/Godot/app_userdata/<project>/). Holds the pilot name the player
// typed on the start screen, the per-bus audio volumes, and the mouse-feel prefs the settings
// dialog drives (SettingsDialog reads and writes everything here).
public static class UserPrefs
{
    private const string Path = "user://settings.cfg";
    private const string PlayerSection = "player";
    private const string NameKey = "name";
    private const string AudioSection = "audio";
    private const string InputSection = "input";
    private const string MouseSensKey = "mouse_sens_mult";
    private const string InvertYKey = "invert_y";
    private const string ViewSection = "view";
    private const string FirstPersonKey = "first_person";
    private const string BindingsSection = "bindings";

    // The audio buses the settings sliders drive, mirroring the buses SfxManager/EngineGlow use.
    // Each stores a 0..1 linear volume (1 = full); applied as dB to the matching Godot bus.
    public static readonly string[] AudioBuses = { "Master", "SFX", "Engines", "Ambient", "UI" };

    // Defaults the settings dialog's RESTORE DEFAULTS lands on.
    public const float DefaultMouseSensMultiplier = 1f;
    public const bool DefaultMouseInvertY = false;
    public const bool DefaultFirstPersonView = true;

    // Raised at the end of every setter so live consumers (ShipController mouse feel, the server
    // browser's name field) can re-read. Setters only run on the main thread, so subscribers may
    // touch the scene tree directly.
    public static event System.Action? Changed;

    // A pilot name is sent in MsgHello with a single-byte length prefix and floats above the ship as
    // a nameplate, so keep it short.
    public const int MaxNameLength = 24;

    private static ConfigFile? _cfg;

    private static ConfigFile Cfg
    {
        get
        {
            if (_cfg is not null)
                return _cfg;
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
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
        Changed?.Invoke();
    }

    // Trim surrounding whitespace and cap the length so it fits the wire format and the nameplate.
    public static string Clamp(string name)
    {
        name = (name ?? "").Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    // Authored linear volume per bus, captured from the audio server before the first ApplyBus.
    // default_bus_layout.tres ships offsets (Engines −3 dB, Ambient −12 dB, UI −4 dB); defaulting
    // to 1.0 and applying it would stomp that mix at startup, so the authored values are the real
    // first-run defaults.
    private static System.Collections.Generic.Dictionary<string, float>? _audioDefaults;

    // Snapshot each bus's authored volume on first use. Safe ordering: SfxManager._Ready calls
    // ApplyAudioPrefs() (which lands here first) before any sound plays or slider is touched.
    private static void EnsureAudioDefaults()
    {
        if (_audioDefaults is not null)
            return;
        _audioDefaults = new System.Collections.Generic.Dictionary<string, float>();
        foreach (var bus in AudioBuses)
        {
            int idx = AudioServer.GetBusIndex(bus);
            if (idx < 0)
                continue; // bus not in the layout — DefaultBusVolume falls back to 1
            _audioDefaults[bus] = Mathf.Clamp(Mathf.DbToLinear(AudioServer.GetBusVolumeDb(idx)), 0f, 1f);
        }
    }

    // The authored 0..1 linear volume for a bus — what RESTORE DEFAULTS lands on (1 = full for a
    // bus the layout doesn't define).
    public static float DefaultBusVolume(string bus)
    {
        EnsureAudioDefaults();
        return _audioDefaults!.TryGetValue(bus, out float v) ? v : 1f;
    }

    // Saved 0..1 linear volume for a bus (defaults to the authored layout volume on first run).
    public static float GetBusVolume(string bus)
    {
        EnsureAudioDefaults();
        return Mathf.Clamp((float)(double)Cfg.GetValue(AudioSection, bus, DefaultBusVolume(bus)), 0f, 1f);
    }

    // Persist a bus volume and apply it live. Writes through immediately like SetPilotName.
    public static void SetBusVolume(string bus, float linear)
    {
        EnsureAudioDefaults();
        linear = Mathf.Clamp(linear, 0f, 1f);
        Cfg.SetValue(AudioSection, bus, linear);
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
        ApplyBus(bus, linear);
        Changed?.Invoke();
    }

    // Push every saved bus volume to the audio server. Call once at startup so persisted
    // settings take effect before any sound plays.
    public static void ApplyAudioPrefs()
    {
        EnsureAudioDefaults();
        foreach (var bus in AudioBuses)
            ApplyBus(bus, GetBusVolume(bus));
    }

    private static void ApplyBus(string bus, float linear)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx < 0)
            return; // bus not in the layout — skip rather than crash (no-fallback discipline)
        // Linear 0 → muted; otherwise map to dB. LinearToDb(0) is -inf, so mute explicitly.
        if (linear <= 0f)
            AudioServer.SetBusMute(idx, true);
        else
        {
            AudioServer.SetBusMute(idx, false);
            AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(linear));
        }
    }

    // Mouse-look sensitivity as a multiplier over ShipController's baseline (clamped so a stray
    // config edit can't make the ship unflyable).
    public static float MouseSensMultiplier =>
        Mathf.Clamp((float)(double)Cfg.GetValue(InputSection, MouseSensKey, (double)DefaultMouseSensMultiplier), 0.1f, 3f);

    // Persist the sensitivity multiplier. Writes through immediately like SetPilotName.
    public static void SetMouseSensMultiplier(float v)
    {
        Cfg.SetValue(InputSection, MouseSensKey, Mathf.Clamp(v, 0.1f, 3f));
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
        Changed?.Invoke();
    }

    // Whether mouse pitch is inverted (push forward = nose down).
    public static bool MouseInvertY => (bool)Cfg.GetValue(InputSection, InvertYKey, DefaultMouseInvertY);

    // Persist the invert-Y toggle. Writes through immediately like SetPilotName.
    public static void SetMouseInvertY(bool v)
    {
        Cfg.SetValue(InputSection, InvertYKey, v);
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
        Changed?.Invoke();
    }

    // Whether the chase camera spawns in first person (cockpit view). Default true — the pilot's-eye
    // view is the intended default; the last mode the player toggled to persists across sessions.
    public static bool FirstPersonView => (bool)Cfg.GetValue(ViewSection, FirstPersonKey, DefaultFirstPersonView);

    // Persist the first-person view preference. Writes through immediately like SetPilotName so the
    // last-used mode survives even a force-quit.
    public static void SetFirstPersonView(bool v)
    {
        Cfg.SetValue(ViewSection, FirstPersonKey, v);
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
        Changed?.Invoke();
    }

    // ---- Control bindings ----------------------------------------------------
    // Per-action keybinding overrides for the InputMap actions, stored as a list of compact
    // event strings (encoding owned by InputBindings, which is the only caller). A row exists
    // only for an action the player has changed away from its default — the InputMap itself is
    // the live source of truth, this is just the persistence layer (no Changed event needed:
    // InputBindings applies edits to the InputMap directly).

    // The saved override for an action, or an empty array if the action uses its default.
    public static string[] GetBinding(string action)
    {
        Variant v = Cfg.GetValue(BindingsSection, action, new Godot.Collections.Array());
        if (v.VariantType != Variant.Type.Array)
            return System.Array.Empty<string>();
        var arr = v.AsGodotArray();
        var res = new string[arr.Count];
        for (int i = 0; i < arr.Count; i++)
            res[i] = arr[i].AsString();
        return res;
    }

    public static bool HasBinding(string action) => Cfg.HasSectionKey(BindingsSection, action);

    // Persist an action's override event list. Writes through immediately like the other setters.
    public static void SetBinding(string action, string[] encoded)
    {
        var arr = new Godot.Collections.Array();
        foreach (string s in encoded)
            arr.Add(s);
        Cfg.SetValue(BindingsSection, action, arr);
        SaveBindings();
    }

    // Drop an action's override (it reverts to the compiled-in default). No-op if none stored.
    public static void ClearBinding(string action)
    {
        if (!Cfg.HasSectionKey(BindingsSection, action))
            return;
        Cfg.EraseSectionKey(BindingsSection, action);
        SaveBindings();
    }

    private static void SaveBindings()
    {
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
    }
}
