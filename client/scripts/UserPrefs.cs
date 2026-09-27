using Godot;

// Local, persistent player preferences — the project's first use of user:// storage. Backed by a
// Godot ConfigFile at user://settings.cfg (a real on-disk path per platform, e.g. macOS
// ~/Library/Application Support/Godot/app_userdata/<project>/). Holds the pilot name the player
// typed on the start screen, the per-bus audio volumes, the display prefs, and the mouse-feel prefs
// the settings dialog drives (SettingsDialog reads and writes everything here).
public static class UserPrefs
{
    private const string Path = "user://settings.cfg";
    private const string PlayerSection = "player";
    private const string NameKey = "name";
    private const string LastShipKey = "last_ship";
    private const string AudioSection = "audio";
    private const string InputSection = "input";
    private const string MouseSensKey = "mouse_sens_mult";
    private const string InvertYKey = "invert_y";
    private const string ViewSection = "view";
    private const string FirstPersonKey = "first_person";
    private const string DisplaySection = "display";
    private const string DisplayModeKey = "window_mode";
    private const string VsyncKey = "vsync";
    private const string MaxFpsKey = "max_fps";
    private const string RenderScaleKey = "render_scale";
    private const string UiScaleKey = "ui_scale";
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

    // Persist Cfg to disk and log on failure. Every setter funnels its save+error-log through here.
    private static void Save()
    {
        var err = Cfg.Save(Path);
        if (err != Error.Ok)
            Log.Err($"[UserPrefs] failed to save {Path}: {err}");
    }

    // The saved pilot name, or "" if none has been stored yet.
    public static string PilotName => (string)Cfg.GetValue(PlayerSection, NameKey, "");

    // Persist the pilot name (trimmed + clamped). Writes through to disk immediately so it survives
    // even if the game is force-quit before a clean shutdown.
    public static void SetPilotName(string name)
    {
        Cfg.SetValue(PlayerSection, NameKey, Clamp(name));
        Save();
        Changed?.Invoke();
    }

    // Trim surrounding whitespace and cap the length so it fits the wire format and the nameplate.
    public static string Clamp(string name)
    {
        name = (name ?? "").Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    // The hull class the pilot last docked with, or -1 if none has been stored yet. The hangar's
    // ship picker defaults its highlighted card to this so a pilot relaunches in the same ship they
    // last flew, unless they pick another — the next dock updates it. Purely a UI default: the sim
    // still validates the class the client sends at spawn. Stored as a long (ConfigFile has no byte).
    public static int LastShip => (int)(long)Cfg.GetValue(PlayerSection, LastShipKey, (long)-1);

    // Persist the last-docked hull class. Writes through immediately like SetPilotName so the
    // preference survives even a force-quit right after docking.
    public static void SetLastShip(byte classId)
    {
        Cfg.SetValue(PlayerSection, LastShipKey, (long)classId);
        Save();
        Changed?.Invoke();
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
        Save();
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
        Save();
        Changed?.Invoke();
    }

    // Whether mouse pitch is inverted (push forward = nose down).
    public static bool MouseInvertY => (bool)Cfg.GetValue(InputSection, InvertYKey, DefaultMouseInvertY);

    // Persist the invert-Y toggle. Writes through immediately like SetPilotName.
    public static void SetMouseInvertY(bool v)
    {
        Cfg.SetValue(InputSection, InvertYKey, v);
        Save();
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
        Save();
        Changed?.Invoke();
    }

    // ---- Display -------------------------------------------------------------
    // The settings VIDEO tab: window mode, vsync, frame cap, 3D render scale and UI scale. Every setter
    // writes through and applies live; ApplyDisplayPrefs restores them at boot. Godot never changes the
    // display's video mode (both fullscreen modes run at desktop resolution), so the GPU-cost lever is
    // render scale — the root viewport's Scaling3DScale — not a resolution list.

    public enum DisplayMode
    {
        Windowed,
        Borderless, // Godot's WINDOW_MODE_FULLSCREEN: a borderless window covering the screen
        Fullscreen, // WINDOW_MODE_EXCLUSIVE_FULLSCREEN
    }

    public enum VsyncMode
    {
        Off,
        On,
        Adaptive,
    }

    public const DisplayMode DefaultDisplayMode = DisplayMode.Windowed;
    public const VsyncMode DefaultVsync = VsyncMode.On; // the project default
    public const int DefaultMaxFps = 0; // 0 = uncapped
    public const float DefaultRenderScale = 1f;
    public const float MinRenderScale = 0.5f;
    public const float UiScaleAuto = 0f;
    public const float DefaultUiScale = UiScaleAuto;

    // The choices the VIDEO tab offers, in display order.
    public static readonly int[] MaxFpsOptions = { 0, 60, 120, 144, 240 };
    public static readonly float[] UiScaleOptions = { UiScaleAuto, 1f, 1.25f, 1.5f, 1.75f, 2f };

    // UI scale never shrinks the logical canvas below the 1920×1080 the UI is laid out for (the
    // settings panel alone is 1080×840), so no pick can push a dialog's buttons off-screen. AUTO scales
    // against the 2560×1440 design reference (project.godot's viewport) in 25% steps, never below 100%.
    private const float MinLogicalWidth = 1920f;
    private const float MinLogicalHeight = 1080f;
    private const float DesignHeight = 1440f;

    // Every display apply is skipped headless (--headless, asset verify): there is no window to drive.
    // The setters still persist.
    private static bool CanDriveWindow => DisplayServer.GetName() != "headless";

    private static Window? Root => (Engine.GetMainLoop() as SceneTree)?.Root;

    private static T GetEnum<T>(string key, T fallback)
        where T : struct, System.Enum =>
        System.Enum.TryParse((string)Cfg.GetValue(DisplaySection, key, ""), ignoreCase: true, out T v)
        && System.Enum.IsDefined(v)
            ? v
            : fallback;

    // Enums persist as their lower-case names, so reordering an enum never reinterprets a saved cfg.
    private static void SetEnum<T>(string key, T v)
        where T : struct, System.Enum => Cfg.SetValue(DisplaySection, key, v.ToString().ToLowerInvariant());

    public static DisplayMode DisplayModePref => GetEnum(DisplayModeKey, DefaultDisplayMode);

    // The window's LIVE mode. The OS can change it behind the pref's back (macOS's green button), so
    // the settings dialog shows and snapshots this, not DisplayModePref.
    public static DisplayMode CurrentDisplayMode =>
        !CanDriveWindow
            ? DisplayModePref
            : DisplayServer.WindowGetMode() switch
            {
                DisplayServer.WindowMode.Fullscreen => DisplayMode.Borderless,
                DisplayServer.WindowMode.ExclusiveFullscreen => DisplayMode.Fullscreen,
                _ => DisplayMode.Windowed, // windowed / maximized / minimized
            };

    public static void SetDisplayMode(DisplayMode m)
    {
        SetEnum(DisplayModeKey, m);
        Save();
        ApplyDisplayMode(m);
        Changed?.Invoke();
    }

    // Only switches when the live mode differs — a fullscreen transition is slow and visible.
    private static void ApplyDisplayMode(DisplayMode m)
    {
        if (!CanDriveWindow || CurrentDisplayMode == m)
            return;
        switch (m)
        {
            case DisplayMode.Borderless:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                break;
            case DisplayMode.Fullscreen:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
                break;
            default:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                // Going fullscreen forces the borderless flag on; hand the title bar back.
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                break;
        }
    }

    public static VsyncMode Vsync => GetEnum(VsyncKey, DefaultVsync);

    public static void SetVsync(VsyncMode v)
    {
        SetEnum(VsyncKey, v);
        Save();
        ApplyVsync(v);
        Changed?.Invoke();
    }

    private static void ApplyVsync(VsyncMode v)
    {
        if (!CanDriveWindow)
            return;
        DisplayServer.WindowSetVsyncMode(
            v switch
            {
                VsyncMode.Off => DisplayServer.VSyncMode.Disabled,
                VsyncMode.Adaptive => DisplayServer.VSyncMode.Adaptive,
                _ => DisplayServer.VSyncMode.Enabled,
            }
        );
    }

    // Frame cap in fps (0 = uncapped) — the GPU-draw lever when vsync is off.
    public static int MaxFps =>
        Mathf.Clamp((int)(long)Cfg.GetValue(DisplaySection, MaxFpsKey, (long)DefaultMaxFps), 0, 1000);

    public static void SetMaxFps(int fps)
    {
        fps = Mathf.Clamp(fps, 0, 1000);
        Cfg.SetValue(DisplaySection, MaxFpsKey, (long)fps);
        Save();
        if (CanDriveWindow)
            Engine.MaxFps = fps;
        Changed?.Invoke();
    }

    // 3D render scale as a fraction of the window's pixels; the 2D UI always draws at full resolution.
    public static float RenderScale =>
        Mathf.Clamp(
            (float)(double)Cfg.GetValue(DisplaySection, RenderScaleKey, (double)DefaultRenderScale),
            MinRenderScale,
            1f
        );

    public static void SetRenderScale(float s)
    {
        s = Mathf.Clamp(s, MinRenderScale, 1f);
        Cfg.SetValue(DisplaySection, RenderScaleKey, s);
        Save();
        ApplyRenderScale(s);
        Changed?.Invoke();
    }

    private static void ApplyRenderScale(float s)
    {
        if (!CanDriveWindow || Root is not { } root)
            return;
        // FSR 1.0 upscales far cleaner than bilinear; at 100% there is nothing to upscale.
        root.Scaling3DMode = s < 1f ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        root.Scaling3DScale = s;
    }

    // The chosen UI scale (1 = 100%), or UiScaleAuto.
    public static float UiScale
    {
        get
        {
            float v = (float)(double)Cfg.GetValue(DisplaySection, UiScaleKey, (double)DefaultUiScale);
            return v <= 0f ? UiScaleAuto : Mathf.Clamp(v, 1f, 2f);
        }
    }

    public static void SetUiScale(float scale)
    {
        Cfg.SetValue(DisplaySection, UiScaleKey, scale <= 0f ? UiScaleAuto : Mathf.Clamp(scale, 1f, 2f));
        Save();
        TrackUiScale();
        Changed?.Invoke();
    }

    // The largest UI scale that keeps the logical canvas at least MinLogicalWidth×MinLogicalHeight.
    public static float UiScaleCap(Vector2I window) =>
        Mathf.Max(1f, Mathf.Min(window.X / MinLogicalWidth, window.Y / MinLogicalHeight));

    public static float AutoUiScale(Vector2I window) => Mathf.Max(1f, Mathf.Floor(window.Y / DesignHeight * 4f) / 4f);

    // What a UI scale pref resolves to for a window of this (physical) size: AUTO resolved, then capped.
    public static float EffectiveUiScale(float pref, Vector2I window) =>
        Mathf.Min(pref <= 0f ? AutoUiScale(window) : pref, UiScaleCap(window));

    // The UI scale actually in force (the root's content scale factor).
    public static float AppliedUiScale => Root?.ContentScaleFactor ?? 1f;

    private static bool _uiScaleTracked;

    // Apply the UI scale and keep it tracking the window: a mode switch or resize re-resolves AUTO and
    // re-caps. Stretch mode stays disabled — ContentScaleFactor alone scales the 2D canvas, while the 3D
    // view keeps rendering at the window's full size.
    private static void TrackUiScale()
    {
        if (!CanDriveWindow || Root is not { } root)
            return;
        if (!_uiScaleTracked)
        {
            _uiScaleTracked = true;
            root.SizeChanged += ApplyUiScale;
        }
        ApplyUiScale();
    }

    private static void ApplyUiScale()
    {
        if (Root is not { } root)
            return;
        Vector2I size = root.Size; // physical pixels — unaffected by the content scale
        if (size.X <= 0 || size.Y <= 0)
            return; // minimized
        float f = EffectiveUiScale(UiScale, size); // Assigning ContentScaleFactor re-emits SizeChanged (whose handler is this method), so only
        // write a real change or it recurses forever.
        if (!Mathf.IsEqualApprox(root.ContentScaleFactor, f))
            root.ContentScaleFactor = f;
    }

    // Push every saved display pref to the window. Call once at boot (ConnectionManager._Ready), before
    // the first frame. A no-op headless and on harness runs — see IsPlayerLaunch.
    public static void ApplyDisplayPrefs()
    {
        if (!CanDriveWindow)
            return;
        // Test hook: SA_DISPLAY_PREFS=1 opts a harness run back in, so a --ui-shot capture can verify
        // the display prefs themselves.
        bool forced = System.Environment.GetEnvironmentVariable("SA_DISPLAY_PREFS") == "1";
        if (!forced && !IsPlayerLaunch())
        {
            Log.Print("[display] harness run — keeping the project window (SA_DISPLAY_PREFS=1 overrides)");
            return;
        }
        ApplyDisplayMode(DisplayModePref);
        ApplyVsync(Vsync);
        Engine.MaxFps = MaxFps;
        ApplyRenderScale(RenderScale);
        TrackUiScale();
        Log.Print(
            $"[display] {DisplayModePref} vsync={Vsync} max-fps={MaxFps} render={RenderScale:0.00} "
                + $"ui={(UiScale <= 0f ? "auto" : UiScale.ToString("0.00"))}→{AppliedUiScale:0.00} window={Root?.Size}"
        );
    }

    // Boot restores the saved display only on a PLAYER launch. Harness and capture runs keep the
    // project's window, or a dev's saved fullscreen / 150% would leak into --ui-shot captures,
    // --write-movie output and turret-test.ps1's side-by-side 1280×720 windows. This is an allow-list
    // of player flags rather than a list of harness flags: the launcher passes none (it marks itself
    // with an env var), a direct-join shortcut passes only these, while ShipController alone has a
    // dozen autofly-implying harness flags. OS.GetCmdlineArgs excludes engine flags (--resolution,
    // --fullscreen, --write-movie), so those can't be keyed off directly.
    private static bool IsPlayerLaunch()
    {
        if (Engine.GetWriteMoviePath().Length > 0)
            return false;
        if (OS.GetCmdlineUserArgs().Length > 0)
            return false; // everything after `--` is UI-harness (the "Client CLI flags split" convention)
        foreach (string a in OS.GetCmdlineArgs())
        {
            if (!a.StartsWith("--"))
                continue; // a flag's value, e.g. the address after --host
            string flag = a.Split('=', 2)[0];
            if (flag is not ("--host" or "--lobby" or "--join-listing" or "--anonymous"))
                return false;
        }
        return true;
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

    private static void SaveBindings() => Save();

    // NOTE: hangar loadouts are deliberately NOT persisted here — they are per-match and live only in
    // LoadoutState.Shared for the current process, reset at each match boundary (see LoadoutState).
}
