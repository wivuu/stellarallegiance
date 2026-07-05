using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Ui;

// Always-on minimap in the bottom-left: a 2D node-link diagram of the sector graph.
// Each sector is a circle; each aleph pair is a line between the two sectors it links.
// The NODE LAYOUT is stable — sectors are placed deterministically (a ring ordered by
// sector id), so the map doesn't reshuffle even though the alephs' in-world positions
// are random. The player's current sector is haloed, and each node is tinted by which
// team(s) hold a base there (the plan's team-presence coloring).
//
// Pure overlay: it reads the subscribed Sector / Aleph / Base cache each frame and
// draws. It never touches authoritative state. Created and wired up by the Hud.
public partial class Minimap : Control
{
    private const float PanelW = 196f;
    private const float PanelH = 176f;
    private const float Margin = 16f; // inset from the viewport's bottom-left corner
    private const float NodeRadius = 13f;
    private const float HaloRadius = 18f; // current-sector highlight ring
    private const float LayoutRadius = 50f; // radius of the ring the sector nodes sit on

    // Chrome pulls from the shared "Stellar Allegiance" design tokens. Team identity stays
    // the faction colours (NOT the cyan structural accent); the current-sector marker and
    // panel brackets are chrome, so they use the accent.
    private static readonly Color PanelBg = DesignTokens.PanelFill;
    private static readonly Color BracketColor = DesignTokens.TeamAccent;
    private static readonly Color EdgeColor = DesignTokens.BorderHi; // quiet aleph link
    private static readonly Color FrontEdge = DesignTokens.Warn; // link touching a contested sector
    private static readonly Color Team0 = DesignTokens.Faction0;
    private static readonly Color Team1 = DesignTokens.Faction1;
    private static readonly Color Disputed = DesignTokens.Warn; // held by both teams (contested)
    private static readonly Color Neutral = DesignTokens.TextDim;
    private static readonly Color HaloColor = DesignTokens.TeamAccent; // current-sector ring (chrome)
    private static readonly Color NodeEdge = DesignTokens.Void; // dark diamond outline
    private static readonly Color HeaderColor = DesignTokens.Data; // mono header
    private static readonly Color TextColor = DesignTokens.TextHi;

    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;

    // Screen-space node centers from the last draw, so the F3 overview can hit-test
    // clicks against the sector nodes (see TryClickSector).
    private readonly Dictionary<uint, Vector2> _nodePos = new();

    // Reused across draws to avoid per-frame allocation.
    private readonly List<Sector> _sectorsBuf = new();
    private readonly HashSet<long> _drawnEdges = new();
    private readonly Dictionary<uint, (bool t0, bool t1)> _teams = new();
    private readonly Vector2[] _diamond = new Vector2[4]; // rotated-square node marker
    private readonly Vector2[] _diamondClosed = new Vector2[5]; // _diamond + first point, for the outline

    // Hit-test a viewport-space point against the drawn sector nodes. Returns the
    // sector id of the node under the point, if any (used to retarget the F3 overview).
    public bool TryClickSector(Vector2 point, out uint sector)
    {
        foreach (var (id, p) in _nodePos)
            if (point.DistanceTo(p) <= NodeRadius + 3f)
            {
                sector = id;
                return true;
            }
        sector = 0;
        return false;
    }

    public void Init(ConnectionManager cm, WorldRenderer world)
    {
        _cm = cm;
        _world = world;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // never eat clicks meant for the game/menu
        UiFonts.EnsureLoaded(); // custom-draw node reads the mono font directly, not via a Theme
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        // Sectors, sorted by id so the ring layout is STABLE (id -> slot never changes).
        _sectorsBuf.Clear();
        _sectorsBuf.AddRange(_world.MapSectors);
        if (_sectorsBuf.Count == 0)
            return;
        _sectorsBuf.Sort(static (a, b) => a.SectorId.CompareTo(b.SectorId));
        var sectors = _sectorsBuf;

        // Panel anchored to the viewport's bottom-left.
        Vector2 view = GetViewportRect().Size;
        Vector2 panelPos = new(Margin, view.Y - PanelH - Margin);
        var panel = new Rect2(panelPos, new Vector2(PanelW, PanelH));
        UiDraw.Hairline(this, panel, PanelBg, DesignTokens.BorderLo);
        UiDraw.CornerBrackets(this, panel, DesignTokens.BracketLength, BracketColor);

        var font = UiFonts.Mono;
        DrawString(font, panelPos + new Vector2(12, 20), "▶ SECTOR MAP", HorizontalAlignment.Left, -1, 11, HeaderColor);

        // Deterministic ring layout (centered below the title). Index -> angle is fixed,
        // so the nodes hold their places regardless of the random aleph geometry.
        Vector2 center = panelPos + new Vector2(PanelW * 0.5f, PanelH * 0.5f + 12f);
        int n = sectors.Count;
        var pos = _nodePos; // populate the field so clicks can hit-test these centers
        pos.Clear();
        for (int i = 0; i < n; i++)
        {
            Vector2 p = center;
            if (n > 1)
            {
                float ang = Mathf.Pi + i * (Mathf.Tau / n); // first node on the left
                p = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * LayoutRadius;
            }
            pos[sectors[i].SectorId] = p;
        }

        // Which team(s) hold a base in each sector — computed first so contested sectors can
        // tint the aleph links that touch them (the design's front-line cue).
        _teams.Clear();
        foreach (var (sector, team) in _world.MapBaseTeams)
        {
            _teams.TryGetValue(sector, out var cur);
            if (team == 0)
                cur.t0 = true;
            else if (team == 1)
                cur.t1 = true;
            _teams[sector] = cur;
        }

        // Edges: one line per aleph PAIR (dedupe by unordered sector pair). A link touching a
        // contested (both-team) sector is tinted as a front line.
        _drawnEdges.Clear();
        foreach (var (sector, dest) in _world.MapAlephLinks)
        {
            uint lo = sector < dest ? sector : dest;
            uint hi = sector < dest ? dest : sector;
            if (!_drawnEdges.Add(((long)lo << 32) | hi))
                continue;
            if (pos.TryGetValue(sector, out var p0) && pos.TryGetValue(dest, out var p1))
                DrawLine(p0, p1, IsContested(sector) || IsContested(dest) ? FrontEdge : EdgeColor, 2f, true);
        }

        // Nodes (drawn after edges so they sit on top of the lines). Rotated-diamond markers
        // per the design: held sectors fill with the faction colour, neutral draws hollow,
        // and the current sector gets a cyan accent ring.
        uint localSector = _world.LocalSector;
        foreach (var s in sectors)
        {
            Vector2 p = pos[s.SectorId];
            bool neutral = true;
            Color fill = Neutral;
            if (_teams.TryGetValue(s.SectorId, out var tp) && (tp.t0 || tp.t1))
            {
                neutral = false;
                fill = tp.t0 && tp.t1 ? Disputed : tp.t0 ? Team0 : Team1;
            }

            // Fog stale memory: a single team's base(s) here are all destroyed but still remembered.
            // Dim the tint to a hollow outline so the map reads it as lost ground, not a live hold.
            // (Disputed sectors keep the live tint — a mixed presence isn't cleanly "stale".)
            bool stale = !neutral && !(tp.t0 && tp.t1) && _world.SectorTeamStale(s.SectorId, tp.t0 ? (byte)0 : (byte)1);

            bool current = s.SectorId == localSector;
            if (current)
                DrawArc(p, HaloRadius, 0f, Mathf.Tau, 28, HaloColor, 1.5f, true); // current-sector ring

            DiamondPoints(p, current ? NodeRadius + 2f : NodeRadius, _diamond);
            bool hollow = neutral || stale;
            if (neutral)
                DrawPolyline(ClosedDiamond(), Neutral, 1.5f, true); // hollow outline
            else if (stale)
                DrawPolyline(ClosedDiamond(), new Color(fill, 0.4f), 1.5f, true); // stale-memory: dim hollow
            else
            {
                DrawColoredPolygon(_diamond, fill);
                DrawPolyline(ClosedDiamond(), NodeEdge, 1f, true);
            }

            string lbl = s.SectorId.ToString();
            Vector2 ts = font.GetStringSize(lbl, HorizontalAlignment.Left, -1, 11);
            DrawString(font, p + new Vector2(-ts.X * 0.5f, ts.Y * 0.30f), lbl, HorizontalAlignment.Left, -1, 11, hollow ? TextColor : NodeEdge);
        }

        // Current-sector name along the panel's bottom edge.
        string name = _world.SectorName(localSector);
        if (!string.IsNullOrEmpty(name))
            DrawString(
                font,
                panelPos + new Vector2(12, PanelH - 9),
                name,
                HorizontalAlignment.Left,
                PanelW - 24,
                11,
                HaloColor
            );
    }

    // A sector held by BOTH teams — drawn/treated as contested (front line).
    private bool IsContested(uint sectorId) => _teams.TryGetValue(sectorId, out var tp) && tp.t0 && tp.t1;

    // Fill `arr` with the four points of a 45°-rotated square (diamond) of "radius" r at p.
    private static void DiamondPoints(Vector2 p, float r, Vector2[] arr)
    {
        arr[0] = p + new Vector2(0f, -r);
        arr[1] = p + new Vector2(r, 0f);
        arr[2] = p + new Vector2(0f, r);
        arr[3] = p + new Vector2(-r, 0f);
    }

    // The current _diamond closed back to its first point, for a DrawPolyline outline.
    private Vector2[] ClosedDiamond()
    {
        _diamond.CopyTo(_diamondClosed, 0);
        _diamondClosed[4] = _diamond[0];
        return _diamondClosed;
    }
}
