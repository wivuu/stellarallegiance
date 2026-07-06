using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;

namespace StellarAllegiance.Ui;

// The sector-map picker (the Claude Design "MATCH ROTATION / SELECT SECTOR MAP" mockup): dim
// scrim, centred bracket panel, a 2-up grid of map cards (thumbnail + metadata, an "IN PLAY" badge
// on the current map), and a footer. Reuses the SettingsDialog modal scaffold (scrim + centred
// BracketPanel + header ✕ + footer) and the SectorMapPreview widget for each card's thumbnail.
//
// Permission: only the match HOST (server-designated first pilot, GameNetClient.IsHost) may pick —
// they get CANCEL / SET MAP and clickable cards; everyone else gets a read-only preview with a
// locked notice and a CLOSE button. SET MAP sends the staged choice over the wire; the server
// advertises it as the next map (it does not rebuild the live arena — see the server MsgSetMap note).
public partial class MapPickerModal : Control
{
    public static bool Active { get; private set; }

    public static void Open(Node context, GameNetClient net)
    {
        if (Active)
            return;
        ModalHost.Ensure(context).AddChild(new MapPickerModal { _net = net });
    }

    private GameNetClient _net = null!;
    private string _pending = ""; // the staged map name (host clicks a card to change it)
    private GridContainer _grid = null!;
    private bool _closing;

    public override void _EnterTree() => Active = true;

    public override void _ExitTree() => Active = false;

    public override void _Ready()
    {
        _pending = _net.SelectedMap;
        BuildUi();
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private bool Host => _net.IsHost;

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();

        // Scrim: click-to-dismiss (a picker is a non-destructive preview — CANCEL semantics).
        var scrim = new ColorRect { Color = new Color(DesignTokens.Scrim, 0.82f), MouseFilter = MouseFilterEnum.Stop };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scrim.GuiInput += ev =>
        {
            if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                Close();
        };
        AddChild(scrim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var panel = new BracketPanel { FillOverride = DesignTokens.PanelDeep, CustomMinimumSize = new Vector2(760, 0) };
        center.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        panel.AddChild(col);

        BuildHeader(col);
        if (!Host)
            col.AddChild(LockedNotice());

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 360),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        col.AddChild(scroll);
        _grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 14);
        _grid.AddThemeConstantOverride("v_separation", 14);
        scroll.AddChild(_grid);
        RebuildCards();

        BuildFooter(col);
    }

    private void BuildHeader(VBoxContainer col)
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        col.AddChild(header);

        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 2);
        titles.AddChild(UiKit.MakeLabel("MATCH ROTATION", UiKit.TextStyle.Label, DesignTokens.TextDim));
        titles.AddChild(UiKit.MakeLabel("SELECT SECTOR MAP", UiKit.TextStyle.Title));
        header.AddChild(titles);

        var close = UiKit.MakeButton("✕", Close, ButtonVariant.Icon);
        close.CustomMinimumSize = new Vector2(34, 34);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        header.AddChild(close);
    }

    // Non-host banner: accent left-border notice (design's "only the host can change the map").
    private Control LockedNotice()
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.PanelFill, 0.6f), BorderColor = DesignTokens.Secondary, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = sb.ContentMarginRight = 14;
        sb.ContentMarginTop = sb.ContentMarginBottom = 12;
        panel.AddThemeStyleboxOverride("panel", sb);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        panel.AddChild(row);
        row.AddChild(UiKit.MakeLabel("⏻", UiKit.TextStyle.Body, DesignTokens.Secondary));
        var msg = UiKit.MakeLabel(
            "Only the match host can change the sector map. You can preview the rotation below.",
            UiKit.TextStyle.Data, DesignTokens.Text2);
        msg.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(msg);
        return panel;
    }

    private void RebuildCards()
    {
        foreach (var c in _grid.GetChildren())
            c.QueueFree();
        if (_net.Maps.Count == 0)
        {
            var empty = UiKit.MakeLabel("No maps advertised by this server.", UiKit.TextStyle.Body, DesignTokens.TextDim);
            _grid.AddChild(empty);
            return;
        }
        foreach (var m in _net.Maps)
            _grid.AddChild(MapCard(m));
    }

    private Control MapCard(MapInfo m)
    {
        bool current = string.Equals(m.Name, _net.SelectedMap, StringComparison.OrdinalIgnoreCase);
        bool selected = string.Equals(m.Name, _pending, StringComparison.OrdinalIgnoreCase);

        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var sb = new StyleBoxFlat
        {
            BgColor = selected ? new Color(DesignTokens.TeamAccent, 0.06f) : DesignTokens.PanelFill,
            BorderColor = selected ? DesignTokens.TeamAccent : DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        panel.AddThemeStyleboxOverride("panel", sb);
        // Only the host can stage a different map; non-host cards are inert previews.
        if (Host)
            panel.GuiInput += ev =>
            {
                if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    _pending = m.Name;
                    SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
                    RebuildCards();
                }
            };

        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 0);
        panel.AddChild(col);

        var thumb = new SectorMapPreview { CustomMinimumSize = new Vector2(0, 132), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        thumb.SetMap(m.Layout);
        col.AddChild(thumb);

        var footer = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        footer.AddThemeConstantOverride("margin_left", 12);
        footer.AddThemeConstantOverride("margin_right", 12);
        footer.AddThemeConstantOverride("margin_top", 10);
        footer.AddThemeConstantOverride("margin_bottom", 10);
        var frow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        frow.AddThemeConstantOverride("separation", 8);
        frow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        footer.AddChild(frow);

        var names = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 2);
        names.AddChild(UiKit.MakeLabel(m.Name, UiKit.TextStyle.Label, DesignTokens.TextHi));
        var meta = UiKit.MakeLabel(
            $"{m.Mode} · {m.SectorLabel} · {m.GarrisonCount} GARRISONS · {m.SizeLabel}",
            UiKit.TextStyle.Data, DesignTokens.Text2);
        meta.AddThemeFontSizeOverride("font_size", 10);
        names.AddChild(meta);
        frow.AddChild(names);

        // Badge: green "IN PLAY" on the current map, else the ◆ selection check.
        if (current)
        {
            frow.AddChild(Badge("IN PLAY", DesignTokens.Ok));
        }
        else
        {
            var check = UiKit.MakeLabel("◆", UiKit.TextStyle.Title, selected ? DesignTokens.TeamAccent : DesignTokens.BorderHi);
            check.MouseFilter = MouseFilterEnum.Ignore;
            check.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            frow.AddChild(check);
        }

        col.AddChild(footer);
        return panel;
    }

    private static Label Badge(string text, Color color)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, DesignTokens.Void);
        l.AddThemeFontSizeOverride("font_size", 9);
        var sb = new StyleBoxFlat { BgColor = color, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 7;
        sb.ContentMarginTop = sb.ContentMarginBottom = 2;
        l.AddThemeStyleboxOverride("normal", sb);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    private void BuildFooter(VBoxContainer col)
    {
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

        var note = UiKit.MakeLabel(
            Host ? "Applies to next match · all pilots notified" : "Read-only · host controls rotation",
            UiKit.TextStyle.Data, DesignTokens.Text2);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(note);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (Host)
        {
            row.AddChild(UiKit.MakeButton("CANCEL", Close, ButtonVariant.Secondary));
            row.AddChild(UiKit.MakeButton("SET MAP", ApplyAndClose, ButtonVariant.Primary));
        }
        else
        {
            row.AddChild(UiKit.MakeButton("CLOSE", Close, ButtonVariant.Primary));
        }
    }

    private void ApplyAndClose()
    {
        if (!string.IsNullOrEmpty(_pending))
            _net.SetMap(_pending);
        Close();
    }

    private void Close()
    {
        if (_closing)
            return;
        _closing = true;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        QueueFree();
    }
}
