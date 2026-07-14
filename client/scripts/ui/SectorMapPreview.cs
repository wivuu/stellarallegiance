using System;
using System.Collections.Generic;
using Godot;

namespace StellarAllegiance.Ui;

// Compact sector-graph preview for the server browser: one circle per sector — tinted in its owning
// team's color when garrisoned — with gate dots placed by their world X/Z and links drawn as lines
// between sector centers, over a faint grid with corner brackets. Garrison positions WITHIN a sector
// are deliberately not shown (they're randomized per match and secret); only the sector's ownership
// is revealed. Fed by the lobby's advertised map layout — no asteroids, no live entities. Sectors are
// laid out by their authored 2D map-pos (star/custom shapes); a map without map-pos falls back to
// sqrt(radius)-weighted horizontal slots. Renders "NO MAP DATA" for servers that predate the layout.
public sealed partial class SectorMapPreview : Control
{
    // Only which team garrisons the sector — never where inside it. The base's position is
    // randomized per match and is deliberately kept off the wire so it can't be read by a client.
    public sealed record BaseMark(int Team);

    // MapX/MapY (valid when HasMapPos) is the authored 2D diagram position, normalized ~[-1,1].
    public sealed record SectorModel(
        uint Id, float Radius, List<BaseMark> Bases, List<Vector2> Gates, string? Name = null,
        float MapX = 0f, float MapY = 0f, bool HasMapPos = false);

    // Links are bidirectional sector-id pairs (aleph gate topology) drawn as lines between sector
    // node centers. Both feeds populate them: the game lobby from the advertised link list, the
    // server browser derived from each gate's destination sector (see the two MapModel call sites).
    public sealed record MapModel(List<SectorModel> Sectors, List<(uint A, uint B)>? Links = null);

    private const float GridStep = 30f; // px, matches the design's background grid
    private const float GateDot = 3f;

    private MapModel? _map;

    // When set, the matching sector node gets a pulsing highlight ring (the docked-screen
    // CommandSidebar sets this to the selected base's sector). The pulse animates in _Process, but
    // only while this control is on-screen so an off-tab sidebar costs nothing.
    private uint? _highlight;
    public uint? HighlightSector
    {
        get => _highlight;
        set
        {
            if (_highlight == value)
                return;
            _highlight = value;
            QueueRedraw();
        }
    }

    private float _pulse; // 0..Tau phase for the highlight ring

    public SectorMapPreview()
    {
        UiFonts.EnsureLoaded();
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        // Only spend a redraw when there's a highlight ring to animate AND we're actually visible.
        if (_highlight is null || !IsVisibleInTree())
            return;
        _pulse = (_pulse + (float)delta * 3f) % Mathf.Tau;
        QueueRedraw();
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

        var sectors = _map.Sectors;
        float pad = 14f;
        float usableW = Size.X - pad * 2f;
        float usableH = Size.Y - pad * 2f - 14f; // leave room for the caption line

        // Place each sector node (center + circle radius). Authored map-pos → a real 2D layout (star/
        // custom); otherwise fall back to sqrt(radius)-weighted horizontal slots.
        var centers = new Vector2[sectors.Count];
        var radii = new float[sectors.Count];
        LayoutSectors(sectors, pad, usableW, usableH, centers, radii);

        // Gate links first, so the faint connecting lines sit *under* the sector nodes.
        if (_map.Links is { Count: > 0 })
        {
            var indexById = new Dictionary<uint, int>(sectors.Count);
            for (int i = 0; i < sectors.Count; i++)
                indexById[sectors[i].Id] = i;

            var linkColor = DesignTokens.Data with { A = 0.35f };
            foreach (var (a, b) in _map.Links)
            {
                if (indexById.TryGetValue(a, out int ia) && indexById.TryGetValue(b, out int ib)
                    && radii[ia] > 4f && radii[ib] > 4f)
                    DrawLine(centers[ia], centers[ib], linkColor, 1f, antialiased: true);
            }
        }

        for (int i = 0; i < sectors.Count; i++)
        {
            var s = sectors[i];
            var center = centers[i];
            float circleR = radii[i];
            if (circleR <= 4f)
                continue;

            // A garrisoned sector is highlighted in its owning team's color — a tinted ring plus a
            // faint wash — but we deliberately do NOT plot where inside the sector the base sits:
            // that position is randomized per match and is meant to stay secret. Contested/ungarrisoned
            // sectors keep the neutral ring.
            int? team = SectorTeam(s.Bases);
            Color ring = team is int t ? DesignTokens.Faction(t) : DesignTokens.BorderHi;
            if (team is not null)
                DrawCircle(center, circleR, ring with { A = 0.07f });
            DrawArc(center, circleR, 0, Mathf.Tau, 48, ring, team is null ? 1f : 1.5f, antialiased: true);

            // Selected-base highlight: a cyan pulsing ring outside the sector node (chrome accent —
            // the selection cursor is UI chrome, not team identity).
            if (_highlight == s.Id)
            {
                float p = 0.5f + 0.5f * Mathf.Sin(_pulse);
                float rr = circleR + 5f + p * 4f;
                DrawArc(center, rr, 0, Mathf.Tau, 48, DesignTokens.TeamAccent with { A = 0.35f + 0.45f * p }, 2f, antialiased: true);
            }

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

    // Compute each sector's node center + circle radius. If any sector carries an authored map-pos we
    // lay the whole map out in that normalized 2D space (star/custom shapes); otherwise we fall back to
    // sqrt(radius)-weighted horizontal slots (the legacy look for maps/servers without map-pos).
    private void LayoutSectors(
        List<SectorModel> sectors, float pad, float usableW, float usableH, Vector2[] centers, float[] radii)
    {
        bool useMapPos = sectors.Exists(s => s.HasMapPos);
        if (useMapPos)
        {
            // Bounds of the authored positions (unit box fallback for any sector missing a map-pos,
            // which sits at origin). Node circle radius scales with sqrt(radius) but is capped by the
            // spacing so neighbours don't overlap.
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var s in sectors)
            {
                float px = s.HasMapPos ? s.MapX : 0f,
                    py = s.HasMapPos ? s.MapY : 0f;
                minX = Mathf.Min(minX, px);
                maxX = Mathf.Max(maxX, px);
                minY = Mathf.Min(minY, py);
                maxY = Mathf.Max(maxY, py);
            }
            float spanX = Mathf.Max(maxX - minX, 0.001f);
            float spanY = Mathf.Max(maxY - minY, 0.001f);
            float maxRadius = 1f;
            foreach (var s in sectors)
                maxRadius = Mathf.Max(maxRadius, s.Radius);

            // Node radius: a fraction of the smaller usable dimension, shrinking as the map gets busier.
            float nodeR = Mathf.Min(usableW, usableH) * 0.5f / Mathf.Max(2f, Mathf.Sqrt(sectors.Count) + 1f);
            float left = pad + nodeR,
                right = Size.X - pad - nodeR,
                top = pad + nodeR,
                bottom = pad + usableH - nodeR;
            for (int i = 0; i < sectors.Count; i++)
            {
                var s = sectors[i];
                float px = s.HasMapPos ? s.MapX : 0f,
                    py = s.HasMapPos ? s.MapY : 0f;
                float u = (px - minX) / spanX,
                    v = (py - minY) / spanY;
                centers[i] = new Vector2(
                    Mathf.Lerp(left, right, spanX < 0.01f ? 0.5f : u),
                    Mathf.Lerp(top, bottom, spanY < 0.01f ? 0.5f : v)); // +Y down = screen down
                radii[i] = nodeR * Mathf.Sqrt(Mathf.Max(s.Radius, 1f) / maxRadius);
            }
            return;
        }

        // Legacy: horizontal slots sized by sqrt(radius) so a small sector stays legible beside a big one.
        float totalWeight = 0f;
        foreach (var s in sectors)
            totalWeight += Mathf.Sqrt(Mathf.Max(s.Radius, 1f));
        float cx = pad;
        for (int i = 0; i < sectors.Count; i++)
        {
            float weight = Mathf.Sqrt(Mathf.Max(sectors[i].Radius, 1f)) / totalWeight;
            float slotW = usableW * weight;
            centers[i] = new Vector2(cx + slotW / 2f, pad + usableH / 2f);
            radii[i] = Mathf.Min(slotW / 2f, usableH / 2f) - 4f;
            cx += slotW;
        }
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

    // The team a sector belongs to for highlighting: its garrisons' team when they all agree, else
    // null (ungarrisoned, or contested by both teams → neutral ring). We only need ownership here,
    // never the individual base positions.
    private static int? SectorTeam(List<BaseMark> bases)
    {
        if (bases.Count == 0)
            return null;
        int t = bases[0].Team;
        foreach (var b in bases)
            if (b.Team != t)
                return null;
        return t;
    }
}
