using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using StellarAllegiance.Launcher.Theme;
using StellarAllegiance.Ui;

namespace StellarAllegiance.Launcher.Controls;

// The launcher's background, after the game's NebulaBackground (client/scripts/ui/NebulaBackground.cs):
//   * the gas clouds + void vignette are a pre-rendered image (tools/launcher-art/gen_nebula.py evaluates
//     the game's shader maths at a fixed time) — smooth, so it scales to any size without artefacts;
//   * the star-dot lattice (60px pitch) and the scanlines (1 lit row in 3) are drawn HERE, snapped to
//     device pixels. Baked into a scaled bitmap they would moiré at 125% / 150% display scaling.
public sealed class Backdrop : Control
{
    private const double DotPitch = 60;
    private const float DotAlpha = 0.08f;
    private const float ScanlineAlpha = 0.0175f;
    private readonly Bitmap? _clouds;

    public Backdrop()
    {
        IsHitTestVisible = false;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://StellarLauncher/Assets/nebula-clouds.png"));
            _clouds = new Bitmap(stream);
        }
        catch (Exception)
        {
            _clouds = null; // a missing backdrop must never stop the launcher: fall back to flat Void
        }
    }

    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size);
        context.FillRectangle(Sa.Void, r);
        if (_clouds is not null)
            context.DrawImage(_clouds, new Rect(_clouds.Size), r);

        double scale = UiGeometry.TopLevelScale(this);
        double px = 1 / scale;

        var dot = Sa.B(DesignTokens.Data, DotAlpha);
        for (double y = DotPitch / 2; y < r.Height; y += DotPitch)
        for (double x = DotPitch / 2; x < r.Width; x += DotPitch)
            context.FillRectangle(dot, new Rect(Snap(x, scale), Snap(y, scale), px, px));

        var line = Sa.B(DesignTokens.TextHi, ScanlineAlpha);
        int rows = (int)Math.Ceiling(r.Height * scale);
        for (int row = 0; row < rows; row += 3)
            context.FillRectangle(line, new Rect(0, row * px, r.Width, px));

        static double Snap(double v, double s) => Math.Round(v * s) / s;
    }
}
