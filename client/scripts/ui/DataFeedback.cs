using Godot;

namespace StellarAllegiance.Ui;

// ── 05 DATA & FEEDBACK ───────────────────────────────────────────────────────

// Radial gauge — a conic-style ring (emulated with DrawArc, since Godot has no conic
// gradient) with a value arc, faint track, soft glow, and a centred mono readout.
public partial class RadialGauge : Control
{
    public Color Arc = DesignTokens.TeamAccent;
    public string CenterText = "";
    public string Caption = "";

    private float _value; // 0..1

    public override void _Ready()
    {
        UiFonts.EnsureLoaded();
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(96, 96);
        Resized += QueueRedraw;
    }

    public void SetValue(float v01)
    {
        v01 = Mathf.Clamp(v01, 0f, 1f);
        if (Mathf.IsEqualApprox(v01, _value))
            return;
        _value = v01;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = Size * 0.5f;
        float radius = Mathf.Min(Size.X, Size.Y) * 0.5f - 8f;
        const float start = -Mathf.Pi * 0.5f; // 12 o'clock
        float end = start + _value * Mathf.Pi * 2f;

        DrawArc(center, radius, 0, Mathf.Pi * 2f, 64, new Color(DesignTokens.BorderLo, 0.5f), 8f, true);
        if (_value > 0f)
        {
            DrawArc(center, radius, start, end, 64, new Color(Arc, 0.25f), 14f, true); // glow
            DrawArc(center, radius, start, end, 64, Arc, 8f, true); // value
        }

        if (!string.IsNullOrEmpty(CenterText))
        {
            var sz = UiFonts.Mono.GetStringSize(CenterText, HorizontalAlignment.Left, -1, 20);
            DrawString(UiFonts.Mono, center + new Vector2(-sz.X * 0.5f, -2), CenterText, HorizontalAlignment.Left, -1, 20, DesignTokens.TextHi);
        }
        if (!string.IsNullOrEmpty(Caption))
        {
            var sz = UiFonts.SairaLabel.GetStringSize(Caption, HorizontalAlignment.Left, -1, 9);
            DrawString(UiFonts.SairaLabel, center + new Vector2(-sz.X * 0.5f, 14), Caption, HorizontalAlignment.Left, -1, 9, DesignTokens.TextDim);
        }
    }
}

// Segmented bar — N discrete cells, `filled` of them lit. The classic hull/ammo readout.
public partial class SegmentedBar : Control
{
    public int Segments = 12;
    public Color Fill = DesignTokens.Ok;

    private int _filled;

    public override void _Ready()
    {
        if (CustomMinimumSize == Vector2.Zero)
            CustomMinimumSize = new Vector2(0, 8);
        Resized += QueueRedraw;
    }

    public void Set(int filled)
    {
        filled = Mathf.Clamp(filled, 0, Segments);
        if (filled == _filled)
            return;
        _filled = filled;
        QueueRedraw();
    }

    public override void _Draw()
    {
        const float gap = 2f;
        float w = (Size.X - (Segments - 1) * gap) / Segments;
        for (int i = 0; i < Segments; i++)
        {
            var r = new Rect2(i * (w + gap), 0, w, Size.Y);
            DrawRect(r, i < _filled ? Fill : new Color(DesignTokens.BorderLo, 0.6f), filled: true);
        }
    }
}

// Status pill — a small coloured chip, optionally pulsing (missile lock, alerts).
public partial class StatusPill : PanelContainer
{
    public enum Kind
    {
        Ok,
        Warn,
        Danger,
        Data,
        Neutral,
        Accent, // structural cyan — in-progress / negotiating states
    }

    private Label _label = null!;
    private Tween? _tween;

    public override void _Ready() => EnsureLabel();

    private void EnsureLabel()
    {
        if (_label != null)
            return;
        _label = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        AddChild(_label);
    }

    private static Color ColorOf(Kind k) =>
        k switch
        {
            Kind.Ok => DesignTokens.Ok,
            Kind.Warn => DesignTokens.Warn,
            Kind.Danger => DesignTokens.DangerText,
            Kind.Data => DesignTokens.Data,
            Kind.Accent => DesignTokens.TeamAccent,
            _ => DesignTokens.Text2,
        };

    public void Configure(string text, Kind kind, bool pulse = false)
    {
        EnsureLabel();
        Color c = ColorOf(kind);
        _label.Text = text;
        _label.AddThemeColorOverride("font_color", c);
        _label.AddThemeFontSizeOverride("font_size", 11);

        var sb = new StyleBoxFlat { BgColor = new Color(c, 0.16f), AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 10;
        sb.ContentMarginTop = sb.ContentMarginBottom = 3;
        AddThemeStyleboxOverride("panel", sb);

        _tween?.Kill();
        if (pulse)
        {
            _tween = CreateTween().SetLoops();
            _tween.TweenProperty(this, "modulate:a", 0.35f, 0.7);
            _tween.TweenProperty(this, "modulate:a", 1.0f, 0.7);
        }
        else
        {
            Modulate = Colors.White;
        }
    }
}

// Alert banner — accent left-border + bold title + mono sub-line.
public partial class AlertBox : PanelContainer
{
    private Label _title = null!;
    private Label _sub = null!;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_title != null)
            return;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 2);
        _title = UiKit.MakeLabel("", UiKit.TextStyle.Label);
        _sub = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _sub.AddThemeColorOverride("font_color", DesignTokens.Text2);
        _sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(_title);
        col.AddChild(_sub);
        AddChild(col);
    }

    public void Configure(string title, string sub, StatusPill.Kind kind)
    {
        EnsureBuilt();
        Color c = kind switch
        {
            StatusPill.Kind.Ok => DesignTokens.Ok,
            StatusPill.Kind.Warn => DesignTokens.Warn,
            StatusPill.Kind.Danger => DesignTokens.Danger,
            StatusPill.Kind.Data => DesignTokens.TeamAccent,
            _ => DesignTokens.Text2,
        };
        _title.Text = title;
        _title.AddThemeColorOverride("font_color", kind == StatusPill.Kind.Danger ? DesignTokens.DangerText : c);
        _sub.Text = sub;
        _sub.Visible = !string.IsNullOrEmpty(sub);

        var sb = new StyleBoxFlat { BgColor = new Color(c, 0.10f), BorderColor = c, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = 12;
        sb.ContentMarginRight = 12;
        sb.ContentMarginTop = sb.ContentMarginBottom = 10;
        AddThemeStyleboxOverride("panel", sb);
    }
}

// Stat readout card — a big mono number over a dim caps caption.
public partial class StatReadout : PanelContainer
{
    private Label _value = null!;
    private Label _caption = null!;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_value != null)
            return;
        var sb = new StyleBoxFlat { BgColor = DesignTokens.Well, BorderColor = DesignTokens.BorderLo, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(8);
        AddThemeStyleboxOverride("panel", sb);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        _caption = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _caption.AddThemeFontSizeOverride("font_size", 9);
        _caption.AddThemeColorOverride("font_color", DesignTokens.TextDim);
        _value = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _value.AddThemeFontSizeOverride("font_size", 22);
        col.AddChild(_caption);
        col.AddChild(_value);
        AddChild(col);
    }

    public void Set(string value, string label, Color? valueColor = null)
    {
        EnsureBuilt();
        _value.Text = value;
        _value.AddThemeColorOverride("font_color", valueColor ?? DesignTokens.Data);
        _caption.Text = label;
    }
}

// Data table — header row + data rows on a hairline panel. Columns share width by weight.
public partial class DataTable : VBoxContainer
{
    private float[] _weights = [];
    private string[] _headers = [];
    private Control? _header;

    public override void _Ready() => AddThemeConstantOverride("separation", 0);

    public void SetColumns(string[] headers, float[]? weights = null)
    {
        _headers = headers;
        _weights = weights ?? Filled(headers.Length);
        _header?.QueueFree();
        _header = Row(headers, isHeader: true);
        AddChild(_header);
        MoveChild(_header, 0);
    }

    public void AddRow(string[] cells) => AddChild(Row(cells, isHeader: false));

    public void Clear()
    {
        foreach (var c in GetChildren())
            if (c != _header)
                c.QueueFree();
    }

    private HBoxContainer Row(string[] cells, bool isHeader)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        for (int i = 0; i < cells.Length; i++)
        {
            var l = UiKit.MakeLabel(cells[i], isHeader ? UiKit.TextStyle.Label : UiKit.TextStyle.Data);
            if (isHeader)
                l.AddThemeColorOverride("font_color", DesignTokens.TextDim);
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            l.SizeFlagsStretchRatio = i < _weights.Length ? _weights[i] : 1f;
            row.AddChild(l);
        }
        return row;
    }

    private static float[] Filled(int n)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++)
            a[i] = 1f;
        return a;
    }
}

// Toast host — pin to a corner; Show() drops a transient notification that auto-fades.
public partial class ToastHost : Control
{
    private VBoxContainer _stack = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        _stack = new VBoxContainer();
        _stack.AddThemeConstantOverride("separation", 6);
        _stack.SetAnchorsPreset(LayoutPreset.TopWide);
        AddChild(_stack);
    }

    public void Show(string message, float seconds = 3f)
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = DesignTokens.Panel, BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.ContentMarginLeft = sb.ContentMarginRight = 12;
        sb.ContentMarginTop = sb.ContentMarginBottom = 10;
        panel.AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var dot = new ColorRect { Color = DesignTokens.TeamAccent, CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(dot);
        row.AddChild(UiKit.MakeLabel(message, UiKit.TextStyle.Body));
        panel.AddChild(row);
        _stack.AddChild(panel);

        var tw = CreateTween();
        tw.TweenInterval(seconds);
        tw.TweenProperty(panel, "modulate:a", 0f, 0.4);
        tw.TweenCallback(Callable.From(panel.QueueFree));
    }
}
