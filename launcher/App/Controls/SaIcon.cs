using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StellarAllegiance.Launcher.Controls;

public enum SaIconKind
{
    None,
    Diamond, // ◆ primary call-to-action mark, brand mark
    Dot, // ● status
    Check, // ✓
    Chevron, // ▸ active stage
    Cross, // ✕ close / failed stage
    Ring, // ○ pending stage
    Warn, // ⚠
    Gear, // ⚙ settings (drawn as sliders)
    Minimize,
    External, // ↗ opens outside the launcher
}

// The design system's symbols as VECTOR shapes. The game writes them as text ("◆ CONNECT") and leans on
// Godot's fallback fonts — but Saira contains none of ◆ ● ✓ ▸ ✕ ○ ⚠ ⚙ ↗ (and JetBrains Mono lacks ✓ ○ ⚙),
// so in Avalonia they would fall back to whatever the OS has: a different look per platform, or tofu on a
// minimal Linux install. Drawn shapes look the same everywhere and stay crisp at any scale.
public sealed class SaIcon : Control
{
    public static readonly StyledProperty<SaIconKind> KindProperty = AvaloniaProperty.Register<SaIcon, SaIconKind>(
        nameof(Kind)
    );
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<SaIcon, IBrush?>(
        nameof(Foreground)
    );

    static SaIcon()
    {
        AffectsRender<SaIcon>(KindProperty, ForegroundProperty);
    }

    public SaIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Kind == SaIconKind.None || Foreground is not { } brush)
            return;
        double s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0)
            return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double stroke = Math.Max(1.25, s * 0.13);
        var pen = new Pen(brush, stroke, lineCap: PenLineCap.Square, lineJoin: PenLineJoin.Miter);
        double h = s / 2;

        switch (Kind)
        {
            case SaIconKind.Diamond:
                context.DrawGeometry(brush, null, UiGeometry.Diamond(c, h * 0.9));
                break;
            case SaIconKind.Dot:
                context.DrawEllipse(brush, null, c, h * 0.62, h * 0.62);
                break;
            case SaIconKind.Ring:
                context.DrawEllipse(null, pen, c, h * 0.62, h * 0.62);
                break;
            case SaIconKind.Check:
                context.DrawGeometry(null, pen, Poly(false, P(-0.62, 0.02), P(-0.18, 0.46), P(0.66, -0.46)));
                break;
            case SaIconKind.Chevron:
                context.DrawGeometry(brush, null, Poly(true, P(-0.38, -0.62), P(0.5, 0), P(-0.38, 0.62)));
                break;
            case SaIconKind.Cross:
                context.DrawLine(pen, Abs(-0.55, -0.55), Abs(0.55, 0.55));
                context.DrawLine(pen, Abs(-0.55, 0.55), Abs(0.55, -0.55));
                break;
            case SaIconKind.Minimize:
                context.DrawLine(pen, Abs(-0.6, 0.45), Abs(0.6, 0.45));
                break;
            case SaIconKind.Warn:
                context.DrawGeometry(null, pen, Poly(true, P(0, -0.72), P(0.78, 0.62), P(-0.78, 0.62)));
                context.DrawLine(pen, Abs(0, -0.22), Abs(0, 0.16));
                context.DrawLine(pen, Abs(0, 0.4), Abs(0, 0.42));
                break;
            case SaIconKind.External:
                context.DrawLine(pen, Abs(-0.5, 0.5), Abs(0.5, -0.5));
                context.DrawGeometry(null, pen, Poly(false, P(-0.1, -0.5), P(0.5, -0.5), P(0.5, 0.1)));
                break;
            case SaIconKind.Gear:
                // "Settings" as three sliders: a toothed cog turns to mush at 12px, and a ring with four
                // ticks reads as a targeting reticle — which in THIS game means something else entirely.
                foreach (var (y, knob) in new[] { (-0.55, 0.35), (0.0, -0.3), (0.55, 0.15) })
                {
                    context.DrawLine(pen, Abs(-0.8, y), Abs(0.8, y));
                    context.FillRectangle(
                        brush,
                        new Rect(c.X + (knob - 0.17) * h, c.Y + (y - 0.24) * h, 0.34 * h, 0.48 * h)
                    );
                }
                break;
        }

        Point P(double x, double y) => new(c.X + x * h, c.Y + y * h);
        Point Abs(double x, double y) => P(x, y);
        StreamGeometry Poly(bool closed, params Point[] points)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            g.BeginFigure(points[0], isFilled: closed);
            for (int i = 1; i < points.Length; i++)
                g.LineTo(points[i]);
            g.EndFigure(closed);
            return geometry;
        }
    }
}
