using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  BuildTab.cs — docked-screen BUILD tab (Phase D, placeholder catalog)
//
//  Layout mirrors the RESEARCH tab: [ center content (expand) | shared TechDetailPanel 400px ]. The
//  shared CommandSidebar sits left of this in ShipLoadout; its selected base feeds SetBase.
//
//  The center is a responsive card GRID of the station CATALOG (DefRegistry.AllStationCatalog()
//  entries with BaseTypeId == -1 — future structures with no runtime base projection). Status is
//  derived CLIENT-SIDE from streamed data only (owned techs/caps): available = cyan card, locked =
//  dim. Nothing is baked: an empty catalog shows the awaiting-uplink guard.
//
//  Construction is NOT wired anywhere — the action footer is ALWAYS disabled ("CONSTRUCTORS
//  OFFLINE"); building lands with the base-building update. No MsgSpawn/MsgResearch is sent here.
// =====================================================================
public partial class BuildTab : Control
{
    private DefRegistry? _defs;
    private WorldRenderer? _world;
    private GameNetClient? _net;

    private ulong _baseId;
    private string _baseTitle = "";
    private string _baseSector = "";

    private string? _selectedId;
    private readonly List<(string id, StationCard card)> _cards = new();

    private bool _built;
    private double _refreshTimer;
    private long _statusSig = long.MinValue;
    private int _catalogCount = -1;

    private Control _guard = null!;
    private Control _mainBody = null!;
    private Label _headerLabel = null!;
    private HFlowContainer _grid = null!;
    private TechDetailPanel _detail = null!;

    // Fleet-constructor roster (producing + launched drones), rebuilt when the set/states change.
    private Control _rosterSection = null!;
    private VBoxContainer _roster = null!;
    private long _rosterSig = long.MinValue;

    public void Init(DefRegistry defs, WorldRenderer world, GameNetClient? net = null)
    {
        _defs = defs;
        _world = world;
        _net = net;
    }

    // Called by ShipLoadout when the CommandSidebar selection changes (mirrors ResearchTab.SetBase).
    public void SetBase(ulong id, string title, string sector)
    {
        _baseId = id;
        _baseTitle = title;
        _baseSector = sector;
        if (_built)
        {
            UpdateHeader();
            RefreshDetail();
        }
    }

    private byte Team => _world?.LocalTeam ?? 0;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        UiFonts.EnsureLoaded();
        MouseFilter = MouseFilterEnum.Stop; // opaque body

        BuildGuard();
        BuildMain();
        _built = true;
        _mainBody.Visible = false;
        _guard.Visible = true;
    }

    // ---- guard -------------------------------------------------------------

    private void BuildGuard()
    {
        _guard = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _guard.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _guard.AddChild(center);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        col.AddThemeConstantOverride("separation", 8);
        var glyph = UiKit.MakeLabel("⬡", UiKit.TextStyle.Display, DesignTokens.TextDim);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(glyph);
        var msg = UiKit.MakeLabel("CONSTRUCTION CATALOG OFFLINE — AWAITING SERVER CATALOG", UiKit.TextStyle.Data, DesignTokens.TextDim);
        msg.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(msg);
        center.AddChild(col);
        AddChild(_guard);
    }

    // ---- main --------------------------------------------------------------

    private void BuildMain()
    {
        _mainBody = new HBoxContainer();
        _mainBody.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _mainBody.AddThemeConstantOverride("separation", 0);
        AddChild(_mainBody);

        _mainBody.AddChild(BuildCenter());

        _detail = new TechDetailPanel();
        _detail.SetSchematic("⬡", "// STRUCTURE");
        _detail.PrimaryPressed += OnBuildPressed;
        _mainBody.AddChild(_detail);
    }

    private Control BuildCenter()
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 28);
        pad.AddThemeConstantOverride("margin_right", 24);
        pad.AddThemeConstantOverride("margin_top", 22);
        pad.AddThemeConstantOverride("margin_bottom", 24);
        scroll.AddChild(pad);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 14);
        pad.AddChild(col);

        _headerLabel = UiKit.MakeLabel("CONSTRUCTION CATALOG", UiKit.TextStyle.Title);
        col.AddChild(UiKit.MakeLabel("▶ CONSTRUCTION DIRECTORATE", UiKit.TextStyle.Label, DesignTokens.TextDim));
        col.AddChild(_headerLabel);

        col.AddChild(new DiamondDivider());

        // Fleet-constructor roster: producing drones (progress + cancel) and launched drones (status).
        // Hidden entirely when the team has no constructors.
        _rosterSection = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false };
        _rosterSection.AddThemeConstantOverride("separation", 8);
        _rosterSection.AddChild(UiKit.MakeLabel("▶ FLEET CONSTRUCTORS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _roster = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _roster.AddThemeConstantOverride("separation", 6);
        _rosterSection.AddChild(_roster);
        _rosterSection.AddChild(new DiamondDivider());
        col.AddChild(_rosterSection);

        // Responsive card grid — HFlowContainer wraps ~232px cells (StationCard sets its min width).
        _grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 14);
        _grid.AddThemeConstantOverride("v_separation", 14);
        col.AddChild(_grid);

        return scroll;
    }

    // ---- catalog-only entries ---------------------------------------------

    // Every buildable structure: the runtime forward bases (BaseTypeId >= 1, e.g. the outpost — really
    // constructible) plus the catalog-only placeholders (-1). The garrison (type 0) is the starting
    // base, never in the Build catalog.
    // The garrison (type 0) is the starting base, never in the Build catalog. Upgrade-tier runtime
    // bases (garrison-str/supremacy-adv/shipyard-dry — a runtime base-type-id but NO build-on-rock-class)
    // are reached only via research (their upgrade dev), never built directly, so they are excluded too.
    private List<StationCatalogDef> Catalog() =>
        _defs == null
            ? new()
            : _defs.AllStationCatalog()
                .Where(s => s.BaseTypeId != 0 && !(s.BaseTypeId >= 1 && s.BuildRockClass == 255))
                .ToList();

    // A structure that actually builds today (has a runtime base projection). Placeholders (-1) are
    // display-only until their type is authored.
    private static bool IsConstructible(StationCatalogDef s) => s.BaseTypeId >= 1;

    public override void _Process(double delta)
    {
        if (_defs == null || _world == null)
            return;

        UpdateRoster();

        List<StationCatalogDef> catalog = Catalog();
        bool have = catalog.Count > 0;
        _guard.Visible = !have;
        _mainBody.Visible = have;
        if (!have)
            return;

        _refreshTimer -= delta;
        long sig = ComputeStatusSig(catalog);
        bool catalogChanged = catalog.Count != _catalogCount;
        if (_refreshTimer <= 0 || sig != _statusSig || catalogChanged)
        {
            _refreshTimer = 0.25;
            _catalogCount = catalog.Count;
            bool structural = sig != _statusSig || catalogChanged;
            _statusSig = sig;
            if (structural)
                RebuildGrid(catalog);
            UpdateHeader();
            RefreshDetail();
        }
    }

    // Order-independent hash of everything that flips a card's status (owned techs/caps + count).
    private long ComputeStatusSig(List<StationCatalogDef> catalog)
    {
        byte team = Team;
        long sig = team + 1L + catalog.Count * 131L;
        foreach (ushort t in _world!.TeamOwnedTechs(team))
            sig ^= (t + 1) * 2654435761L;
        // Fold capability ownership: caps are a small closed enum, poll each catalog entry's needs.
        foreach (StationCatalogDef s in catalog)
            foreach (byte c in s.RequiredCaps)
                if (_world.TeamOwnsCap(team, c))
                    sig ^= (c + 17L) * 40503L;
        return sig;
    }

    private void UpdateHeader() =>
        _headerLabel.Text = string.IsNullOrEmpty(_baseTitle)
            ? "CONSTRUCTION CATALOG"
            : $"CONSTRUCTION CATALOG · {_baseTitle.ToUpperInvariant()}";

    // ---- fleet-constructor roster -----------------------------------------

    // Rebuild the roster only when the constructor SET or their STATES change (progress within a state
    // animates continuously in each row's _Process — no rebuild). Hidden when the team has none.
    private void UpdateRoster()
    {
        var states = _world!.ConstructorStates();
        long sig = states.Count * 2654435761L;
        foreach (var c in states)
            sig ^= (long)(c.Id * 131u + c.State + 1u) * 40503L;
        if (sig == _rosterSig)
            return;
        _rosterSig = sig;
        RebuildRoster(states);
    }

    private void RebuildRoster(IReadOnlyList<WorldRenderer.ConstructorStatus> states)
    {
        foreach (Node c in _roster.GetChildren())
            c.QueueFree();
        _rosterSection.Visible = states.Count > 0;
        if (states.Count == 0)
            return;

        bool commander = _net?.IsCommander ?? false;
        foreach (var c in states)
        {
            string name = StationName(c.StationTypeId);
            var row = new ActiveBanner { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            switch (c.State)
            {
                case 0: // Producing
                {
                    ulong id = c.Id;
                    Action? cancel = commander ? () => { _net?.SendCancelConstructor(id); SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick); } : null;
                    row.ConfigureTimed(_world!, DesignTokens.Warn, $"◷ PRODUCING · {name} CONSTRUCTOR", c.StartTick, c.DurationTicks, cancel, "✕ CANCEL");
                    break;
                }
                case 1: // Idle
                    row.ConfigureNote(DesignTokens.Data, $"◌ {name} CONSTRUCTOR · IDLE — F3-order it to an asteroid");
                    break;
                case 2: // ToRock
                    row.ConfigureNote(DesignTokens.TeamAccent, $"▸ {name} CONSTRUCTOR · EN ROUTE TO BUILD SITE");
                    break;
                case 3: // MoveTo
                {
                    string sec = _world!.SectorName((uint)c.TargetId);
                    if (string.IsNullOrEmpty(sec)) sec = $"SECTOR {c.TargetId}";
                    row.ConfigureNote(DesignTokens.TeamAccent, $"▸ {name} CONSTRUCTOR · MOVING TO {sec.ToUpperInvariant()}");
                    break;
                }
                case 4: // Aligning (timed: the station's align-time-seconds)
                    row.ConfigureTimed(_world!, DesignTokens.TeamAccent, $"◈ {name} · ALIGNING", c.StartTick, c.DurationTicks, null, "");
                    break;
                case 5: // Approaching (v38: distance-gated creep to surface contact — untimed)
                    row.ConfigureNote(DesignTokens.TeamAccent, $"◈ {name} · APPROACHING SURFACE");
                    break;
                case 6: // Sinking (v38: distance-gated embed creep — untimed)
                    row.ConfigureNote(DesignTokens.TeamAccent, $"◈ {name} · EMBEDDING");
                    break;
                default: // Building (timed: the station's build-time-seconds)
                    row.ConfigureTimed(_world!, DesignTokens.Warn, $"◈ BUILDING {name}", c.StartTick, c.DurationTicks, null, "");
                    break;
            }
            _roster.AddChild(row);
        }
    }

    // Display name for a base type (from the station catalog), e.g. "OUTPOST".
    private string StationName(byte baseTypeId) =>
        (_defs?.AllStationCatalog().FirstOrDefault(s => s.BaseTypeId == baseTypeId)?.Name ?? "STRUCTURE").ToUpperInvariant();

    // ---- status resolution (client-side, streamed data only) --------------

    private bool IsAvailable(StationCatalogDef s)
    {
        if (_world == null)
            return false;
        byte team = Team;
        bool obsoleted = s.ObsoletedByTechIdx.Any(t => _world.TeamOwnsTech(team, t));
        return !obsoleted
            && s.RequiredTechIdx.All(t => _world.TeamOwnsTech(team, t))
            && s.RequiredCaps.All(c => _world.TeamOwnsCap(team, c));
    }

    // ---- grid --------------------------------------------------------------

    private void RebuildGrid(List<StationCatalogDef> catalog)
    {
        foreach (Node c in _grid.GetChildren())
            c.QueueFree();
        _cards.Clear();

        foreach (StationCatalogDef s in catalog)
        {
            string id = s.Id;
            var card = new StationCard();
            card.Configure(
                GlyphFor(s.StationClass), s.Name.ToUpperInvariant(), ClassName(s.StationClass),
                s.Description, TechDetailPanel.PriceText(s.Price), TechDetailPanel.Mmss(s.BuildTimeSeconds),
                IsAvailable(s), _selectedId == id);
            card.Pressed += () => SelectStation(id);
            _grid.AddChild(card);
            _cards.Add((id, card));
        }
    }

    private void SelectStation(string id)
    {
        _selectedId = id;
        foreach (var (cid, card) in _cards)
            card.SetSelected(cid == id);
        RefreshDetail();
    }

    // ---- detail panel ------------------------------------------------------

    private void RefreshDetail()
    {
        if (_defs == null || _world == null)
            return;

        StationCatalogDef? sel = _selectedId != null
            ? Catalog().FirstOrDefault(s => s.Id == _selectedId)
            : null;

        if (sel == null)
        {
            _detail.SetSchematic("⬡", "// STRUCTURE");
            _detail.SetTitle("SELECT A STRUCTURE");
            _detail.SetStatus("—", StatusPill.Kind.Neutral);
            _detail.SetDescription("Choose a structure from the catalog to review its cost, prerequisites, and what it unlocks.");
            _detail.SetMeta("—", "—", "—");
            _detail.ClearPrereqs();
            _detail.ClearUnlocks();
            _detail.SetFooter(true, "— SELECT A STRUCTURE", ButtonVariant.Secondary, null,
                "Choose a structure to commission a constructor.");
            return;
        }

        bool available = IsAvailable(sel);
        _detail.SetSchematic(GlyphFor(sel.StationClass), "// STRUCTURE");
        _detail.SetTitle(sel.Name.ToUpperInvariant());
        _detail.SetStatus(available ? "◈ AVAILABLE" : "⊘ LOCKED",
            available ? StatusPill.Kind.Accent : StatusPill.Kind.Neutral);
        _detail.SetDescription(string.IsNullOrEmpty(sel.Description) ? "No briefing on file." : sel.Description);
        _detail.SetMeta(TechDetailPanel.PriceText(sel.Price), TechDetailPanel.Mmss(sel.BuildTimeSeconds),
            string.IsNullOrEmpty(_baseTitle) ? "—" : _baseTitle);

        BuildPrereqs(sel);
        BuildUnlocks(sel);
        UpdateFooter(sel, available);
    }

    // The action footer. A constructible structure that's available lets a COMMANDER buy a constructor
    // (which then F3-orders to a compatible asteroid); non-commanders get a disabled affordance; locked
    // structures show why; placeholder types (BaseTypeId -1) stay "offline" until authored.
    private void UpdateFooter(StationCatalogDef sel, bool available)
    {
        if (!IsConstructible(sel))
        {
            _detail.SetFooter(true, "⊘ NOT YET BUILDABLE", ButtonVariant.Secondary, null,
                "This structure type arrives in a later update.");
            return;
        }
        if (!available)
        {
            _detail.SetFooter(true, "⊘ LOCKED", ButtonVariant.Secondary, null,
                "Research or build the prerequisites to unlock this structure.");
            return;
        }
        bool commander = _net?.IsCommander ?? false;
        if (!commander)
        {
            _detail.SetFooter(true, "⊘ COMMANDER AUTHORIZATION REQUIRED", ButtonVariant.Secondary, null,
                "Only the team commander can commission a constructor.");
            return;
        }
        string rock = sel.BuildRockClass == 255 ? "asteroid" : $"{RockClassName(sel.BuildRockClass)} asteroid";
        _detail.SetFooter(false, "◈ BUILD CONSTRUCTOR", ButtonVariant.Primary, null,
            $"Launches a constructor from your garrison — then F3-order it to a {rock}.");
    }

    private static string RockClassName(byte rc) => rc switch
    {
        0 => "carbonaceous", 1 => "silicon", 2 => "uranium", 3 => "helium-3", 4 => "regolith", _ => "any",
    };

    // BUILD button: commander commissions a constructor for the selected structure, launching from the
    // sidebar-selected base (0 = the server's default garrison). The server validates + charges.
    private void OnBuildPressed()
    {
        if (_net is null || _selectedId is null)
            return;
        StationCatalogDef? sel = Catalog().FirstOrDefault(s => s.Id == _selectedId);
        if (sel is null || !IsConstructible(sel) || !IsAvailable(sel) || !_net.IsCommander)
            return;
        _net.SendBuildConstructor((byte)sel.BaseTypeId, _baseId);
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
    }

    private void BuildPrereqs(StationCatalogDef s)
    {
        byte team = Team;
        var rows = new List<(string, bool)>();
        foreach (ushort t in s.RequiredTechIdx)
            rows.Add((_defs!.GetTech(t)?.Name ?? $"TECH {t}", _world!.TeamOwnsTech(team, t)));
        foreach (byte c in s.RequiredCaps)
            rows.Add((TechDetailPanel.CapName(c), _world!.TeamOwnsCap(team, c)));
        _detail.SetPrereqs(rows); // empty -> "No prerequisites"
    }

    private void BuildUnlocks(StationCatalogDef s)
    {
        var names = new List<string>();
        // Capabilities this structure grants (friendly names).
        foreach (byte c in s.GrantedCaps)
            names.Add(TechDetailPanel.CapName(c));
        // Other catalog structures whose prerequisites this station's grants would satisfy.
        var gTech = new HashSet<ushort>(s.GrantedTechIdx);
        var gCap = new HashSet<byte>(s.GrantedCaps);
        foreach (StationCatalogDef other in Catalog())
            if (other.Id != s.Id
                && (other.RequiredTechIdx.Any(gTech.Contains) || other.RequiredCaps.Any(gCap.Contains)))
                names.Add(other.Name);
        _detail.SetUnlocks(names); // dedupe + "// nothing new" live in the panel
    }

    // ---- demo hook (used by --hangar-demo harness) ------------------------

    // First available catalog card's center (falls back to the first card), for the screenshot demo.
    public Vector2? DemoFirstCardCenter()
    {
        if (_defs == null)
            return null;
        foreach (var (id, card) in _cards)
            if (Catalog().FirstOrDefault(s => s.Id == id) is StationCatalogDef s && IsAvailable(s))
                return card.GetGlobalRect().GetCenter();
        return _cards.Count > 0 ? _cards[0].card.GetGlobalRect().GetCenter() : null;
    }

    // ---- presentation maps -------------------------------------------------

    // Glyph per StationClass byte (Starbase=0 … Electronics=7). A small static map — cosmetic only.
    private static string GlyphFor(byte stationClass) => stationClass switch
    {
        0 => "✦", // Starbase
        1 => "◰", // Garrison
        2 => "⬡", // Shipyard
        3 => "◎", // Ripcord
        4 => "◈", // Mining / Refinery
        5 => "❖", // Research
        6 => "⬢", // Ordnance
        7 => "◇", // Electronics
        _ => "⬡",
    };

    private static string ClassName(byte stationClass) => stationClass switch
    {
        0 => "STARBASE",
        1 => "GARRISON",
        2 => "SHIPYARD",
        3 => "RIPCORD",
        4 => "MINING",
        5 => "RESEARCH",
        6 => "ORDNANCE",
        7 => "ELECTRONICS",
        _ => "STRUCTURE",
    };
}

// =====================================================================
//  StationCard — one catalog entry in the BUILD grid.
//
//  40px glyph tile + status label; name; "STRUCTURE · <class>" kind line; a two-line description
//  snippet; footer "₡ price" (amber) + "BUILD mm:ss". Available = cyan border; locked = dim 0.62 +
//  "⊘ LOCKED"; selected = brighter cyan fill (fills the detail panel). Cyan is chrome here (the
//  selection cursor / availability), never team identity.
// =====================================================================
internal partial class StationCard : PanelContainer
{
    public event Action? Pressed;

    private Label _glyph = null!;
    private Label _status = null!;
    private Label _name = null!;
    private Label _kind = null!;
    private Label _desc = null!;
    private Label _price = null!;
    private Label _build = null!;
    private bool _available;
    private bool _selected;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_name != null)
            return;
        CustomMinimumSize = new Vector2(232, 0);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 7);
        AddChild(col);

        // Top row: glyph tile + status label.
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        _glyph = new Label
        {
            Text = "⬡",
            CustomMinimumSize = new Vector2(40, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _glyph.AddThemeFontOverride("font", UiFonts.Mono);
        _glyph.AddThemeFontSizeOverride("font_size", 22);
        _glyph.AddThemeColorOverride("font_color", DesignTokens.TeamAccent);
        var tileSb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccentBase, 0.10f), BorderColor = new Color(DesignTokens.TeamAccentBase, 0.4f), AntiAliasing = false };
        tileSb.SetCornerRadiusAll(0);
        tileSb.SetBorderWidthAll(1);
        _glyph.AddThemeStyleboxOverride("normal", tileSb);
        top.AddChild(_glyph);
        _status = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TeamAccent);
        _status.AddThemeFontSizeOverride("font_size", 10);
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        top.AddChild(_status);
        col.AddChild(top);

        _name = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextHi);
        _name.AddThemeFontOverride("font", UiFonts.SairaSemi);
        _name.AddThemeFontSizeOverride("font_size", 16);
        _name.ClipText = true;
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        col.AddChild(_name);

        _kind = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _kind.AddThemeFontSizeOverride("font_size", 10);
        col.AddChild(_kind);

        _desc = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Data);
        _desc.AddThemeFontSizeOverride("font_size", 11);
        _desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _desc.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _desc.CustomMinimumSize = new Vector2(0, 30);
        _desc.SizeFlagsVertical = SizeFlags.ExpandFill;
        col.AddChild(_desc);

        var footer = new HBoxContainer();
        _price = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Warn);
        _price.AddThemeFontSizeOverride("font_size", 13);
        _price.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _build = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _build.AddThemeFontSizeOverride("font_size", 11);
        footer.AddChild(_price);
        footer.AddChild(_build);
        col.AddChild(footer);

        Restyle();
    }

    public void Configure(string glyph, string name, string className, string desc, string priceText, string buildText, bool available, bool selected)
    {
        EnsureBuilt();
        _glyph.Text = glyph;
        _name.Text = name;
        _kind.Text = $"STRUCTURE · {className}";
        _desc.Text = desc;
        _price.Text = priceText;
        _build.Text = $"BUILD {buildText}";
        _available = available;
        _selected = selected;
        _status.Text = available ? "◈ AVAILABLE" : "⊘ LOCKED";
        _status.AddThemeColorOverride("font_color", available ? DesignTokens.TeamAccent : DesignTokens.TextDim);
        Restyle();
    }

    // Showcase-only: render a card with no live catalog.
    public void ConfigureMock(string glyph, string name, string className, string desc, string priceText, string buildText, bool available, bool selected = false) =>
        Configure(glyph, name, className, desc, priceText, buildText, available, selected);

    public void SetSelected(bool sel)
    {
        _selected = sel;
        Restyle();
    }

    private void Restyle()
    {
        var accent = DesignTokens.TeamAccentBase;
        var border = _selected
            ? new Color(accent, 1f)
            : _available ? new Color(accent, 0.4f) : DesignTokens.BorderLo;
        var sb = new StyleBoxFlat
        {
            BgColor = _selected ? new Color(accent, 0.16f) : new Color(DesignTokens.Panel, 0.85f),
            BorderColor = border,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(_selected ? 2 : 1);
        sb.SetContentMarginAll(12);
        AddThemeStyleboxOverride("panel", sb);
        Modulate = _available || _selected ? Colors.White : new Color(1, 1, 1, 0.62f);
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
