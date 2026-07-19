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
//  Data comes from WorldRenderer.KnownBases()/MapSectors/MapBaseTeams/MapAlephLinks, filtered to
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

        var basesPanel = new HairlinePanel { Title = "YOUR BASES", SizeFlagsVertical = SizeFlags.ExpandFill };
        col.AddChild(basesPanel);
        _rowsBox = new VBoxContainer();
        _rowsBox.AddThemeConstantOverride("separation", 7);
        basesPanel.AddChild(_rowsBox);

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
        foreach (var (id, sector, bteam, alive, typeId) in _world.KnownBases())
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
            var res = _world.ResearchAt(id);
            if (res is WorldRenderer.BaseResearch r && r.Active.Length > 0)
            {
                var a = r.Active[0];
                string name = _defs.GetDevelopment(a.DevIndex)?.Name.ToUpperInvariant() ?? $"DEV {a.DevIndex}";
                string mmss = TechDetailPanel.MmssRemaining(_world, a.StartTick, a.DurationTicks);
                string more = r.Active.Length > 1 ? $"  +{r.Active.Length - 1} more" : "";
                row.SetResearch($"◷ {name} · {mmss}{more}", DesignTokens.Warn,
                    _world.ResearchProgress(a.StartTick, a.DurationTicks), showBar: true);
            }
            else if (res is WorldRenderer.BaseResearch r2 && r2.OnDeck is ushort od)
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
            foreach (var (sec, bteam) in world.MapBaseTeams)
                if (sec == s.SectorId)
                    bases.Add(new SectorMapPreview.BaseMark(bteam));
            sectors.Add(new SectorMapPreview.SectorModel(
                s.SectorId, s.Radius, bases, new List<Vector2>(),
                string.IsNullOrEmpty(s.Name) ? null : s.Name, s.MapPosX, s.MapPosY, s.HasMapPos));
        }
        var links = new List<(uint A, uint B)>();
        foreach (var (sec, dest) in world.MapAlephLinks)
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
            var tileSb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccentBase, 0.10f), BorderColor = new Color(DesignTokens.TeamAccentBase, 0.4f), AntiAliasing = false };
            tileSb.SetCornerRadiusAll(0);
            tileSb.SetBorderWidthAll(1);
            tile.AddThemeStyleboxOverride("normal", tileSb);
            row.AddChild(tile);

            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
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
            _bar = new ProgressUnderlay { ShowTrack = true, CustomMinimumSize = new Vector2(0, 3), Visible = false };
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
            _status.Text = alive ? "ACTIVE" : "DESTROYED";
            _status.AddThemeColorOverride("font_color", alive ? DesignTokens.Ok : DesignTokens.Danger);
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
