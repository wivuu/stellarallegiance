using System;
using System.Collections.Generic;
using Godot;

// Client-only keybinding layer. Every rebindable flight/combat/view control is read through
// Godot's InputMap (Input.GetAxis / Input.IsActionPressed / InputEvent.IsActionPressed) instead
// of hardcoded Key.X polls, so players can remap them (and use a gamepad) from Settings → Controls.
//
// Defaults are single-sourced HERE, built from the Key/JoyButton/JoyAxis enums and registered into
// the InputMap at runtime — no fragile project.godot literals. Overrides persist in UserPrefs'
// [bindings] section as compact event strings and are re-applied on top of the defaults at boot
// (Apply). Bindings are purely a local input concern (no wire/sim/server involvement), so the
// headless AutoInput() path, determinism, and the dotnet suites are untouched.
public static class InputBindings
{
    public enum Category
    {
        Flight,
        Combat,
        View,
    }

    public sealed record Action(string Id, string Display, Category Cat);

    // The rebindable catalog in UI display order. Paired flight axes are listed so the two halves
    // sit together; ShipController reads each pair via Input.GetAxis(negative, positive) preserving
    // the old ShipController.Axis(pos, neg) sign conventions (see ReadInput).
    public static readonly Action[] All =
    {
        new("thrust_forward", "Throttle Up", Category.Flight),
        new("thrust_back", "Throttle Down / Reverse", Category.Flight),
        new("strafe_left", "Strafe Left", Category.Flight),
        new("strafe_right", "Strafe Right", Category.Flight),
        new("strafe_up", "Strafe Up", Category.Flight),
        new("strafe_down", "Strafe Down", Category.Flight),
        new("yaw_left", "Yaw Left", Category.Flight),
        new("yaw_right", "Yaw Right", Category.Flight),
        new("pitch_up", "Pitch Up", Category.Flight),
        new("pitch_down", "Pitch Down", Category.Flight),
        new("roll_left", "Roll Left", Category.Flight),
        new("roll_right", "Roll Right", Category.Flight),
        new("fire_primary", "Fire Primary", Category.Combat),
        new("fire_secondary", "Fire Secondary / Missile", Category.Combat),
        new("afterburner", "Afterburner", Category.Combat),
        new("drop_chaff", "Drop Chaff", Category.Combat),
        new("drop_mine", "Drop Mine", Category.Combat),
        new("drop_probe", "Deploy Probe", Category.Combat),
        new("cycle_target", "Cycle Target", Category.Combat),
        new("toggle_view", "Toggle View", Category.View),
        new("scope_zoom_in", "Scope / Zoom In", Category.View),
        new("scope_zoom_out", "Scope Zoom Out", Category.View),
    };

    // Low deadzone so an analog flight stick uses most of its throw (buttons/keys ignore this).
    private const float Deadzone = 0.2f;

    private static Dictionary<string, List<InputEvent>>? _defaults;
    private static bool _initialized;

    // ---- Boot / lazy init ----------------------------------------------------

    // Register the actions + apply persisted overrides. Called at boot (SfxManager._Ready) before
    // any frame reads input; also self-heals via EnsureInit so the settings dialog works in the
    // --ui-showcase boot where SfxManager isn't in the scene.
    public static void Apply() => EnsureInit();

    private static void EnsureInit()
    {
        if (_initialized)
            return;
        _defaults = BuildDefaults();
        foreach (Action a in All)
            RegisterEvents(a.Id, LoadOrDefault(a.Id));
        _initialized = true;
    }

    private static List<InputEvent> LoadOrDefault(string action)
    {
        if (UserPrefs.HasBinding(action))
            return DecodeEvents(UserPrefs.GetBinding(action));
        return CloneDefaults(action);
    }

    // ---- Query / describe ----------------------------------------------------

    // Current events bound to an action, as a fresh mutable list.
    public static List<InputEvent> CurrentEvents(string action)
    {
        EnsureInit();
        var list = new List<InputEvent>();
        if (InputMap.HasAction(action))
            foreach (InputEvent e in InputMap.ActionGetEvents(action))
                list.Add(e);
        return list;
    }

    // Short human label for the UI, e.g. "W", "SPACE", "LMB", "PAD A / LS◂".
    public static string Describe(string action)
    {
        var parts = new List<string>();
        foreach (InputEvent e in CurrentEvents(action))
        {
            string? s = Label(e);
            if (s != null)
                parts.Add(s);
        }
        return parts.Count > 0 ? string.Join("  /  ", parts) : "—";
    }

    // ---- Rebind / reset ------------------------------------------------------

    // Bind ev to action, replacing the existing same-device (keyboard/mouse OR gamepad) event so a
    // key rebind keeps the gamepad binding and vice-versa. Any other action already holding the
    // exact same event is cleared (returned so the UI can refresh it). Persists both sides.
    public static string? Rebind(string action, InputEvent ev)
    {
        EnsureInit();
        string enc = Encode(ev)!;
        string cls = DeviceClass(ev);

        string? conflict = null;
        foreach (Action a in All)
        {
            if (a.Id == action)
                continue;
            var evs = CurrentEvents(a.Id);
            if (evs.RemoveAll(e => Encode(e) == enc) > 0)
            {
                RegisterEvents(a.Id, evs);
                Persist(a.Id, evs);
                conflict = a.Id;
            }
        }

        var cur = CurrentEvents(action);
        cur.RemoveAll(e => DeviceClass(e) == cls);
        cur.Add(ev);
        RegisterEvents(action, cur);
        Persist(action, cur);
        return conflict;
    }

    public static void ResetToDefault(string action)
    {
        EnsureInit();
        RegisterEvents(action, CloneDefaults(action));
        UserPrefs.ClearBinding(action);
    }

    public static void ResetAll()
    {
        foreach (Action a in All)
            ResetToDefault(a.Id);
    }

    // ---- Snapshot / revert (for the settings dialog CANCEL) ------------------

    public static Dictionary<string, string[]> SnapshotOverrides()
    {
        EnsureInit();
        var d = new Dictionary<string, string[]>();
        foreach (Action a in All)
            d[a.Id] = EncodeEvents(CurrentEvents(a.Id));
        return d;
    }

    public static void RestoreOverrides(Dictionary<string, string[]> snapshot)
    {
        EnsureInit();
        foreach (Action a in All)
        {
            if (!snapshot.TryGetValue(a.Id, out string[]? enc))
                continue;
            var evs = DecodeEvents(enc);
            RegisterEvents(a.Id, evs);
            Persist(a.Id, evs);
        }
    }

    // Turn a raw captured event into the canonical event to bind, or null if it isn't a valid
    // single binding (release/echo, mouse motion, wheel, tiny stick deflection). Used by the
    // settings dialog's capture mode.
    public static InputEvent? NormalizeCaptured(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } k:
                Key pk = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
                return pk == Key.None ? null : new InputEventKey { PhysicalKeycode = pk };
            case InputEventMouseButton { Pressed: true } m
                when m.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight):
                return new InputEventMouseButton { ButtonIndex = m.ButtonIndex };
            case InputEventJoypadButton { Pressed: true } jb:
                return new InputEventJoypadButton { ButtonIndex = jb.ButtonIndex };
            case InputEventJoypadMotion jm when Mathf.Abs(jm.AxisValue) > 0.6f:
                return new InputEventJoypadMotion { Axis = jm.Axis, AxisValue = jm.AxisValue < 0 ? -1f : 1f };
            default:
                return null;
        }
    }

    // ---- Internals -----------------------------------------------------------

    private static void RegisterEvents(string action, List<InputEvent> events)
    {
        if (!InputMap.HasAction(action))
            InputMap.AddAction(action, Deadzone);
        InputMap.ActionEraseEvents(action);
        foreach (InputEvent e in events)
            InputMap.ActionAddEvent(action, e);
    }

    // Persist an action's events, but drop the row entirely when it matches the default so
    // RESTORE DEFAULTS (and rebinding back to default) leaves a clean settings.cfg.
    private static void Persist(string action, List<InputEvent> events)
    {
        string[] enc = EncodeEvents(events);
        string[] def = EncodeEvents(CloneDefaults(action));
        if (SequenceEqual(enc, def))
            UserPrefs.ClearBinding(action);
        else
            UserPrefs.SetBinding(action, enc);
    }

    private static List<InputEvent> CloneDefaults(string action)
    {
        var list = new List<InputEvent>();
        if (_defaults != null && _defaults.TryGetValue(action, out var evs))
            list.AddRange(evs);
        return list;
    }

    private static string DeviceClass(InputEvent e) =>
        e is InputEventJoypadButton or InputEventJoypadMotion ? "pad" : "kbm";

    private static bool SequenceEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    // ---- Encode / decode (compact strings for ConfigFile) --------------------

    private static string[] EncodeEvents(List<InputEvent> events)
    {
        var res = new List<string>();
        foreach (InputEvent e in events)
        {
            string? s = Encode(e);
            if (s != null)
                res.Add(s);
        }
        return res.ToArray();
    }

    private static List<InputEvent> DecodeEvents(string[] encoded)
    {
        var res = new List<InputEvent>();
        foreach (string s in encoded)
        {
            InputEvent? e = Decode(s);
            if (e != null)
                res.Add(e);
        }
        return res;
    }

    private static string? Encode(InputEvent e) =>
        e switch
        {
            InputEventKey k => $"k:{(int)(k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode)}",
            InputEventMouseButton m => $"m:{(int)m.ButtonIndex}",
            InputEventJoypadButton jb => $"jb:{(int)jb.ButtonIndex}",
            InputEventJoypadMotion jm => $"ja:{(int)jm.Axis}:{(jm.AxisValue < 0 ? -1 : 1)}",
            _ => null,
        };

    private static InputEvent? Decode(string s)
    {
        string[] p = s.Split(':');
        switch (p[0])
        {
            case "k":
                return new InputEventKey { PhysicalKeycode = (Key)int.Parse(p[1]) };
            case "m":
                return new InputEventMouseButton { ButtonIndex = (MouseButton)int.Parse(p[1]) };
            case "jb":
                return new InputEventJoypadButton { ButtonIndex = (JoyButton)int.Parse(p[1]) };
            case "ja":
                return new InputEventJoypadMotion { Axis = (JoyAxis)int.Parse(p[1]), AxisValue = int.Parse(p[2]) };
            default:
                return null;
        }
    }

    private static string? Label(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey k:
                Key pk = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
                string s = OS.GetKeycodeString(pk);
                return string.IsNullOrEmpty(s) ? null : s.ToUpperInvariant();
            case InputEventMouseButton m:
                return m.ButtonIndex switch
                {
                    MouseButton.Left => "LMB",
                    MouseButton.Right => "RMB",
                    MouseButton.Middle => "MMB",
                    _ => $"MOUSE {(int)m.ButtonIndex}",
                };
            case InputEventJoypadButton jb:
                return "PAD " + jb.ButtonIndex;
            case InputEventJoypadMotion jm:
                return $"PAD {jm.Axis}{(jm.AxisValue < 0 ? "◂" : "▸")}";
            default:
                return null;
        }
    }

    // ---- Default bindings (single source of truth) ---------------------------

    private static Dictionary<string, List<InputEvent>> BuildDefaults()
    {
        var d = new Dictionary<string, List<InputEvent>>();

        void Add(string a, InputEvent e)
        {
            if (!d.TryGetValue(a, out var list))
                d[a] = list = new List<InputEvent>();
            list.Add(e);
        }
        void K(string a, Key k) => Add(a, new InputEventKey { PhysicalKeycode = k });
        void Pad(string a, JoyButton b) => Add(a, new InputEventJoypadButton { ButtonIndex = b });
        void Ax(string a, JoyAxis ax, float sign) => Add(a, new InputEventJoypadMotion { Axis = ax, AxisValue = sign });

        // Flight (keyboard defaults preserve today's ShipController.Axis() layout; joypad = a
        // conventional flight-stick starter mapping, easy to retune here).
        K("thrust_forward", Key.W); Ax("thrust_forward", JoyAxis.TriggerRight, 1f);
        K("thrust_back", Key.S); Ax("thrust_back", JoyAxis.TriggerLeft, 1f);
        K("strafe_right", Key.A); Pad("strafe_right", JoyButton.DpadRight);
        K("strafe_left", Key.D); Pad("strafe_left", JoyButton.DpadLeft);
        K("strafe_up", Key.X);
        K("strafe_down", Key.Z);
        K("yaw_left", Key.Left); Ax("yaw_left", JoyAxis.LeftX, -1f);
        K("yaw_right", Key.Right); Ax("yaw_right", JoyAxis.LeftX, 1f);
        K("pitch_up", Key.Up); Ax("pitch_up", JoyAxis.LeftY, -1f);
        K("pitch_down", Key.Down); Ax("pitch_down", JoyAxis.LeftY, 1f);
        K("roll_right", Key.E); Ax("roll_right", JoyAxis.RightX, 1f);
        K("roll_left", Key.Q); Ax("roll_left", JoyAxis.RightX, -1f);

        // Combat.
        K("fire_primary", Key.Space); Pad("fire_primary", JoyButton.RightShoulder);
        K("fire_secondary", Key.F); Pad("fire_secondary", JoyButton.LeftShoulder);
        K("afterburner", Key.Shift); Pad("afterburner", JoyButton.A);
        K("drop_chaff", Key.C); Pad("drop_chaff", JoyButton.B);
        K("drop_mine", Key.B); Pad("drop_mine", JoyButton.X);
        K("drop_probe", Key.G); Pad("drop_probe", JoyButton.Y);
        K("cycle_target", Key.Tab); Pad("cycle_target", JoyButton.RightStick);

        // View (scope zoom stays keyboard-only by default — a niche control).
        K("toggle_view", Key.V); Pad("toggle_view", JoyButton.DpadUp);
        K("scope_zoom_in", Key.Equal);
        Add("scope_zoom_in", new InputEventKey { PhysicalKeycode = Key.KpAdd });
        K("scope_zoom_out", Key.Minus);
        Add("scope_zoom_out", new InputEventKey { PhysicalKeycode = Key.KpSubtract });

        return d;
    }
}
