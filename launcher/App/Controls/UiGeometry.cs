using Avalonia;
using Avalonia.Media;

namespace StellarAllegiance.Launcher.Controls;

// The launcher's counterpart of the game's UiDraw (client/scripts/ui/UiDraw.cs): the few shapes the
// design system is made of. Chamfers are explicit POLYGONS (never a corner radius), hairlines are crisp
// device-pixel lines — same rules as DESIGN.md.
public static class UiGeometry
{
    // Rectangle with the top-left and bottom-right corners cut at 45° — UiDraw.ChamferPoints.
    public static StreamGeometry Chamfer(Rect r, double cut)
    {
        cut = Math.Min(cut, Math.Min(r.Width, r.Height) / 2);
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(r.X + cut, r.Y), isFilled: true);
        g.LineTo(new Point(r.Right, r.Y));
        g.LineTo(new Point(r.Right, r.Bottom - cut));
        g.LineTo(new Point(r.Right - cut, r.Bottom));
        g.LineTo(new Point(r.X, r.Bottom));
        g.LineTo(new Point(r.X, r.Y + cut));
        g.EndFigure(isClosed: true);
        return geometry;
    }

    // Header tab with a slanted right edge — UiDraw.TabPoints.
    public static StreamGeometry Tab(Rect r, double slant)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(r.X, r.Y), isFilled: true);
        g.LineTo(new Point(r.Right, r.Y));
        g.LineTo(new Point(r.Right - slant, r.Bottom));
        g.LineTo(new Point(r.X, r.Bottom));
        g.EndFigure(isClosed: true);
        return geometry;
    }

    public static StreamGeometry Diamond(Point center, double half)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(center.X, center.Y - half), isFilled: true);
        g.LineTo(new Point(center.X + half, center.Y));
        g.LineTo(new Point(center.X, center.Y + half));
        g.LineTo(new Point(center.X - half, center.Y));
        g.EndFigure(isClosed: true);
        return geometry;
    }

    // One device pixel (or a whole number of them on >2x displays) expressed in DIPs, so a "1px hairline"
    // is exactly that on every scale factor instead of a blurry 1.25px smear.
    public static double Hairline(Visual visual)
    {
        double scale = TopLevelScale(visual);
        return Math.Max(1, Math.Floor(scale)) / scale;
    }

    public static double TopLevelScale(Visual visual) =>
        Avalonia.Controls.TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1.0;

    // Axis-aligned 1px frame, drawn as four filled strips on pixel boundaries (a stroked rect would
    // straddle pixels and anti-alias into two half-bright rows).
    public static void HairlineFrame(DrawingContext context, Rect r, IBrush brush, double t)
    {
        context.FillRectangle(brush, new Rect(r.X, r.Y, r.Width, t));
        context.FillRectangle(brush, new Rect(r.X, r.Bottom - t, r.Width, t));
        context.FillRectangle(brush, new Rect(r.X, r.Y + t, t, r.Height - 2 * t));
        context.FillRectangle(brush, new Rect(r.Right - t, r.Y + t, t, r.Height - 2 * t));
    }
}
