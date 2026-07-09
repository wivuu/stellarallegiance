using System;
using System.Collections.Generic;
using Godot;

namespace StellarAllegiance.Ui;

// The settings modal (the Claude Design "CONFIGURATION / SETTINGS" mockup): dim scrim,
// centred 720×560 bracket panel, header with an ✕ close, a left tab rail
// (AUDIO / CONTROLS / PILOT) beside the scrollable content, and a footer bar with
// RESTORE DEFAULTS · CANCEL · DONE. Opened from the screen gears, the escape menu, or the
// showcase; it mounts last on ModalHost's shared layer, so it draws and receives input
// above any EscapeMenu still open underneath.
//
// Apply semantics: every control writes through to UserPrefs IMMEDIATELY (volume drags are
// audible live, sensitivity is feelable), and a snapshot taken on open makes it revertable —
// CANCEL / ✕ / Esc re-apply the snapshot through the same setters. DONE just commits the
// pending callsign text and closes. RESTORE DEFAULTS resets audio + controls (never the
// callsign) and stays revertable by CANCEL, whose snapshot predates it.
public partial class SettingsDialog : Control
{
    public static bool Active { get; private set; }

    public static void Open(Node context, int startTab = 0)
    {
        if (Active)
            return;
        ModalHost.Ensure(context).AddChild(new SettingsDialog { _startTab = startTab });
    }

    // Which tab (0 AUDIO / 1 CONTROLS / 2 PILOT) to show on open; set by Open before _Ready.
    private int _startTab;

    // Snapshot taken on open — what CANCEL reverts to.
    private readonly Dictionary<string, float> _busSnapshot = new();
    private float _sensSnapshot;
    private bool _invertSnapshot;
    private string _nameSnapshot = "";

    // Live control refs so RESTORE DEFAULTS can drive what's on screen (their change
    // handlers write through to UserPrefs, so control state and prefs never diverge).
    private readonly Dictionary<string, HSlider> _busSliders = new();
    private HSlider _sensSlider = null!;
    private CheckButton _invert = null!;
    private LineEdit _callsign = null!;

    private readonly List<RailTab> _tabs = new();
    private readonly List<Control> _pages = new();
    private bool _closing;

    // Keybinding rows on the CONTROLS tab + the open-time override snapshot CANCEL reverts to, plus
    // the row currently listening for a new binding (null = not capturing).
    private readonly List<KeybindRow> _bindRows = new();
    private Dictionary<string, string[]> _bindSnapshot = new();
    private KeybindRow? _capturingRow;

    public override void _EnterTree() => Active = true;

    public override void _ExitTree() => Active = false;

    public override void _Ready()
    {
        foreach (string bus in UserPrefs.AudioBuses)
            _busSnapshot[bus] = UserPrefs.GetBusVolume(bus);
        _sensSnapshot = UserPrefs.MouseSensMultiplier;
        _invertSnapshot = UserPrefs.MouseInvertY;
        _nameSnapshot = UserPrefs.PilotName;
        _bindSnapshot = InputBindings.SnapshotOverrides();

        BuildUi();
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
    }

    public override void _Input(InputEvent @event)
    {
        // While a keybind row is listening, the next input event IS the new binding — swallow
        // everything else. Esc cancels the capture (not the dialog).
        if (_capturingRow != null)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                _capturingRow.SetCapturing(false);
                _capturingRow = null;
                GetViewport().SetInputAsHandled();
                return;
            }
            InputEvent? bind = InputBindings.NormalizeCaptured(@event);
            if (bind != null)
            {
                string? conflict = InputBindings.Rebind(_capturingRow.ActionId, bind);
                _capturingRow.SetCapturing(false);
                _capturingRow = null;
                foreach (var r in _bindRows)
                    r.Refresh(); // a conflict may have cleared another action's binding
                GetViewport().SetInputAsHandled();
                _ = conflict;
            }
            else if (@event is InputEventKey or InputEventMouseButton or InputEventJoypadButton)
            {
                // A press we can't bind (e.g. a mouse wheel) — consume so it doesn't leak, but keep listening.
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Cancel();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BeginCapture(KeybindRow row)
    {
        _capturingRow?.SetCapturing(false);
        _capturingRow = row;
        row.SetCapturing(true);
    }

    // ---- Layout ------------------------------------------------------------

    private void BuildUi()
    {
        // SetAnchorsAndOffsetsPreset, not SetAnchorsPreset — code-built overlays need the
        // offsets reset too or the root never fills the viewport (see ConnectLinkModal).
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        // Scrim: blocks the screen underneath but deliberately does NOT click-dismiss —
        // in-progress changes are protected; Esc / ✕ / CANCEL are the exits.
        var scrim = new ColorRect { Color = new Color(DesignTokens.Scrim, 0.82f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new BracketPanel { FillOverride = DesignTokens.PanelDeep, CustomMinimumSize = new Vector2(1080, 840) };
        center.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        panel.AddChild(col);

        BuildHeader(col);
        BuildBody(col);
        BuildFooter(col);

        SelectTab(Mathf.Clamp(_startTab, 0, _tabs.Count - 1));
    }

    private void BuildHeader(VBoxContainer col)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        col.AddChild(header);

        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 2);
        titles.AddChild(UiKit.MakeLabel("CONFIGURATION", UiKit.TextStyle.Label, DesignTokens.TextDim));
        titles.AddChild(UiKit.MakeLabel("SETTINGS", UiKit.TextStyle.Title));
        header.AddChild(titles);

        var close = UiKit.MakeButton("✕", Cancel, ButtonVariant.Icon);
        close.CustomMinimumSize = new Vector2(34, 34);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        header.AddChild(close);
    }

    private void BuildBody(VBoxContainer col)
    {
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14);
        col.AddChild(body);

        var rail = new VBoxContainer { CustomMinimumSize = new Vector2(172, 0) };
        rail.AddThemeConstantOverride("separation", 4);
        body.AddChild(rail);

        var divider = new ColorRect { Color = DesignTokens.BorderLo, CustomMinimumSize = new Vector2(1, 0) };
        body.AddChild(divider);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        body.AddChild(scroll);

        // ScrollContainer hosts exactly one child; the visibility-toggled tab pages stack
        // inside this wrapper.
        var host = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(host);

        AddTab(rail, host, "AUDIO", BuildAudioPage());
        AddTab(rail, host, "CONTROLS", BuildControlsPage());
        AddTab(rail, host, "PILOT", BuildPilotPage());
    }

    private void AddTab(VBoxContainer rail, VBoxContainer host, string title, Control page)
    {
        int idx = _tabs.Count;
        var tab = new RailTab { Title = title };
        tab.Pressed += () => SelectTab(idx);
        rail.AddChild(tab);
        _tabs.Add(tab);

        page.Visible = false;
        host.AddChild(page);
        _pages.Add(page);
    }

    private void BuildFooter(VBoxContainer col)
    {
        // Slightly-raised bar with a 1px top hairline separating it from the content.
        var footer = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(DesignTokens.PanelFill, 0.5f),
            BorderColor = DesignTokens.BorderLo,
            AntiAliasing = false,
            BorderWidthTop = 1,
        };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 12;
        sb.ContentMarginTop = sb.ContentMarginBottom = 10;
        footer.AddThemeStyleboxOverride("panel", sb);
        col.AddChild(footer);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        footer.AddChild(row);

        row.AddChild(UiKit.MakeButton("↺ RESTORE DEFAULTS", RestoreDefaults, ButtonVariant.Ghost));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(UiKit.MakeButton("CANCEL", Cancel, ButtonVariant.Secondary));
        row.AddChild(UiKit.MakeButton("DONE", Done, ButtonVariant.Primary));
    }

    // ---- Tab pages -----------------------------------------------------------

    private Control BuildAudioPage()
    {
        var page = MakePage();
        foreach (string bus in UserPrefs.AudioBuses)
        {
            var row = UiKit.MakeSliderRow(
                bus.ToUpperInvariant(),
                0,
                1,
                0.01,
                UserPrefs.GetBusVolume(bus),
                v => UserPrefs.SetBusVolume(bus, (float)v)
            );
            _busSliders[bus] = FindSlider(row);
            page.AddChild(row);
        }
        return page;
    }

    private Control BuildControlsPage()
    {
        var page = MakePage();

        var sensRow = UiKit.MakeSliderRow(
            "SENSITIVITY",
            0.1,
            3.0,
            0.05,
            UserPrefs.MouseSensMultiplier,
            v => UserPrefs.SetMouseSensMultiplier((float)v),
            readout: true,
            format: v => $"{v:0.00}×"
        );
        _sensSlider = FindSlider(sensRow);
        page.AddChild(sensRow);

        // Composed inline rather than via UiKit.MakeToggle — that puts the label inside the
        // CheckButton, the wrong shape for a settings row (label left, bare switch right).
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var text = new VBoxContainer();
        text.AddThemeConstantOverride("separation", 1);
        text.AddChild(UiKit.MakeLabel("INVERT Y-AXIS", UiKit.TextStyle.Label, DesignTokens.TextHi));
        text.AddChild(UiKit.MakeLabel("mouse pitch", UiKit.TextStyle.Data, DesignTokens.TextDim));
        row.AddChild(text);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        _invert = new CheckButton { ButtonPressed = UserPrefs.MouseInvertY, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _invert.Toggled += on => UserPrefs.SetMouseInvertY(on);
        row.AddChild(_invert);
        page.AddChild(row);

        // Key bindings — one KeybindRow per rebindable InputMap action, grouped by category. Click a
        // row's button to rebind; RESTORE DEFAULTS / CANCEL cover these alongside the mouse controls.
        page.AddChild(new DiamondDivider());
        page.AddChild(UiKit.MakeLabel("KEY BINDINGS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        page.AddChild(UiKit.MakeLabel("Click a control to rebind it — key, mouse button, or gamepad.", UiKit.TextStyle.Data, DesignTokens.TextDim));
        AddBindGroup(page, "FLIGHT", InputBindings.Category.Flight);
        AddBindGroup(page, "COMBAT", InputBindings.Category.Combat);
        AddBindGroup(page, "VIEW", InputBindings.Category.View);

        return page;
    }

    private void AddBindGroup(Control page, string title, InputBindings.Category cat)
    {
        var panel = new HairlinePanel { Title = title };
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        foreach (InputBindings.Action a in InputBindings.All)
        {
            if (a.Cat != cat)
                continue;
            var r = new KeybindRow { ActionId = a.Id, Display = a.Display };
            r.CaptureRequested += BeginCapture;
            _bindRows.Add(r);
            v.AddChild(r);
        }
        panel.AddChild(v);
        page.AddChild(panel);
    }

    private Control BuildPilotPage()
    {
        var page = MakePage();

        var group = new VBoxContainer();
        group.AddThemeConstantOverride("separation", 8);
        page.AddChild(group);

        group.AddChild(UiKit.MakeLabel("CALLSIGN", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _callsign = new LineEdit
        {
            Text = UserPrefs.PilotName,
            PlaceholderText = "your callsign",
            MaxLength = UserPrefs.MaxNameLength,
            CustomMinimumSize = new Vector2(240, 34),
        };
        _callsign.AddThemeFontOverride("font", UiFonts.Mono);
        _callsign.AddThemeFontSizeOverride("font_size", 15);
        _callsign.FocusExited += CommitCallsign;
        _callsign.TextSubmitted += _ => CommitCallsign();
        group.AddChild(_callsign);

        // The name only goes out in MsgHello, so a change never renames mid-match.
        group.AddChild(UiKit.MakeLabel("Takes effect next time you connect.", UiKit.TextStyle.Data, DesignTokens.TextDim));

        return page;
    }

    private static VBoxContainer MakePage()
    {
        var page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 20);
        return page;
    }

    private static HSlider FindSlider(Node row)
    {
        foreach (var child in row.GetChildren())
            if (child is HSlider s)
                return s;
        throw new InvalidOperationException("UiKit.MakeSliderRow returned a row without an HSlider");
    }

    // ---- Actions -------------------------------------------------------------

    private void SelectTab(int idx)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            _tabs[i].SetSelected(i == idx);
            _pages[i].Visible = i == idx;
        }
    }

    private void Done()
    {
        CommitCallsign();
        Close();
    }

    // CANCEL / header ✕ / Esc: re-apply the snapshot through the setters, so the revert is
    // as live as the changes were (volumes audibly snap back).
    private void Cancel()
    {
        if (_closing)
            return;
        _capturingRow?.SetCapturing(false);
        _capturingRow = null;
        foreach (var (bus, v) in _busSnapshot)
            UserPrefs.SetBusVolume(bus, v);
        UserPrefs.SetMouseSensMultiplier(_sensSnapshot);
        UserPrefs.SetMouseInvertY(_invertSnapshot);
        UserPrefs.SetPilotName(_nameSnapshot);
        InputBindings.RestoreOverrides(_bindSnapshot);
        Close();
    }

    // Drive the visible controls back to the defaults; their change handlers re-fire into
    // the UserPrefs setters (idempotent when already there). Deliberately leaves the
    // callsign alone, and CANCEL still reverts this — the snapshot predates it.
    private void RestoreDefaults()
    {
        foreach (string bus in UserPrefs.AudioBuses)
            _busSliders[bus].Value = UserPrefs.DefaultBusVolume(bus);
        _sensSlider.Value = UserPrefs.DefaultMouseSensMultiplier;
        _invert.ButtonPressed = UserPrefs.DefaultMouseInvertY; // assigning emits Toggled on change
        InputBindings.ResetAll();
        foreach (var r in _bindRows)
            r.Refresh();
    }

    private void CommitCallsign()
    {
        // FocusExited also fires during teardown — never re-commit after a Cancel revert.
        if (_closing)
            return;
        UserPrefs.SetPilotName(_callsign.Text);
    }

    private void Close()
    {
        if (_closing)
            return;
        _closing = true;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        QueueFree();
    }

    // ---- Tab rail --------------------------------------------------------------

    // One rail entry, drawn custom (like ServerLobbyOverlay.ServerRow) so the active state
    // matches the mockup: a 3px accent bar + faint accent fill + "▸ "-prefixed bright text.
    // Fonts come from UiFonts, not the Theme — custom-draw nodes don't get the cascade.
    private sealed partial class RailTab : Button
    {
        public string Title = "";

        private bool _selected;

        public override void _Ready()
        {
            UiFonts.EnsureLoaded();
            FocusMode = FocusModeEnum.None;
            CustomMinimumSize = new Vector2(0, 40);
            SizeFlagsHorizontal = SizeFlags.ExpandFill;
            foreach (string s in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                AddThemeStyleboxOverride(s, new StyleBoxEmpty());
            MouseEntered += QueueRedraw;
            MouseExited += QueueRedraw;
            Pressed += () => SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        }

        public void SetSelected(bool selected)
        {
            if (_selected == selected)
                return;
            _selected = selected;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var r = new Rect2(Vector2.Zero, Size);
            if (_selected)
            {
                DrawRect(r, new Color(DesignTokens.TeamAccent, 0.10f), filled: true);
                DrawRect(new Rect2(0, 0, 3, Size.Y), DesignTokens.TeamAccent, filled: true);
            }
            else if (IsHovered())
            {
                DrawRect(r, new Color(DesignTokens.TeamAccent, 0.05f), filled: true);
            }

            Font f = UiFonts.SairaLabel;
            int fs = DesignTokens.LabelSize;
            float baseline = (Size.Y - (f.GetAscent(fs) + f.GetDescent(fs))) * 0.5f + f.GetAscent(fs);
            DrawString(
                f,
                new Vector2(14, Mathf.Round(baseline)),
                _selected ? "▸ " + Title : Title,
                HorizontalAlignment.Left,
                -1,
                fs,
                _selected ? DesignTokens.TextHi : DesignTokens.Text2
            );
        }
    }
}
