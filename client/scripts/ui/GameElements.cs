using System;
using System.Collections.Generic;
using Godot;

namespace StellarAllegiance.Ui;

// ── 06 GAME ELEMENTS ─────────────────────────────────────────────────────────

// Loadout slot — an accent-framed card for an equipped weapon/module. Optionally
// interactive (the hangar's hardpoint rows): set Accent/Selected and listen to Pressed.
public partial class LoadoutSlot : PanelContainer
{
    private Label _slot = null!;
    private Label _name = null!;
    private Label _stats = null!;

    public Color Accent = DesignTokens.TeamAccent;

    // Left-click anywhere on the card. Only meaningful when a handler is attached; the
    // static showcase cards simply never connect it.
    public event Action? Pressed;

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            if (_name != null)
                Restyle();
        }
    }

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_name != null)
            return;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        _slot = UiKit.MakeLabel("PRIMARY  ◆", UiKit.TextStyle.Data);
        _slot.AddThemeColorOverride("font_color", DesignTokens.Data);
        _slot.AddThemeFontSizeOverride("font_size", 10);
        _name = UiKit.MakeLabel("", UiKit.TextStyle.Body);
        _name.AddThemeFontOverride("font", UiFonts.SairaSemi);
        _stats = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _stats.AddThemeColorOverride("font_color", DesignTokens.Text2);
        _stats.AddThemeFontSizeOverride("font_size", 10);
        col.AddChild(_slot);
        col.AddChild(_name);
        col.AddChild(_stats);
        AddChild(col);
        Restyle();
    }

    private void Restyle()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(Accent, _selected ? 0.18f : 0.10f),
            BorderColor = new Color(Accent, _selected ? 1f : 0.45f),
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.BorderWidthLeft = 3;
        sb.SetContentMarginAll(12);
        AddThemeStyleboxOverride("panel", sb);
        _slot.AddThemeColorOverride("font_color", Accent);
    }

    public void Configure(string slotLabel, string name, string stats)
    {
        EnsureBuilt();
        _slot.Text = slotLabel;
        _name.Text = name;
        _stats.Text = stats;
        _stats.Visible = !string.IsNullOrEmpty(stats);
        Restyle();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
            Pressed?.Invoke();
            AcceptEvent();
        }
    }
}

// Contact chip — a small hostile/target card with corner brackets, range, and a tracking bar.
public partial class ContactChip : Control
{
    private string _name = "";
    private string _info = "";
    private float _bar; // 0..1
    private Color _accent = DesignTokens.Danger;

    public override void _Ready()
    {
        UiFonts.EnsureLoaded();
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(150, 72);
        Resized += QueueRedraw;
    }

    public void Set(string name, string info, float bar01, bool hostile = true)
    {
        _name = name;
        _info = info;
        _bar = Mathf.Clamp(bar01, 0f, 1f);
        _accent = hostile ? DesignTokens.Danger : DesignTokens.Ok;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, new Color(DesignTokens.Void, 0.7f), filled: true);
        UiDraw.CornerBrackets(this, r, 12f, _accent, 2f);

        var diamond = new Vector2(14, 16);
        const float s = 4.5f;
        DrawColoredPolygon(
            new[] { diamond + new Vector2(0, -s), diamond + new Vector2(s, 0), diamond + new Vector2(0, s), diamond + new Vector2(-s, 0) },
            _accent
        );
        DrawString(UiFonts.SairaSemi, new Vector2(26, 21), _name, HorizontalAlignment.Left, -1, 13, DesignTokens.TextHi);
        DrawString(UiFonts.Mono, new Vector2(12, 40), _info, HorizontalAlignment.Left, -1, 10, DesignTokens.DangerText);

        var track = new Rect2(12, 52, Size.X - 24, 4);
        DrawRect(track, new Color(DesignTokens.BorderLo, 0.6f), filled: true);
        DrawRect(new Rect2(track.Position, new Vector2(track.Size.X * _bar, track.Size.Y)), DesignTokens.TeamAccent, filled: true);
    }
}

// Resource readout — a bordered symbol box, a mono value, and a per-second rate.
public partial class ResourceReadout : HBoxContainer
{
    private Label _symbol = null!;
    private Label _value = null!;
    private Label _rate = null!;
    private Color _color = DesignTokens.Secondary;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_value != null)
            return;
        AddThemeConstantOverride("separation", 10);
        _symbol = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _symbol.HorizontalAlignment = HorizontalAlignment.Center;
        _symbol.VerticalAlignment = VerticalAlignment.Center;
        _symbol.CustomMinimumSize = new Vector2(30, 30);
        var sb = new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = _color, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        _symbol.AddThemeStyleboxOverride("normal", sb);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        _value = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _value.AddThemeFontSizeOverride("font_size", 18);
        _rate = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _rate.AddThemeFontSizeOverride("font_size", 9);
        _rate.AddThemeColorOverride("font_color", DesignTokens.TextDim);
        col.AddChild(_value);
        col.AddChild(_rate);

        AddChild(_symbol);
        AddChild(col);
    }

    public void Set(string symbol, string value, string rate, Color? color = null)
    {
        EnsureBuilt();
        _color = color ?? DesignTokens.Secondary;
        _symbol.Text = symbol;
        _symbol.AddThemeColorOverride("font_color", _color);
        var sb = (StyleBoxFlat)_symbol.GetThemeStylebox("normal");
        sb.BorderColor = _color;
        _value.Text = value;
        _value.AddThemeColorOverride("font_color", _color);
        _rate.Text = rate;
    }
}

// Radar frame — concentric range rings, crosshair, centre marker, and faction blips.
public partial class RadarFrame : Control
{
    private readonly List<(Vector2 pos, Color color)> _blips = new();

    public override void _Ready()
    {
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(140, 140);
        Resized += QueueRedraw;
    }

    // Blips in normalised radar space: (0,0) centre, components in [-1, 1].
    public void SetBlips(IEnumerable<(Vector2 pos, Color color)> blips)
    {
        _blips.Clear();
        _blips.AddRange(blips);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        var center = Size * 0.5f;
        float radius = Mathf.Min(Size.X, Size.Y) * 0.5f;
        DrawRect(r, DesignTokens.BorderHi, filled: false, 1f);
        DrawArc(center, radius * 0.66f, 0, Mathf.Pi * 2f, 48, new Color(DesignTokens.BorderLo, 0.7f), 1f, true);
        DrawArc(center, radius * 0.33f, 0, Mathf.Pi * 2f, 48, new Color(DesignTokens.BorderLo, 0.7f), 1f, true);
        DrawLine(new Vector2(center.X, 0), new Vector2(center.X, Size.Y), new Color(DesignTokens.BorderLo, 0.7f), 1f);
        DrawLine(new Vector2(0, center.Y), new Vector2(Size.X, center.Y), new Color(DesignTokens.BorderLo, 0.7f), 1f);

        DrawSelfDiamond(center, 4f, DesignTokens.TeamAccent);
        foreach (var (pos, color) in _blips)
            DrawSelfDiamond(center + pos * radius, 3f, color);
    }

    private void DrawSelfDiamond(Vector2 c, float s, Color color)
    {
        DrawColoredPolygon(new[] { c + new Vector2(0, -s), c + new Vector2(s, 0), c + new Vector2(0, s), c + new Vector2(-s, 0) }, color);
    }
}
