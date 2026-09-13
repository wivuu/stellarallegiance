using Godot;

namespace StellarAllegiance.Ui;

// HUD strip for a CREW GUNNER riding a captain's ship (v41 crews, slice 1). While you man a turret
// station you have no hull of your own — the camera chases the captain's — so this top-centre bracket
// strip is the one piece of chrome that says whose ship you're on, which station you hold, and what it
// mounts. Built from the roster primitives (RosterCells) so it reads as the same family as the Lobby
// roster and the Scoreboard; mouse-transparent like every other HUD overlay, and hidden whenever a
// full-screen surface (F3 map, hangar) owns the screen.
//
// Aiming and firing the turret is the NEXT slice — the strip says so out loud rather than implying
// controls that don't exist yet. Wired up by the Hud alongside the other overlays; the UiShowcase
// renders it from SetMock so the gallery needs no live world.
public partial class GunnerStrip : Control
{
    private const int TopMargin = 52; // clear of the ViewModeIndicator chip at y≈28

    private WorldRenderer? _world;
    private GameNetClient? _net;
    private DefRegistry? _defs;

    private HBoxContainer _row = null!;

    // Repaint gate: the strip re-texts only when the crew roster actually changed shape (CrewStore
    // bumps Version on structural change only) or the captain's callsign resolved/changed.
    private int _version = int.MinValue;
    private string _captain = "";

    // Showcase fixture: when set, the strip renders this text and never consults a world.
    private (string SeatId, string Gun, string Captain, string Hull)? _mock;

    public void Init(WorldRenderer world, GameNetClient net, DefRegistry defs)
    {
        _world = world;
        _net = net;
        _defs = defs;
        Build();
        Visible = false;
    }

    // Render standalone from fixture text (UiShowcase). No world, no net, no defs.
    public void SetMock(string seatId, string gun, string captain, string hull)
    {
        _mock = (seatId, gun, captain, hull);
        Build();
        Paint(seatId, gun, captain, hull);
        // In game this node is a full-rect HUD overlay whose content is anchored, so it needs no
        // minimum. The gallery stacks it in a VBox, which sizes children by their MINIMUM — anchored
        // content contributes none — so reserve the strip's real height (top margin + panel) there.
        CustomMinimumSize = new Vector2(0, TopMargin + 68);
        Visible = true;
    }

    private void Build()
    {
        if (_row != null && IsInstanceValid(_row))
            return;
        UiFonts.EnsureLoaded();
        // AnchorsAndOffsets, not the plain preset: this node is code-built, so its offsets must be
        // driven from the anchors rather than preserved (see DESIGN.md).
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        // Top-centre: a full-rect margin box holds a centred HBox; the panel shrinks to the top of it
        // so the strip hugs the screen edge instead of floating mid-screen.
        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        pad.AddThemeConstantOverride("margin_top", TopMargin);
        AddChild(pad);

        var center = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        pad.AddChild(center);

        var panel = new BracketPanel
        {
            FillOverride = DesignTokens.PanelSolid,
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        center.AddChild(panel);

        _row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 12);
        panel.AddChild(_row);
    }

    // Cells are rebuilt whole — the strip is six controls and only re-texts on a real change.
    private void Paint(string seatId, string gun, string captain, string hull)
    {
        foreach (var c in _row.GetChildren())
            c.QueueFree();
        _row.AddChild(RosterCells.Badge("GUNNER", DesignTokens.TeamAccent));
        _row.AddChild(RosterCells.Mono($"{seatId} · {gun}", DesignTokens.TextHi));
        _row.AddChild(RosterCells.Hairline(vertical: true));
        _row.AddChild(RosterCells.Mono($"CAPTAIN {captain}", DesignTokens.Data));
        _row.AddChild(RosterCells.Mono(hull, DesignTokens.Text2));
        _row.AddChild(RosterCells.Hairline(vertical: true));
        _row.AddChild(RosterCells.Mono("TURRET CONTROLS · NEXT SLICE", DesignTokens.TextDim));
    }

    public override void _Process(double delta)
    {
        if (_mock is not null)
            return; // showcase fixture: painted once, always visible
        if (_world == null || _net == null || _defs == null)
            return;

        // The strip belongs to the flight view only: a full-screen surface owning the screen hides it.
        bool riding = _world.Ships.Riding && !SectorOverview.Active && !ShipLoadout.Active;
        Visible = riding;
        if (!riding)
            return;
        if (_world.Crew.SeatOf(_net.LocalClientId) is not { } seat)
            return; // riding without a seat can only be a one-frame race; keep the last paint

        string captain = NameOf(seat.CaptainId);
        if (_world.Crew.Version == _version && captain == _captain)
            return;
        _version = _world.Crew.Version;
        _captain = captain;

        string gun = _defs.GetWeapon(seat.WeaponId)?.Name.ToUpperInvariant() ?? "—";
        string hull = _defs.TryGetShipDef(seat.ClassId, out var def)
            ? (string.IsNullOrEmpty(def.Glyph) ? def.Name : $"{def.Glyph} {def.Name}").ToUpperInvariant()
            : "";
        Paint(CrewStore.SeatId(seat.SeatIndex), gun, captain, hull);
    }

    // Callsign for a client id from the lobby roster (snapshots carry no identity) — the same lookup
    // the Scoreboard's live cells do. Falls back to the raw id so the strip never reads blank.
    private string NameOf(int clientId)
    {
        foreach (var p in _net!.LobbyPlayers)
            if (p.Id == clientId && !string.IsNullOrEmpty(p.Name))
                return p.Name.ToUpperInvariant();
        return $"#{clientId}";
    }
}
