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

    // Axis-aligned diamond (rotated square) — the design's marker dot / blip glyph.
    public static void Diamond(CanvasItem ci, Vector2 center, float size, Color c)
    {
        var pts = new[]
        {
            center + new Vector2(0, -size),
            center + new Vector2(size, 0),
            center + new Vector2(0, size),
            center + new Vector2(-size, 0),
        };
        ci.DrawColoredPolygon(pts, c);
    }

    // Filled rect with a crisp 1px hairline border (no AA so the 1px edge stays sharp).
    public static void Hairline(CanvasItem ci, Rect2 r, Color fill, Color border, float width = 1f)
    {
        if (fill.A > 0f)
            ci.DrawRect(r, fill, filled: true);
        ci.DrawRect(r, border, filled: false, width);
    }

    // Hollow diamond outline + center dot + a short mono tag sitting just above the top vertex —
    // the nav-waypoint / order-glyph marker (waypoint "NAV", commander-order "CMD", rock-order
    // "BUILD"/"MINE"). Four DrawLine segments (a HOLLOW outline, unlike the filled Diamond blip
    // above) so it never reads as a solid contact marker.
    public static void HollowDiamondMarker(CanvasItem ci, Vector2 center, float r, Color color, string tag, Font font, int fontSize)
    {
        Vector2 top = center + new Vector2(0f, -r);
        Vector2 right = center + new Vector2(r, 0f);
        Vector2 bottom = center + new Vector2(0f, r);
        Vector2 left = center + new Vector2(-r, 0f);
        ci.DrawLine(top, right, color, 1.75f, true);
        ci.DrawLine(right, bottom, color, 1.75f, true);
        ci.DrawLine(bottom, left, color, 1.75f, true);
        ci.DrawLine(left, top, color, 1.75f, true);
        ci.DrawCircle(center, r * 0.28f, color);
        float tw = font.GetStringSize(tag, HorizontalAlignment.Left, -1, fontSize).X;
        ci.DrawString(font, center + new Vector2(-tw * 0.5f, -r - 4f), tag, HorizontalAlignment.Left, -1, fontSize, color);
    }

    // Clamp a projected screen point to the inset viewport edge along the ray from its center —
    // the shared off-screen indicator math (nav waypoint / live entities / rock-order glyphs).
    // `point` is the marker's screen point (already un-mirrored for behind-camera points via
    // center*2 - sp), `margin` is the caller's edge inset. Returns the clamped point on the
    // margin-inset rectangle edge, plus the outward unit direction along that ray.
    public static (Vector2 edge, Vector2 dir) ClampToEdge(Vector2 point, Vector2 viewportSize, float margin)
    {
        Vector2 center = viewportSize * 0.5f;
        Vector2 dir = point - center;
        if (dir.LengthSquared() < 1e-4f)
            dir = Vector2.Down;
        dir = dir.Normalized();
        Vector2 half = center - new Vector2(margin, margin);
        float scale = Mathf.Min(half.X / Mathf.Max(Mathf.Abs(dir.X), 1e-4f), half.Y / Mathf.Max(Mathf.Abs(dir.Y), 1e-4f));
        return (center + dir * scale, dir);
    }

    // A filled triangle at `at` pointing along `dir` (unit) — the off-screen edge-arrow glyph.
    public static void DrawEdgeArrow(CanvasItem ci, Vector2 at, Vector2 dir, float size, Color color)
    {
        Vector2 perp = new(-dir.Y, dir.X);
        var pts = new[]
        {
            at + dir * size,
            at - dir * size * 0.5f + perp * size * 0.6f,
            at - dir * size * 0.5f - perp * size * 0.6f,
        };
        ci.DrawColoredPolygon(pts, color);
    }

    private static Vector2[] Close(Vector2[] pts)
    {
        var closed = new Vector2[pts.Length + 1];
        pts.CopyTo(closed, 0);
        closed[pts.Length] = pts[0];
        return closed;
    }
}
