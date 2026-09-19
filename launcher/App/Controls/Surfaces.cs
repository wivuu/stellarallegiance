using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;
using GColor = Godot.Color;
using TextStyle = StellarAllegiance.Launcher.Theme.TextStyle;

namespace StellarAllegiance.Launcher.Controls;

// Ports of the game's surfaces (client/scripts/ui/Surfaces.cs). Same numbers, same rules: rect borders are
// crisp 1-device-pixel hairlines, chamfers/tabs are polygons, and every colour is a DesignTokens member.

// High-priority frame: translucent body + four accent corner brackets. The accent follows the state
// (cyan / Ok / Danger), exactly as the game's connect modal recolours its brackets.
public sealed class BracketPanel : Decorator
{
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<BracketPanel, IBrush?>(
        nameof(Accent),
        Sa.Accent
    );
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<BracketPanel, IBrush?>(
        nameof(Fill),
        Sa.PanelFill
    );

    static BracketPanel()
    {
        AffectsRender<BracketPanel>(AccentProperty, FillProperty);
    }

    public BracketPanel() => Padding = new Thickness(18);

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        if (Fill is { } fill)
            context.FillRectangle(fill, r);
        if (Accent is not { } accent)
            return;
        // 2px arms as filled strips INSIDE the bounds (a stroked line would straddle the edge and clip).
        double len = DesignTokens.BracketLength,
            w = 2;
        foreach (
            var (x, y, dx, dy) in new[]
            {
                (r.X, r.Y, 1, 1),
                (r.Right, r.Y, -1, 1),
                (r.X, r.Bottom, 1, -1),
                (r.Right, r.Bottom, -1, -1),
            }
        )
        {
            context.FillRectangle(accent, Normalize(new Rect(x, y, dx * len, dy * w)));
            context.FillRectangle(accent, Normalize(new Rect(x, y, dx * w, dy * len)));
        }

        static Rect Normalize(Rect rect) =>
            new(
                Math.Min(rect.X, rect.X + rect.Width),
                Math.Min(rect.Y, rect.Y + rect.Height),
                Math.Abs(rect.Width),
                Math.Abs(rect.Height)
            );
    }
}

// Default grouped-data container: hairline border + optional slanted tab header.
public sealed class HairlinePanel : Decorator
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<HairlinePanel, string>(
        nameof(Title),
        ""
    );

    static HairlinePanel()
    {
        AffectsRender<HairlinePanel>(TitleProperty);
    }

    public HairlinePanel() => Padding = new Thickness(14, 38, 14, 14);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
            Padding = new Thickness(14, string.IsNullOrEmpty(Title) ? 14 : 38, 14, 14);
    }

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        double t = UiGeometry.Hairline(this);
        context.FillRectangle(Sa.PanelFill, r);
        UiGeometry.HairlineFrame(context, r, Sa.BorderHi, t);
        if (string.IsNullOrEmpty(Title))
            return;

        var text = new FormattedText(
            Title,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(Sa.Saira, FontStyle.Normal, FontWeight.SemiBold),
            DesignTokens.LabelSize,
            Sa.Data
        );
        // FormattedText has no letter-spacing, so the tab width budgets for the Label style's 2px/glyph.
        double spaced = text.Width + Title.Length * DesignTokens.LabelLetterSpacing;
        double w = spaced + 30;
        context.DrawGeometry(Sa.B(DesignTokens.TeamAccent, 0.12f), null, UiGeometry.Tab(new Rect(0, 0, w, 28), 10));
        context.FillRectangle(Sa.BorderHi, new Rect(0, 28 - t, w - 10, t));
        DrawSpaced(context, Title, new Point(12, (28 - text.Height) / 2));
    }

    // Letter-spaced caps, glyph by glyph (matches the Label style everywhere else).
    private static void DrawSpaced(DrawingContext context, string title, Point origin)
    {
        var typeface = new Typeface(Sa.Saira, FontStyle.Normal, FontWeight.SemiBold);
        double x = origin.X;
        foreach (char ch in title)
        {
            var glyph = new FormattedText(
                ch.ToString(),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                DesignTokens.LabelSize,
                Sa.Data
            );
            context.DrawText(glyph, new Point(x, origin.Y));
            x += glyph.WidthIncludingTrailingWhitespace + DesignTokens.LabelLetterSpacing;
        }
    }
}

// Horizontal rule with a centred accent diamond.
public sealed class DiamondDivider : Control
{
    public DiamondDivider() => Height = 16;

    public override void Render(DrawingContext context)
    {
        double t = UiGeometry.Hairline(this);
        double y = Math.Round(Bounds.Height / 2);
        context.FillRectangle(Sa.BorderHi, new Rect(0, y, Bounds.Width, t));
        context.DrawGeometry(Sa.Accent, null, UiGeometry.Diamond(new Point(Bounds.Width / 2, y), 5));
    }
}

// Status + mono telemetry pill: text in colour c on c@.16, square corners, optional slow pulse.
public sealed class StatusPill : Border
{
    private readonly SaIcon _icon = new()
    {
        Width = 8,
        Height = 8,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    private readonly TextBlock _text = Sa.Text("", TextStyle.Data, size: DesignTokens.CaptionSize);
    private readonly Avalonia.Threading.DispatcherTimer _pulse;
    private DateTime _pulseStart;

    public StatusPill()
    {
        Padding = new Thickness(10, 3);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _text.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Child = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 7,
            Children = { _icon, _text },
        };
        _pulse = new Avalonia.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            Avalonia.Threading.DispatcherPriority.Render,
            OnPulse
        );
    }

    public void Configure(string text, GColor color, SaIconKind icon, bool pulse)
    {
        var brush = Sa.B(color);
        _text.Text = text;
        _text.Foreground = brush;
        _icon.Kind = icon;
        _icon.Foreground = brush;
        _icon.IsVisible = icon != SaIconKind.None;
        Background = Sa.B(color, 0.16f);
        IsVisible = text.Length > 0;
        if (pulse && !_pulse.IsEnabled)
        {
            _pulseStart = DateTime.UtcNow;
            _pulse.Start();
        }
        else if (!pulse)
        {
            _pulse.Stop();
            Opacity = 1;
        }
    }

    // Godot: alpha 1 → .35 → 1 with 0.7 s legs.
    private void OnPulse(object? sender, EventArgs e)
    {
        double phase = (DateTime.UtcNow - _pulseStart).TotalSeconds % 1.4 / 0.7; // 0..2
        double k = phase <= 1 ? phase : 2 - phase;
        Opacity = 1 - 0.65 * k;
    }
}

// Inline alert: c@.10 body, 3px left bar in c, caps title in c (DangerText for Danger), mono body.
public sealed class AlertBox : Border
{
    private readonly SaIcon _icon = new()
    {
        Width = 12,
        Height = 12,
        Kind = SaIconKind.Warn,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    private readonly TextBlock _title = Sa.Text("", TextStyle.Label);
    private readonly TextBlock _body = Sa.Text("", TextStyle.Data, DesignTokens.Text2);

    public AlertBox()
    {
        Padding = new Thickness(12, 10);
        BorderThickness = new Thickness(3, 0, 0, 0);
        _title.TextWrapping = TextWrapping.Wrap;
        _body.TextWrapping = TextWrapping.Wrap;
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_icon, Dock.Left);
        _icon.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(_icon);
        head.Children.Add(_title);
        Child = new StackPanel { Spacing = 2, Children = { head, _body } };
    }

    public void Configure(string title, string body, GColor color, bool danger)
    {
        var titleBrush = danger ? Sa.DangerText : Sa.B(color);
        _title.Text = title;
        _title.Foreground = titleBrush;
        _icon.Foreground = titleBrush;
        _body.Text = body;
        _body.IsVisible = body.Length > 0;
        Background = Sa.B(color, 0.10f);
        BorderBrush = Sa.B(color);
    }
}
