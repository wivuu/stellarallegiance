using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;

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
	private const float Margin = 16f;        // inset from the viewport's bottom-left corner
	private const float NodeRadius = 13f;
	private const float HaloRadius = 18f;    // current-sector highlight ring
	private const float LayoutRadius = 50f;  // radius of the ring the sector nodes sit on

	private static readonly Color PanelBg = new(0.04f, 0.05f, 0.08f, 0.72f);
	private static readonly Color PanelEdge = new(0.40f, 0.60f, 0.80f, 0.55f);
	private static readonly Color EdgeColor = new(0.55f, 0.70f, 0.85f, 0.85f);
	private static readonly Color Team0 = new(0.30f, 0.55f, 1.00f);   // matches WorldRenderer team 0
	private static readonly Color Team1 = new(1.00f, 0.40f, 0.34f);   // matches WorldRenderer team 1
	private static readonly Color Disputed = new(0.80f, 0.45f, 1.00f);
	private static readonly Color Neutral = new(0.45f, 0.50f, 0.58f);
	private static readonly Color HaloColor = new(1.00f, 0.95f, 0.50f);
	private static readonly Color NodeEdge = new(0f, 0f, 0f, 0.55f);
	private static readonly Color TextColor = new(0.92f, 0.96f, 1.00f);

	private ConnectionManager _cm = null!;
	private WorldRenderer _world = null!;

	// Screen-space node centers from the last draw, so the F3 overview can hit-test
	// clicks against the sector nodes (see TryClickSector).
	private readonly Dictionary<uint, Vector2> _nodePos = new();

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
		MouseFilter = MouseFilterEnum.Ignore;   // never eat clicks meant for the game/menu
	}

	public override void _Process(double delta) => QueueRedraw();

	public override void _Draw()
	{
		// Sectors, sorted by id so the ring layout is STABLE (id -> slot never changes).
		var sectors = new List<Sector>(_world.MapSectors);
		if (sectors.Count == 0)
			return;
		sectors.Sort((a, b) => a.SectorId.CompareTo(b.SectorId));

		// Panel anchored to the viewport's bottom-left.
		Vector2 view = GetViewportRect().Size;
		Vector2 panelPos = new(Margin, view.Y - PanelH - Margin);
		var panel = new Rect2(panelPos, new Vector2(PanelW, PanelH));
		DrawRect(panel, PanelBg);
		DrawRect(panel, PanelEdge, false, 1.5f);

		var font = GetThemeDefaultFont();
		DrawString(font, panelPos + new Vector2(11, 19), "SECTOR MAP", HorizontalAlignment.Left, -1, 13, PanelEdge);

		// Deterministic ring layout (centered below the title). Index -> angle is fixed,
		// so the nodes hold their places regardless of the random aleph geometry.
		Vector2 center = panelPos + new Vector2(PanelW * 0.5f, PanelH * 0.5f + 12f);
		int n = sectors.Count;
		var pos = _nodePos;   // populate the field so clicks can hit-test these centers
		pos.Clear();
		for (int i = 0; i < n; i++)
		{
			Vector2 p = center;
			if (n > 1)
			{
				float ang = Mathf.Pi + i * (Mathf.Tau / n);   // first node on the left
				p = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * LayoutRadius;
			}
			pos[sectors[i].SectorId] = p;
		}

		// Edges: one line per aleph PAIR (dedupe by unordered sector pair).
		var drawn = new HashSet<long>();
		foreach (var (sector, dest) in _world.MapAlephLinks)
		{
			uint lo = sector < dest ? sector : dest;
			uint hi = sector < dest ? dest : sector;
			if (!drawn.Add(((long)lo << 32) | hi))
				continue;
			if (pos.TryGetValue(sector, out var p0) && pos.TryGetValue(dest, out var p1))
				DrawLine(p0, p1, EdgeColor, 2f, true);
		}

		// Which team(s) hold a base in each sector.
		var teams = new Dictionary<uint, (bool t0, bool t1)>();
		foreach (var (sector, team) in _world.MapBaseTeams)
		{
			teams.TryGetValue(sector, out var cur);
			if (team == 0) cur.t0 = true; else if (team == 1) cur.t1 = true;
			teams[sector] = cur;
		}

		// Nodes (drawn after edges so they sit on top of the lines).
		uint localSector = _world.LocalSector;
		foreach (var s in sectors)
		{
			Vector2 p = pos[s.SectorId];
			Color fill = Neutral;
			if (teams.TryGetValue(s.SectorId, out var tp))
				fill = tp.t0 && tp.t1 ? Disputed : tp.t0 ? Team0 : tp.t1 ? Team1 : Neutral;

			if (s.SectorId == localSector)
				DrawCircle(p, HaloRadius, HaloColor);   // current-sector halo
			DrawCircle(p, NodeRadius, fill);
			DrawArc(p, NodeRadius, 0f, Mathf.Tau, 24, NodeEdge, 1.5f, true);

			string lbl = s.SectorId.ToString();
			Vector2 ts = font.GetStringSize(lbl, HorizontalAlignment.Left, -1, 12);
			DrawString(font, p + new Vector2(-ts.X * 0.5f, ts.Y * 0.30f), lbl, HorizontalAlignment.Left, -1, 12, TextColor);
		}

		// Current-sector name along the panel's bottom edge.
		string name = _world.SectorName(localSector);
		if (!string.IsNullOrEmpty(name))
			DrawString(font, panelPos + new Vector2(11, PanelH - 9), name, HorizontalAlignment.Left, PanelW - 22, 12, HaloColor);
	}
}
