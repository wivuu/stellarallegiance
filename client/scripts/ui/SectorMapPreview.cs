using System;
using System.Collections.Generic;
using Godot;

namespace StellarAllegiance.Ui;

// Compact sector-graph preview for the server browser: one circle per sector (Core + Verge
// today), team-colored base diamonds and gate dots placed by their world X/Z inside each
// circle, over a faint grid with corner brackets. Fed by the lobby's advertised map layout —
// no asteroids, no live entities. Renders a "NO MAP DATA" placeholder for servers that
// predate the layout field.
public sealed partial class SectorMapPreview : Control
{
    public sealed record BaseMark(int Team, Vector2 Pos);

    public sealed record SectorModel(uint Id, float Radius, List<BaseMark> Bases, List<Vector2> Gates, string? Name = null);

    public sealed record MapModel(List<SectorModel> Sectors);

    private const float GridStep = 30f; // px, matches the design's background grid
    private const float BaseDiamond = 5f; // half-diagonal of a base marker
    private const float GateDot = 3f;

    private MapModel? _map;

    public SectorMapPreview()
    {
        UiFonts.EnsureLoaded();
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void SetMap(MapModel? map)
    {
        if (Equals(map, _map))
            return;
        _map = map;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, DesignTokens.Well, filled: true);

        // Faint alignment grid, then the bracket frame on top.
        var grid = DesignTokens.BorderLo with { A = 0.06f };
        for (float x = GridStep; x < Size.X; x += GridStep)
            DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), grid);
        for (float y = GridStep; y < Size.Y; y += GridStep)
            DrawLine(new Vector2(0, y), new Vector2(Size.X, y), grid);
        UiDraw.CornerBrackets(this, r, DesignTokens.BracketLength, DesignTokens.TeamAccent);

        Font mono = UiFonts.Mono;
        if (_map is null || _map.Sectors.Count == 0)
        {
            string placeholder = "NO MAP DATA";
            float w = mono.GetStringSize(placeholder, fontSize: 12).X;
            DrawString(
                mono,
                new Vector2((Size.X - w) / 2f, Size.Y / 2f + 4f),
                placeholder,
                fontSize: 12,
                modulate: DesignTokens.TextDim
            );
            return;
        }

        // Sectors don't share a coordinate space, so each gets a horizontal slot sized by
        // sqrt(radius) — keeps the small Verge legible beside the much larger Core.
        var sectors = _map.Sectors;
        float pad = 14f;
        float totalWeight = 0f;
        foreach (var s in sectors)
            totalWeight += Mathf.Sqrt(Mathf.Max(s.Radius, 1f));

        float usableW = Size.X - pad * 2f;
        float usableH = Size.Y - pad * 2f - 14f; // leave room for the caption line
        float cx = pad;
        foreach (var s in sectors)
        {
            float weight = Mathf.Sqrt(Mathf.Max(s.Radius, 1f)) / totalWeight;
            float slotW = usableW * weight;
            float circleR = Mathf.Min(slotW / 2f, usableH / 2f) - 4f;
            var center = new Vector2(cx + slotW / 2f, pad + usableH / 2f);
            cx += slotW;
            if (circleR <= 4f)
                continue;

            DrawArc(center, circleR, 0, Mathf.Tau, 48, DesignTokens.BorderHi, 1f, antialiased: true);

            foreach (var b in s.Bases)
                DrawDiamond(MapPoint(center, circleR, b.Pos, s.Radius), BaseDiamond, DesignTokens.Faction(b.Team));

            foreach (var g in s.Gates)
            {
                var p = MapPoint(center, circleR, g, s.Radius);
                DrawCircle(p, GateDot, DesignTokens.Data with { A = 0.9f });
                DrawArc(p, GateDot + 2f, 0, Mathf.Tau, 16, DesignTokens.Data with { A = 0.4f }, 1f, antialiased: true);
            }

            string label = string.IsNullOrEmpty(s.Name) ? $"S{s.Id}" : s.Name;
            DrawString(mono, center + new Vector2(-6f, circleR + 12f), label, fontSize: 10, modulate: DesignTokens.TextDim);
        }

        DrawString(
            mono,
            new Vector2(pad, Size.Y - 8f),
            $"// SECTOR MAP · {sectors.Count} SECTORS",
            fontSize: 10,
            modulate: DesignTokens.TextDim
        );
    }

    // World X/Z (sector-local, |pos| <= radius-ish) → pixel position inside the sector circle.
    private static Vector2 MapPoint(Vector2 center, float circleR, Vector2 world, float sectorRadius)
    {
        float scale = circleR / Mathf.Max(sectorRadius, 1f);
        var p = world * scale;
        // Clamp inside the circle so slightly-outside markers (outer-rim gates) stay visible.
        float len = p.Length();
        float max = circleR - 4f;
        if (len > max && len > 0f)
            p *= max / len;
        return center + p;
    }

    private void DrawDiamond(Vector2 c, float half, Color color)
    {
        var pts = new[]
        {
            c + new Vector2(0, -half),
            c + new Vector2(half, 0),
            c + new Vector2(0, half),
            c + new Vector2(-half, 0),
        };
        DrawColoredPolygon(pts, color);
    }
}
