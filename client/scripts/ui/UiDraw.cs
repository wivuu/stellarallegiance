using Godot;

namespace StellarAllegiance.Ui;

// Shared _Draw() primitives for the bracket / chamfer look. Centralised so every
// custom-draw component (buttons, panels, chips) renders the same geometry rather than
// re-deriving the polygon maths inline. Chamfers are GEOMETRY (an explicit polygon),
// not a StyleBoxFlat corner radius — the spec keeps corners square and cuts two of them.
public static class UiDraw
{
    // Button chamfer: top-left and bottom-right corners cut by `cut` px, matching the
    // design's clip-path polygon(9px 0, 100% 0, 100% 100%-9, 100%-9 100%, 0 100%, 0 9).
    public static Vector2[] ChamferPoints(Rect2 r, float cut)
    {
        float x = r.Position.X,
            y = r.Position.Y;
        float x2 = x + r.Size.X,
            y2 = y + r.Size.Y;
        cut = Mathf.Min(cut, Mathf.Min(r.Size.X, r.Size.Y) * 0.5f);
        return new[]
        {
            new Vector2(x + cut, y),
            new Vector2(x2, y),
            new Vector2(x2, y2 - cut),
            new Vector2(x2 - cut, y2),
            new Vector2(x, y2),
            new Vector2(x, y + cut),
        };
    }

    // A left-aligned tab whose bottom-right corner is slanted (section headers / panel tabs):
    // polygon(0 0, 100% 0, 100%-slant 100%, 0 100%).
    public static Vector2[] TabPoints(Rect2 r, float slant)
    {
        float x = r.Position.X,
            y = r.Position.Y;
        float x2 = x + r.Size.X,
            y2 = y + r.Size.Y;
        return new[]
        {
            new Vector2(x, y),
            new Vector2(x2, y),
            new Vector2(x2 - slant, y2),
            new Vector2(x, y2),
        };
    }

    // Fill a chamfered rect and (optionally) stroke its outline. The anti-aliased border
    // hides the polygon fill's edge jaggies.
    public static void Chamfer(CanvasItem ci, Rect2 r, float cut, Color fill, Color? border = null, float width = 1f)
    {
        Vector2[] pts = ChamferPoints(r, cut);
        if (fill.A > 0f)
            ci.DrawColoredPolygon(pts, fill);
        if (border is Color b && b.A > 0f)
            ci.DrawPolyline(Close(pts), b, width, antialiased: true);
    }

    // Four corner L-brackets around a rect — the marker for high-priority panels.
    public static void CornerBrackets(CanvasItem ci, Rect2 r, float len, Color c, float width = 2f)
    {
        float x = r.Position.X,
            y = r.Position.Y;
        float x2 = x + r.Size.X,
            y2 = y + r.Size.Y;
        // top-left
        ci.DrawLine(new Vector2(x, y), new Vector2(x + len, y), c, width, true);
        ci.DrawLine(new Vector2(x, y), new Vector2(x, y + len), c, width, true);
        // top-right
        ci.DrawLine(new Vector2(x2, y), new Vector2(x2 - len, y), c, width, true);
        ci.DrawLine(new Vector2(x2, y), new Vector2(x2, y + len), c, width, true);
        // bottom-left
        ci.DrawLine(new Vector2(x, y2), new Vector2(x + len, y2), c, width, true);
        ci.DrawLine(new Vector2(x, y2), new Vector2(x, y2 - len), c, width, true);
        // bottom-right
        ci.DrawLine(new Vector2(x2, y2), new Vector2(x2 - len, y2), c, width, true);
        ci.DrawLine(new Vector2(x2, y2), new Vector2(x2, y2 - len), c, width, true);
    }

    // Filled rect with a crisp 1px hairline border (no AA so the 1px edge stays sharp).
    public static void Hairline(CanvasItem ci, Rect2 r, Color fill, Color border, float width = 1f)
    {
        if (fill.A > 0f)
            ci.DrawRect(r, fill, filled: true);
        ci.DrawRect(r, border, filled: false, width);
    }

    private static Vector2[] Close(Vector2[] pts)
    {
        var closed = new Vector2[pts.Length + 1];
        pts.CopyTo(closed, 0);
        closed[pts.Length] = pts[0];
        return closed;
    }
}
