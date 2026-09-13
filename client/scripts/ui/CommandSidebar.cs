using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  CommandSidebar.cs — shared 340px column on the docked screen (Phase A)
//
//  Present on every tab (HANGAR / BUILD / RESEARCH). Top: a "COMMAND NETWORK" header over an
//  embedded SectorMapPreview showing the live map (selected base's sector gets a pulsing ring).
//  Below a divider: a "YOUR BASES" list — one selectable row per friendly base (glyph tile, title,
//  sector name, ACTIVE/DESTROYED status). Selecting a base raises BaseSelected and highlights its
//  sector on the map; Phase A stores the pick in LoadoutState.Shared.SelectedBaseId (display-only).
//
//  Data comes from WorldRenderer.Bases.Known()/MapSectors/Bases.Teams/Alephs.Links, filtered to
//  the local team. It exposes only what MsgBases already streams (never a secret base position).
//  The showcase feeds mock rows straight through SetData — no baked data lives in this component.
// =====================================================================
public partial class CommandSidebar : Control
{
    public const float Width = 340f;

    // A friendly base as shown in the list. Title/SectorName are display strings resolved by the caller
    // (Refresh, or the showcase for mocks). The optional Research* fields let the showcase preview the
    // live "RESEARCHING …/ON DECK" row variants without a server (live data flows via UpdateResearchLines).
    public readonly record struct BaseEntry(
        ulong Id,
        string Title,
        string SectorName,
        uint Sector,
        bool Alive,
        byte TypeId = 0,
        string? ResearchName = null,
        float ResearchProgress = 0f,
        bool ResearchOnDeck = false,
        int ResearchMore = 0
    );

    // ---- CREWED SHIPS (v41 crews) ------------------------------------------
    // One crew-served turret station on a teammate's docked hull, as the join list shows it.
    // Seat is the station's hardpoint index — exactly what SendCrewSeat takes — and SeatId its
    // display name ("T1"). GunnerId < 0 means open; GunnerName is the resolved callsign.
    public readonly record struct CrewSeatEntry(byte Seat, string SeatId, string GunName, int GunnerId, string GunnerName)
    {
        public bool IsOpen => GunnerId < 0;
    }

    // One crewable ship in the TAKE A TURRET list: whose it is, what it is, and every station.
    // ShipId 0 = the captain is still docked, which is also the "joinable" flag (boarding is
    // docked-only) — a flying ship's card reads IN FLIGHT and offers no JOIN.
    public readonly record struct CrewShipEntry(
        int CaptainId,
        string CaptainName,
        string ClassName,
        string Glyph,
        ulong ShipId,
        IReadOnlyList<CrewSeatEntry> Seats
    );

    // Raised on a click that WANTS a seat change; the sidebar never mutates seat state itself (the
    // store is server-driven, so an optimistic paint would fight the next frame).
    public event Action<int, byte>? SeatJoinRequested;
    public event Action? SeatLeaveRequested;

    public event Action<ulong>? BaseSelected;
    public ulong SelectedBaseId { get; private set; }

    // Display strings for the selected base, mirrored so the docked-screen top bar / launch footer can
    // read the same label the sidebar shows. Empty when nothing is selected.
    public string SelectedTitle { get; private set; } = "";
    public string SelectedSectorName { get; private set; } = "";

    // BaseDef.BaseTypeId of the selected base — lets the Research tab match a station-upgrade dev to
    // its from-type (so "Upgrade Supremacy" only offers on a Supremacy). 0 when nothing is selected.
    public byte SelectedBaseType { get; private set; }

    private WorldRenderer? _world;
    private GameNetClient? _net;
    private DefRegistry? _defs;

    private SectorMapPreview _map = null!;
    private VBoxContainer _rowsBox = null!;
    private VBoxContainer _crewSection = null!;
    private VBoxContainer _crewBox = null!;

    // UI-only double-click guard: seat changes are round-trips, so a second click inside half a
    // second can't fire another one (Time.GetTicksMsec is fine here — nothing simulation-facing).
    private ulong _seatClickReadyMs;
    private readonly List<(ulong Id, uint Sector, string Title, string SectorName, byte TypeId, BaseRow Row)> _rows = new();

    public void Init(WorldRenderer world, GameNetClient net, DefRegistry? defs = null)
    {
        _world = world;
        _net = net;
        _defs = defs;
    }

    public override void _Ready()
    {
        UiFonts.EnsureLoaded();
        // Pin the width; preserve any author-set height (the docked screen stretches us vertically in
        // an HBox, but the showcase gives an explicit height since a VBox won't).
        CustomMinimumSize = new Vector2(Width, CustomMinimumSize.Y);
        SizeFlagsVertical = SizeFlags.Fill;
        MouseFilter = MouseFilterEnum.Stop;

        var pad = new MarginContainer();
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 14);
        pad.AddThemeConstantOverride("margin_top", 18);
        pad.AddThemeConstantOverride("margin_bottom", 18);
        AddChild(pad);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        pad.AddChild(col);

        col.AddChild(UiKit.MakeLabel("▶ COMMAND NETWORK", UiKit.TextStyle.Label, DesignTokens.TextDim));

        _map = new SectorMapPreview { CustomMinimumSize = new Vector2(0, 170) };
        col.AddChild(_map);

        col.AddChild(new DiamondDivider());

        // Bases AND the crew list share one scroll: on a tall roster the column would otherwise push
        // the crew section off the bottom of a fixed-height sidebar with no way to reach it.
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        col.AddChild(scroll);
        var scrollCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scrollCol.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(scrollCol);

        var basesPanel = new HairlinePanel { Title = "YOUR BASES", SizeFlagsVertical = SizeFlags.ShrinkBegin };
        scrollCol.AddChild(basesPanel);
        _rowsBox = new VBoxContainer();
        _rowsBox.AddThemeConstantOverride("separation", 7);
        basesPanel.AddChild(_rowsBox);

        // CREWED SHIPS · TAKE A TURRET — hidden outright while no teammate advertises a crewable hull
        // (the section only earns its space once there's something to join).
        _crewSection = new VBoxContainer { Visible = false };
        _crewSection.AddThemeConstantOverride("separation", 10);
        scrollCol.AddChild(_crewSection);
        _crewSection.AddChild(new DiamondDivider());
        var crewHead = new HBoxContainer();
        var crewTitle = UiKit.MakeLabel("CREWED SHIPS", UiKit.TextStyle.Label, DesignTokens.TextDim);
        crewTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var crewHint = UiKit.MakeLabel("TAKE A TURRET", UiKit.TextStyle.Data, DesignTokens.TextDim);
        crewHint.AddThemeFontSizeOverride("font_size", DesignTokens.MicroSize);
        crewHead.AddChild(crewTitle);
        crewHead.AddChild(crewHint);
        _crewSection.AddChild(crewHead);
        _crewBox = new VBoxContainer();
        _crewBox.AddThemeConstantOverride("separation", 8);
        _crewSection.AddChild(_crewBox);

        // Politely wait for world data (mirrors the ship list's "awaiting" guard).
        _rowsBox.AddChild(UiKit.MakeLabel("AWAITING BASE TELEMETRY…", UiKit.TextStyle.Data, DesignTokens.TextDim));

        Refresh();
    }

    // Re-pull live base data from the WorldRenderer (no-op in the showcase, which drives SetData directly).
    public void Refresh()
    {
        if (_world == null || _rowsBox == null)
            return;
        byte team = _world.LocalTeam ?? _net?.MyTeam ?? 0;

        // Sector id -> name, for the row's location line.
        var sectorNames = new Dictionary<uint, string>();
        foreach (Sector s in _world.MapSectors)
            sectorNames[s.SectorId] = string.IsNullOrEmpty(s.Name) ? $"SECTOR {s.SectorId}" : s.Name.ToUpperInvariant();

        // Name each base by TYPE · SECTOR (e.g. "OUTPOST · CINDER BELT"). When two same-type bases share
        // a sector, the second+ get a numeric suffix so they stay distinct.
        var entries = new List<BaseEntry>();
        var seen = new Dictionary<(string, uint), int>();
        foreach (var (id, sector, bteam, alive, typeId) in _world.Bases.Known())
        {
            if (bteam != team)
                continue; // never surface enemy bases beyond what the map already reveals
            string sname = sectorNames.TryGetValue(sector, out string? nm) ? nm : $"SECTOR {sector}";
            string typeName = (_defs?.GetBaseDef(typeId)?.Name ?? "BASE").ToUpperInvariant();
            int k = seen.TryGetValue((typeName, sector), out int c) ? c + 1 : 1;
            seen[(typeName, sector)] = k;
            string label = k > 1 ? $"{typeName} · {sname} {k}" : $"{typeName} · {sname}";
            entries.Add(new BaseEntry(id, label, sname, sector, alive, typeId));
        }

        SetData(entries, BuildMapModel(_world));
        UpdateResearchLines();
    }

    private double _researchTimer;

    public override void _Process(double delta)
    {
        // Live research lines refresh on their own cadence (base-set Refresh is gated on a change sig
        // upstream, so it won't fire when only research state changes).
        if (_world == null)
            return;
        _researchTimer -= delta;
        if (_researchTimer > 0)
            return;
        _researchTimer = 0.5;
        UpdateResearchLines();
    }

    // Repaint each row's research line from live per-base research state (no row rebuild).
    private void UpdateResearchLines()
    {
        if (_world == null || _defs == null)
            return;
        foreach (var (id, _, _, _, _, row) in _rows)
        {
            var res = _world.TeamState.ResearchAt(id);
            if (res is TeamStateStore.BaseResearch r && r.Active.Length > 0)
            {
                var a = r.Active[0];
                string name = _defs.GetDevelopment(a.DevIndex)?.Name.ToUpperInvariant() ?? $"DEV {a.DevIndex}";
                string mmss = TechDetailPanel.MmssRemaining(_world, a.StartTick, a.DurationTicks);
                string more = r.Active.Length > 1 ? $"  +{r.Active.Length - 1} more" : "";
                row.SetResearch(
                    $"◷ {name} · {mmss}{more}",
                    DesignTokens.Warn,
                    _world.TeamState.ResearchProgress(a.StartTick, a.DurationTicks),
                    showBar: true
                );
            }
            else if (res is TeamStateStore.BaseResearch r2 && r2.OnDeck is ushort od)
            {
                string name = _defs.GetDevelopment(od)?.Name.ToUpperInvariant() ?? $"DEV {od}";
                row.SetResearch($"⊕ ON DECK {name}", DesignTokens.Data, 0f, showBar: false);
            }
            else
                row.SetResearch(null, default, 0f, false);
        }
    }

    // Build the sidebar's live map model from the streamed world layout (same shape the lobby builds).
    private static SectorMapPreview.MapModel BuildMapModel(WorldRenderer world)
    {
        var sectors = new List<SectorMapPreview.SectorModel>();
        foreach (Sector s in world.MapSectors)
        {
            var bases = new List<SectorMapPreview.BaseMark>();
            foreach (var (sec, bteam) in world.Bases.Teams)
                if (sec == s.SectorId)
                    bases.Add(new SectorMapPreview.BaseMark(bteam));
            sectors.Add(
                new SectorMapPreview.SectorModel(
                    s.SectorId,
                    s.Radius,
                    bases,
                    new List<Vector2>(),
                    string.IsNullOrEmpty(s.Name) ? null : s.Name,
                    s.MapPosX,
                    s.MapPosY,
                    s.HasMapPos
                )
            );
        }
        var links = new List<(uint A, uint B)>();
        foreach (var (sec, dest) in world.Alephs.Links)
            links.Add((sec, dest));
        return new SectorMapPreview.MapModel(sectors, links);
    }

    // Rebuild the base rows + map from an explicit dataset. Refresh() feeds live data; the showcase
    // feeds mocks. Auto-selects the first base when the current selection is gone.
    public void SetData(IReadOnlyList<BaseEntry> bases, SectorMapPreview.MapModel? map)
    {
        _map.SetMap(map);

        foreach (Node child in _rowsBox.GetChildren())
            child.QueueFree();
        _rows.Clear();

        if (bases.Count == 0)
        {
            _rowsBox.AddChild(UiKit.MakeLabel("NO FRIENDLY BASES", UiKit.TextStyle.Data, DesignTokens.TextDim));
            SelectedBaseId = 0;
            SelectedTitle = "";
            SelectedSectorName = "";
            SelectedBaseType = 0;
            _map.HighlightSector = null;
            return;
        }

        foreach (BaseEntry e in bases)
        {
            var row = new BaseRow();
            row.Configure(e.Title, e.SectorName, e.Alive);
            // Mock research line (showcase only — live data flows through UpdateResearchLines).
            if (!string.IsNullOrEmpty(e.ResearchName))
            {
                if (e.ResearchOnDeck)
                    row.SetResearch($"⊕ ON DECK {e.ResearchName}", DesignTokens.Data, 0f, showBar: false);
                else
                {
                    string more = e.ResearchMore > 0 ? $"  +{e.ResearchMore} more" : "";
                    row.SetResearch($"◷ {e.ResearchName}{more}", DesignTokens.Warn, e.ResearchProgress, showBar: true);
                }
            }
            else
                row.SetResearch(null, default, 0f, false);
            ulong id = e.Id;
            row.Pressed += () => Select(id);
            _rowsBox.AddChild(row);
            _rows.Add((e.Id, e.Sector, e.Title, e.SectorName, e.TypeId, row));
        }

        // Keep the current selection if it still exists; else default to the base the pilot last
        // docked at (relaunch from where you docked); else the first base in list order. The sidebar
        // is rebuilt fresh each time the hangar opens (SelectedBaseId starts 0), so this default
        // applies once per dock — a deliberate click here overrides it, and the next dock moves it.
        bool stillPresent = false;
        foreach (var (id, _, _, _, _, _) in _rows)
            if (id == SelectedBaseId)
                stillPresent = true;

        ulong pick;
        if (stillPresent)
            pick = SelectedBaseId;
        else
        {
            pick = _rows[0].Id;
            ulong lastDocked = _world?.LastDockedBaseId ?? 0;
            if (lastDocked != 0)
                foreach (var (id, _, _, _, _, _) in _rows)
                    if (id == lastDocked)
                        pick = lastDocked;
        }
        Select(pick);
        ApplyRowHints(); // rows were rebuilt — re-dress them with the current launch hints
    }

    // Per-base launch hints (2026-07-21 launch-station-classes): base id -> the reason the
    // currently selected hull can't launch there ("SHIPYARD ONLY" / "NO LAUNCH BAY"); a missing id
    // means no hint. Hinted rows dim + surface the reason on their status line but STAY clickable —
    // the Build/Research tabs still need those bases selectable. ShipLoadout recomputes the map on
    // hull selection + roster refresh; null clears everything.
    public void SetRowHints(IReadOnlyDictionary<ulong, string>? hints)
    {
        _rowHints = hints;
        ApplyRowHints();
    }

    private IReadOnlyDictionary<ulong, string>? _rowHints;

    private void ApplyRowHints()
    {
        foreach (var (id, _, _, _, _, row) in _rows)
            row.SetLaunchHint(_rowHints != null && _rowHints.TryGetValue(id, out string? h) ? h : null);
    }

    private void Select(ulong id)
    {
        SelectedBaseId = id;
        uint? sector = null;
        foreach (var (rid, rsector, title, sname, rtype, row) in _rows)
        {
            row.Selected = rid == id;
            if (rid == id)
            {
                sector = rsector;
                SelectedTitle = title;
                SelectedSectorName = sname;
                SelectedBaseType = rtype;
            }
        }
        _map.HighlightSector = sector;
        BaseSelected?.Invoke(id);
    }

    // Rebuild the CREWED SHIPS list. `ships` is every OTHER captain on our team advertising a crewable
    // hull (the caller filters us out); `mySeat` is the station we hold, if any, so the card that owns
    // it can mark itself. Store-driven only — a click raises an event and the next server frame paints
    // the result, so the list never shows a seat we merely asked for.
    public void SetCrewData(IReadOnlyList<CrewShipEntry> ships, (int CaptainId, byte Seat)? mySeat)
    {
        if (_crewBox == null)
            return;
        foreach (Node c in _crewBox.GetChildren())
            c.QueueFree();

        bool hasCrew = ships != null && ships.Count > 0;
        _crewSection.Visible = hasCrew;
        if (!hasCrew)
        {
            // Unreachable while the section is hidden on an empty roster, but a sidebar that is shown
            // with an empty list must still say so rather than render a bare header.
            _crewBox.AddChild(UiKit.MakeLabel("NO CREWED SHIPS", UiKit.TextStyle.Data, DesignTokens.TextDim));
            return;
        }

        foreach (CrewShipEntry e in ships!)
        {
            CrewShipEntry entry = e;
            var card = new CrewCard();
            card.Configure(entry, mySeat);
            card.JoinRequested += seat =>
            {
                if (SeatClickAllowed())
                    SeatJoinRequested?.Invoke(entry.CaptainId, seat);
            };
            card.LeaveRequested += () =>
            {
                if (SeatClickAllowed())
                    SeatLeaveRequested?.Invoke();
            };
            _crewBox.AddChild(card);
        }
    }

    private bool SeatClickAllowed()
    {
        ulong now = Time.GetTicksMsec();
        if (now < _seatClickReadyMs)
            return false;
        _seatClickReadyMs = now + 500;
        return true;
    }

    // One teammate's crewable ship: the hull header (glyph tile, name, CLASS · CAPT, manned count)
    // over one row per turret station. Rebuilt whole on every roster change — it's a handful of
    // controls and the seat state it paints is entirely server-owned.
    private sealed partial class CrewCard : PanelContainer
    {
        // The sidebar is a fixed 340px, so the fixed columns are kept tight: everything they don't
        // claim goes to the gun name, which is the only label worth reading in full.
        private const int SeatIdWidth = 44;
        private const int TagWidth = 74;

        public event Action<byte>? JoinRequested;
        public event Action? LeaveRequested;

        public void Configure(in CrewShipEntry e, (int CaptainId, byte Seat)? mySeat)
        {
            foreach (Node c in GetChildren())
                c.QueueFree();

            bool mineShip = mySeat is { } m0 && m0.CaptainId == e.CaptainId;
            bool seatedSomewhere = mySeat != null;
            bool docked = e.ShipId == 0;
            int manned = 0;
            foreach (CrewSeatEntry s in e.Seats)
                if (!s.IsOpen)
                    manned++;

            // The 2px left bar is the "this is the ship I'm on" marker (StyleBoxFlat carries ONE border
            // colour, so the whole hairline takes the accent at low alpha — the BaseRow idiom).
            var sb = new StyleBoxFlat
            {
                BgColor = DesignTokens.PanelFill,
                BorderColor = mineShip ? new Color(DesignTokens.TeamAccent, 0.55f) : DesignTokens.BorderLo,
                AntiAliasing = false,
            };
            sb.SetCornerRadiusAll(0);
            sb.SetBorderWidthAll(1);
            sb.BorderWidthLeft = 2;
            sb.SetContentMarginAll(12);
            sb.ContentMarginTop = sb.ContentMarginBottom = 13;
            AddThemeStyleboxOverride("panel", sb);

            var col = new VBoxContainer();
            col.AddThemeConstantOverride("separation", 9);
            AddChild(col);

            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 11);
            col.AddChild(head);
            head.AddChild(GlyphTile(e.Glyph, mineShip));

            var texts = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            texts.AddThemeConstantOverride("separation", 1);
            // Ships carry no authored NAME in this game (the design mock's "BLU-FORGE" is flavor), so
            // the HULL is the card's title and the subline names its captain — repeating the class in
            // the subline the way the mock does would just be noise.
            var name = UiKit.MakeLabel(e.ClassName, UiKit.TextStyle.Body);
            name.AddThemeFontOverride("font", UiFonts.SairaSemi);
            var sub = UiKit.MakeLabel($"CAPT {e.CaptainName}", UiKit.TextStyle.Data, DesignTokens.Text2);
            sub.AddThemeFontSizeOverride("font_size", 10);
            sub.ClipText = true;
            sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            texts.AddChild(name);
            texts.AddChild(sub);
            head.AddChild(texts);

            // In flight = no longer joinable (boarding is docked-only), so the count says that instead.
            string countText = docked ? $"{manned}/{e.Seats.Count} MANNED" : "IN FLIGHT";
            Color countCol =
                !docked ? DesignTokens.Warn
                : mineShip ? DesignTokens.TeamAccent
                : manned >= e.Seats.Count ? DesignTokens.Warn
                : DesignTokens.Data;
            var count = UiKit.MakeLabel(countText, UiKit.TextStyle.Data, countCol);
            count.AddThemeFontSizeOverride("font_size", 10);
            count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            head.AddChild(count);

            col.AddChild(new ColorRect { Color = DesignTokens.BorderLo, CustomMinimumSize = new Vector2(0, 1) });

            var seats = new VBoxContainer();
            seats.AddThemeConstantOverride("separation", 6);
            col.AddChild(seats);
            foreach (CrewSeatEntry s in e.Seats)
                seats.AddChild(
                    SeatRow(s, mySeat is { } m && m.CaptainId == e.CaptainId && m.Seat == s.Seat, seatedSomewhere, docked)
                );
        }

        private static Control GlyphTile(string glyph, bool mine)
        {
            var tile = new Label
            {
                Text = string.IsNullOrEmpty(glyph) ? "◇" : glyph,
                CustomMinimumSize = new Vector2(34, 34),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            Color c = mine ? DesignTokens.TeamAccent : DesignTokens.Text2;
            tile.AddThemeFontOverride("font", UiFonts.Mono);
            tile.AddThemeFontSizeOverride("font_size", 16);
            tile.AddThemeColorOverride("font_color", c);
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(c, mine ? 0.10f : 0.05f),
                BorderColor = new Color(c, mine ? 0.6f : 0.3f),
                AntiAliasing = false,
            };
            sb.SetCornerRadiusAll(0);
            sb.SetBorderWidthAll(1);
            tile.AddThemeStyleboxOverride("normal", sb);
            return tile;
        }

        // One station: pip + seat id + gun + the action tag. JOIN is offered only for an open seat on
        // a DOCKED ship while we hold no seat at all — every other case is a read-only tag.
        private Control SeatRow(in CrewSeatEntry s, bool mine, bool seatedSomewhere, bool docked)
        {
            bool claimable = s.IsOpen && docked && !seatedSomewhere;
            var panel = new PanelContainer();
            var sb = new StyleBoxFlat
            {
                BgColor = DesignTokens.PanelFill,
                BorderColor =
                    mine ? new Color(DesignTokens.TeamAccent, 0.4f)
                    : claimable ? new Color(DesignTokens.Ok, 0.25f)
                    : new Color(DesignTokens.BorderLo, 0.1f),
                AntiAliasing = false,
            };
            sb.SetCornerRadiusAll(0);
            sb.SetBorderWidthAll(1);
            sb.SetContentMarginAll(8);
            sb.ContentMarginTop = sb.ContentMarginBottom = 6;
            panel.AddThemeStyleboxOverride("panel", sb);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            panel.AddChild(row);

            Color pipCol =
                mine ? DesignTokens.TeamAccent
                : s.IsOpen ? DesignTokens.TextDim
                : DesignTokens.Text2;
            row.AddChild(RosterCells.Diamond(pipCol, hollow: s.IsOpen && !mine));

            var id = UiKit.MakeLabel(s.SeatId, UiKit.TextStyle.Data, DesignTokens.Data);
            id.AddThemeFontSizeOverride("font_size", 10);
            id.CustomMinimumSize = new Vector2(SeatIdWidth, 0);
            id.VerticalAlignment = VerticalAlignment.Center;
            row.AddChild(id);

            var gun = UiKit.MakeLabel(
                s.GunName,
                UiKit.TextStyle.Body,
                !s.IsOpen || mine ? DesignTokens.TextHi : DesignTokens.Text2
            );
            gun.AddThemeFontSizeOverride("font_size", 12);
            gun.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            gun.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            gun.ClipText = true;
            gun.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            row.AddChild(gun);

            row.AddChild(SeatTag(s, mine, claimable));
            return panel;
        }

        private Control SeatTag(in CrewSeatEntry s, bool mine, bool claimable)
        {
            if (mine)
            {
                var leave = UiKit.MakeButton("✕ LEAVE", () => LeaveRequested?.Invoke(), ButtonVariant.Ghost);
                leave.AccentOverride = DesignTokens.Danger;
                leave.CustomMinimumSize = new Vector2(TagWidth, 26);
                return leave;
            }
            if (claimable)
            {
                byte seat = s.Seat;
                var join = UiKit.MakeButton("＋ JOIN", () => JoinRequested?.Invoke(seat), ButtonVariant.Ghost);
                join.AccentOverride = DesignTokens.Ok;
                join.CustomMinimumSize = new Vector2(TagWidth, 26);
                return join;
            }
            var tag = UiKit.MakeLabel(
                s.IsOpen ? "OPEN" : s.GunnerName,
                UiKit.TextStyle.Data,
                s.IsOpen ? DesignTokens.TextDim : DesignTokens.Data
            );
            tag.AddThemeFontSizeOverride("font_size", 9);
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            tag.CustomMinimumSize = new Vector2(TagWidth, 0);
            return tag;
        }
    }

    // A selectable friendly-base row: glyph tile + title + sector line + status line, framed in the
    // LoadoutSlot idiom (cyan accent border, brighter tint when selected). Cyan is chrome here.
    private sealed partial class BaseRow : PanelContainer
    {
        private Label _title = null!;
        private Label _sector = null!;
        private Label _status = null!;
        private Label _research = null!;
        private ProgressUnderlay _bar = null!;
        private bool _selected;

        public event Action? Pressed;

        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                Restyle();
            }
        }

        public override void _Ready() => EnsureBuilt();

        private void EnsureBuilt()
        {
            if (_title != null)
                return;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            AddChild(row);

            // Glyph tile — ◰ marks a garrison.
            var tile = new Label
            {
                Text = "◰",
                CustomMinimumSize = new Vector2(34, 34),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tile.AddThemeFontOverride("font", UiFonts.Mono);
            tile.AddThemeFontSizeOverride("font_size", 18);
            tile.AddThemeColorOverride("font_color", DesignTokens.TeamAccent);
            var tileSb = new StyleBoxFlat
            {
                BgColor = new Color(DesignTokens.TeamAccentBase, 0.10f),
                BorderColor = new Color(DesignTokens.TeamAccentBase, 0.4f),
                AntiAliasing = false,
            };
            tileSb.SetCornerRadiusAll(0);
            tileSb.SetBorderWidthAll(1);
            tile.AddThemeStyleboxOverride("normal", tileSb);
            row.AddChild(tile);

            var texts = new VBoxContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            texts.AddThemeConstantOverride("separation", 1);
            _title = UiKit.MakeLabel("", UiKit.TextStyle.Body);
            _title.AddThemeFontOverride("font", UiFonts.SairaSemi);
            _sector = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
            _sector.AddThemeFontSizeOverride("font_size", 10);
            _status = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Ok);
            _status.AddThemeFontSizeOverride("font_size", 10);
            _research = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Warn);
            _research.AddThemeFontSizeOverride("font_size", 10);
            _research.ClipText = true;
            _research.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            _research.Visible = false;
            _bar = new ProgressUnderlay
            {
                ShowTrack = true,
                CustomMinimumSize = new Vector2(0, 3),
                Visible = false,
            };
            texts.AddChild(_title);
            texts.AddChild(_sector);
            texts.AddChild(_status);
            texts.AddChild(_research);
            texts.AddChild(_bar);
            row.AddChild(texts);

            Restyle();
        }

        // Live research line under the status row: null hides it; otherwise "RESEARCHING …"/"ON DECK".
        public void SetResearch(string? text, Color color, float progress, bool showBar)
        {
            EnsureBuilt();
            bool has = !string.IsNullOrEmpty(text);
            _research.Visible = has;
            _bar.Visible = has && showBar;
            if (!has)
                return;
            _research.Text = text;
            _research.AddThemeColorOverride("font_color", color);
            if (showBar)
            {
                _bar.Progress = progress;
                _bar.QueueRedraw();
            }
        }

        public void Configure(string title, string sectorName, bool alive)
        {
            EnsureBuilt();
            _title.Text = title;
            _sector.Text = sectorName;
            _alive = alive;
            ApplyStatus();
        }

        // Launch hint (2026-07-21 launch-station-classes): a non-null reason dims the row and
        // takes over the status line ("SHIPYARD ONLY" / "NO LAUNCH BAY"); null restores the
        // ACTIVE/DESTROYED readout. The row stays clickable either way.
        public void SetLaunchHint(string? hint)
        {
            EnsureBuilt();
            _launchHint = string.IsNullOrEmpty(hint) ? null : hint;
            Modulate = _launchHint is null ? Colors.White : new Color(1, 1, 1, 0.55f);
            ApplyStatus();
        }

        private bool _alive;
        private string? _launchHint;

        private void ApplyStatus()
        {
            if (_launchHint is string hint)
            {
                _status.Text = hint;
                _status.AddThemeColorOverride("font_color", DesignTokens.Warn);
            }
            else
            {
                _status.Text = _alive ? "ACTIVE" : "DESTROYED";
                _status.AddThemeColorOverride("font_color", _alive ? DesignTokens.Ok : DesignTokens.Danger);
            }
        }

        private void Restyle()
        {
            var sb = new StyleBoxFlat
            {
                BgColor = new Color(DesignTokens.TeamAccentBase, _selected ? 0.18f : 0.06f),
                BorderColor = new Color(DesignTokens.TeamAccentBase, _selected ? 1f : 0.35f),
                AntiAliasing = false,
            };
            sb.SetCornerRadiusAll(0);
            sb.SetBorderWidthAll(1);
            sb.BorderWidthLeft = 3;
            sb.SetContentMarginAll(10);
            AddThemeStyleboxOverride("panel", sb);
        }

        public override void _GuiInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
                Pressed?.Invoke();
                AcceptEvent();
            }
        }
    }
}
