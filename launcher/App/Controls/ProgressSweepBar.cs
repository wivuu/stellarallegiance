using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;
using GColor = Godot.Color;

namespace StellarAllegiance.Launcher.Controls;

// Port of the game's ProgressSweepBar (client/scripts/ui/ConnectFeedback.cs): a thin square-ended bar —
// BorderLo track, coloured fill — plus, while indeterminate, a soft highlight band sweeping left→right:
// 40% of the width, 1.6 s per pass, six slices whose alpha peaks in the middle of the band.
// The animation timer only runs while the bar is visible AND sweeping; a hidden launcher burns no CPU.
public sealed class ProgressSweepBar : Control
{
    private const double SweepSeconds = 1.6;
    private const double BandFraction = 0.4;
    private const int Slices = 6;

    private readonly DispatcherTimer _timer;
    private readonly DateTime _epoch = DateTime.UtcNow;
    private BarMode _mode = BarMode.Hidden;
    private double _percent;
    private GColor _color = DesignTokens.TeamAccent;

    public ProgressSweepBar()
    {
        Height = 6;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Render,
            (_, _) =>
            {
                if (IsEffectivelyVisible)
                    InvalidateVisual();
                else
                    UpdateTimer(); // window hidden: stop; Configure() restarts us with the next view
            }
        );
    }

    public void Configure(BarMode mode, int percent, GColor color)
    {
        _mode = mode;
        _percent = Math.Clamp(percent, 0, 100) / 100.0;
        _color = color;
        Opacity = mode == BarMode.Hidden ? 0 : 1; // keep the layout slot so the column does not jump
        UpdateTimer();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        bool animate = _mode == BarMode.Sweep && IsEffectivelyVisible;
        if (animate && !_timer.IsEnabled)
            _timer.Start();
        else if (!animate)
            _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        if (_mode == BarMode.Hidden)
            return;
        var r = new Rect(Bounds.Size);
        context.FillRectangle(Sa.BorderLo, r);

        double fill = _mode switch
        {
            BarMode.Fill => _percent,
            BarMode.Full => 1,
            _ => 1, // Sweep: a full-width dim fill under the travelling highlight
        };
        var fillBrush = _mode == BarMode.Sweep ? Sa.B(_color, 0.35f) : Sa.B(_color);
        context.FillRectangle(fillBrush, new Rect(0, 0, Math.Round(r.Width * fill), r.Height));

        if (_mode != BarMode.Sweep)
            return;
        double band = r.Width * BandFraction;
        double t = (DateTime.UtcNow - _epoch).TotalSeconds % SweepSeconds / SweepSeconds;
        double start = -band + (r.Width + band) * t;
        double slice = band / Slices;
        using (context.PushClip(r))
        {
            for (int i = 0; i < Slices; i++)
            {
                double mid = (i + 0.5) / Slices; // 0..1 across the band
                float alpha = (float)(0.5 * (1 - Math.Abs(2 * mid - 1)));
                context.FillRectangle(
                    Sa.B(DesignTokens.TextHi, alpha),
                    new Rect(start + i * slice, 0, slice + 0.5, r.Height)
                );
            }
        }
    }
}
