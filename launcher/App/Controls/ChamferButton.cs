using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;
using GColor = Godot.Color;
using TextStyle = StellarAllegiance.Launcher.Theme.TextStyle;

namespace StellarAllegiance.Launcher.Controls;

public enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger,
    Icon,
}

// Port of the game's ChamferButton (client/scripts/ui/ChamferButton.cs): same variants, same colours per
// state, same hover glow easing, same 9px chamfer cut on the top-left and bottom-right corners. Like the
// original it has NO pressed visual, and it paints itself (custom Render) rather than using a themed
// template — the template is just a content presenter.
//
// Two deliberate differences: the leading ◆ of a call-to-action is a vector SaIcon (Saira has no such
// glyph), and there is no click sound (the launcher is silent).
public sealed class ChamferButton : Button
{
    public static readonly StyledProperty<ButtonVariant> VariantProperty = AvaloniaProperty.Register<
        ChamferButton,
        ButtonVariant
    >(nameof(Variant), ButtonVariant.Secondary);

    private const double GlowPerSecond = 6; // Godot: _glow = MoveToward(_glow, target, delta * 6)
    private const double LabelPad = 14;
    private static readonly GColor White = new(1f, 1f, 1f);

    private readonly TextBlock _label;
    private readonly SaIcon _icon;
    private readonly DispatcherTimer _glowTimer;
    private double _glow;
    private DateTime _lastGlowTick;

    static ChamferButton()
    {
        AffectsRender<ChamferButton>(VariantProperty, IsEnabledProperty);
    }

    public ChamferButton()
    {
        _icon = new SaIcon
        {
            Width = 9,
            Height = 9,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _label = Sa.Text("", TextStyle.Label, size: DesignTokens.LabelSize + 1);
        _label.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _icon, _label },
        };
        Content = row;
        Template = new FuncControlTemplate<ChamferButton>(
            (_, scope) =>
                new ContentPresenter
                {
                    Name = "PART_ContentPresenter",
                    [!ContentPresenter.ContentProperty] = this[!ContentProperty],
                    Padding = new Thickness(LabelPad, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent, // the whole shape is clickable, not just the glyphs
                }.RegisterInNameScope(scope)
        );
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = true;
        MinWidth = 130;
        MinHeight = 38;
        _glowTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnGlowTick);
        ApplyForeground();
    }

    public ButtonVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    // Caps text; the design system never upper-cases for you.
    public string Label
    {
        get => _label.Text ?? "";
        set
        {
            _label.Text = value;
            _label.IsVisible = value.Length > 0;
        }
    }

    public SaIconKind Icon
    {
        get => _icon.Kind;
        set
        {
            _icon.Kind = value;
            _icon.IsVisible = value != SaIconKind.None;
        }
    }

    public double IconSize
    {
        set => _icon.Width = _icon.Height = value;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == VariantProperty)
        {
            if (Variant == ButtonVariant.Icon)
            {
                MinWidth = MinHeight = 34;
                Padding = default;
            }
            ApplyForeground();
        }
        else if (change.Property == IsEnabledProperty)
            ApplyForeground();
        else if (change.Property == IsPointerOverProperty || change.Property == IsFocusedProperty)
        {
            _lastGlowTick = DateTime.UtcNow;
            _glowTimer.Start();
        }
    }

    private void OnGlowTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double step = (now - _lastGlowTick).TotalSeconds * GlowPerSecond;
        _lastGlowTick = now;
        double target = IsEnabled && (IsPointerOver || IsKeyboardFocusWithin) ? 1 : 0;
        _glow = target > _glow ? Math.Min(target, _glow + step) : Math.Max(target, _glow - step);
        if (Math.Abs(_glow - target) < 0.0001)
            _glowTimer.Stop();
        ApplyForeground();
        InvalidateVisual();
    }

    private void ApplyForeground()
    {
        GColor text = Variant switch
        {
            ButtonVariant.Primary => DesignTokens.Void,
            ButtonVariant.Ghost => DesignTokens.Data.Lerp(White, (float)_glow),
            ButtonVariant.Danger => DesignTokens.DangerText,
            _ => DesignTokens.TextHi,
        };
        if (!IsEnabled)
            text = DesignTokens.TextDim;
        var brush = Sa.B(text);
        _label.Foreground = brush;
        _icon.Foreground = brush;
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        double cut = Variant == ButtonVariant.Icon ? 0 : DesignTokens.CornerChamfer;
        GColor accent = DesignTokens.TeamAccent;
        float glow = (float)_glow;

        GColor fill;
        GColor? border;
        switch (Variant)
        {
            case ButtonVariant.Primary:
                fill = accent;
                border = null;
                break;
            case ButtonVariant.Ghost:
                fill = new GColor(0, 0, 0, 0);
                border = null;
                break;
            case ButtonVariant.Danger:
                fill = new GColor(DesignTokens.Danger, 0.14f);
                border = new GColor(DesignTokens.Danger, 0.5f);
                break;
            default:
                fill = new GColor(accent, 0.10f);
                border = new GColor(DesignTokens.BorderHi, 0.4f);
                break;
        }

        if (!IsEnabled)
        {
            // Godot's Color(from, alpha) REPLACES alpha: a disabled fill is BorderLo's RGB at .40, not .16×.4.
            fill = new GColor(DesignTokens.BorderLo, 0.4f);
            border = DesignTokens.BorderLo;
        }
        else if (glow > 0)
        {
            switch (Variant)
            {
                case ButtonVariant.Primary:
                    fill = accent.Lerp(White, 0.18f * glow);
                    break;
                case ButtonVariant.Ghost:
                    break; // only the text brightens (ApplyForeground)
                case ButtonVariant.Danger:
                    fill = new GColor(DesignTokens.Danger, 0.14f + 0.11f * glow);
                    break;
                default:
                    fill = new GColor(accent, 0.10f + 0.10f * glow);
                    border = accent;
                    break;
            }
        }

        var shape = UiGeometry.Chamfer(rect.Deflate(0.5), cut);
        context.DrawGeometry(fill.A > 0 ? Sa.B(fill) : null, border is { } b ? new Pen(Sa.B(b), 1) : null, shape);

        // Outer glow on hover: a faint, wider accent outline just outside the shape.
        if (glow > 0.01f && Variant != ButtonVariant.Ghost && IsEnabled)
        {
            GColor glowColor = Variant == ButtonVariant.Danger ? DesignTokens.Danger : accent;
            context.DrawGeometry(
                null,
                new Pen(Sa.B(glowColor, 0.45f * glow), 2),
                UiGeometry.Chamfer(rect.Inflate(1.5), cut)
            );
        }
    }
}
