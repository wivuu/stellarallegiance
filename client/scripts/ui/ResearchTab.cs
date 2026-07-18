using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  ResearchTab.cs — docked-screen RESEARCH tab (Phase C, live)
//
//  Layout: [ center content (expand) | right detail column 400px ]. The shared CommandSidebar
//  sits left of this in ShipLoadout; its selected base IS the research base (fed here via SetBase).
//
//  All state is derived CLIENT-SIDE from streamed data only (DefRegistry catalog + WorldRenderer
//  team-owned-techs / per-base research). Nothing is baked: with an empty catalog we show the
//  awaiting-uplink guard, exactly like the hangar's ship list. Commander-only actions send
//  MsgResearch ops (GameNetClient.SendResearch) and hold an optimistic PENDING state until the
//  next MsgResearchState / team-state frame moves the derived status.
// =====================================================================
public partial class ResearchTab : Control
{
    public enum Status
    {
        Done,
        InProgress,
        OnDeck,
        Available,
        Locked,
    }

    // Rail-line colour (plan spec rgba(120,190,255,0.26)); cyan when the parent is done.
    private static readonly Color RailColor = new(120f / 255f, 190f / 255f, 255f / 255f, 0.26f);

    private DefRegistry? _defs;
    private WorldRenderer? _world;
    private GameNetClient? _net;

    private ulong _baseId;
    private string _baseTitle = "";
    private string _baseSector = "";
    private byte _baseType; // BaseDef.BaseTypeId of the selected base — gates station-upgrade devs by from-type

    private ushort? _selectedDev;

    // Collapse state persists across rebuilds.
    private readonly HashSet<string> _collapsedGroups = new();
    private readonly HashSet<ushort> _collapsedNodes = new();

    // Optimistic PENDING after an op — cleared when the selected dev's derived status changes or 3s elapses.
    private ushort? _pendingDev;
    private Status _pendingStatus;
    private double _pendingUntilMsec;

    private bool _built;
    private double _refreshTimer;
    private long _statusSig = long.MinValue;
    private int _catalogCount = -1;

    // -- nodes / roots -------------------------------------------------------
    private Control _guard = null!;
    private Control _mainBody = null!;
    private Label _baseGlyph = null!;
    private Label _baseTitleLabel = null!;
    private Label _baseSectorLabel = null!;
    private VBoxContainer _bannersBox = null!;
    private HFlowContainer _clusters = null!;
    private readonly List<(ushort dev, NodeCard card)> _cards = new();

    // -- detail column (shared control) --------------------------------------
    private TechDetailPanel _detail = null!;

    public void Init(DefRegistry defs, WorldRenderer world, GameNetClient net)
    {
        _defs = defs;
        _world = world;
        _net = net;
    }

    // Called by ShipLoadout when the CommandSidebar selection changes (BaseSelected).
    public void SetBase(ulong id, string title, string sector, byte typeId)
    {
        _baseId = id;
        _baseTitle = title;
        _baseSector = sector;
        _baseType = typeId;
        if (_built)
        {
            UpdateBaseHeader();
            RebuildBanners();
            RefreshDetail();
        }
    }

    private byte Team => _world?.LocalTeam ?? _net?.MyTeam ?? 0;

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
        var glyph = UiKit.MakeLabel("◇", UiKit.TextStyle.Display, DesignTokens.TextDim);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(glyph);
        var msg = UiKit.MakeLabel("RESEARCH UPLINK OFFLINE — AWAITING SERVER CATALOG", UiKit.TextStyle.Data, DesignTokens.TextDim);
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
        _mainBody.AddChild(BuildDetail());
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

        // Selected-base header: glyph tile + title + sector.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 14);
        _baseGlyph = new Label
        {
            Text = "◰",
            CustomMinimumSize = new Vector2(46, 46),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _baseGlyph.AddThemeFontOverride("font", UiFonts.Mono);
        _baseGlyph.AddThemeFontSizeOverride("font_size", 24);
        _baseGlyph.AddThemeColorOverride("font_color", DesignTokens.TeamAccent);
        var tileSb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccentBase, 0.10f), BorderColor = new Color(DesignTokens.TeamAccentBase, 0.4f), AntiAliasing = false };
        tileSb.SetCornerRadiusAll(0);
        tileSb.SetBorderWidthAll(1);
        _baseGlyph.AddThemeStyleboxOverride("normal", tileSb);
        head.AddChild(_baseGlyph);
        var htxt = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        htxt.AddThemeConstantOverride("separation", 1);
        htxt.AddChild(UiKit.MakeLabel("▶ RESEARCH DIRECTORATE", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _baseTitleLabel = UiKit.MakeLabel("—", UiKit.TextStyle.Title);
        _baseSectorLabel = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        htxt.AddChild(_baseTitleLabel);
        htxt.AddChild(_baseSectorLabel);
        head.AddChild(htxt);
        col.AddChild(head);

        // Status banners for the selected base.
        _bannersBox = new VBoxContainer();
        _bannersBox.AddThemeConstantOverride("separation", 7);
        col.AddChild(_bannersBox);

        col.AddChild(new DiamondDivider());
        col.AddChild(UiKit.MakeLabel("▶ RESEARCH CANVAS", UiKit.TextStyle.Label, DesignTokens.TextDim));

        _clusters = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _clusters.AddThemeConstantOverride("h_separation", 20);
        _clusters.AddThemeConstantOverride("v_separation", 18);
        col.AddChild(_clusters);

        return scroll;
    }

    private Control BuildDetail()
    {
        // Shared right-hand detail column; this tab drives its research-specific footer states.
        _detail = new TechDetailPanel();
        _detail.PrimaryPressed += OnFooterPrimary;
        _detail.SecondaryPressed += OnFooterSecondary;
        return _detail;
    }

    // ---- per-frame + timed refresh ----------------------------------------

    public override void _Process(double delta)
    {
        if (_defs == null || _world == null)
            return;

        List<DevelopmentDef> devs = _defs.AllDevelopments().ToList();
        bool haveCatalog = devs.Count > 0;
        _guard.Visible = !haveCatalog;
        _mainBody.Visible = haveCatalog;
        if (!haveCatalog)
            return;

        _refreshTimer -= delta;
        long sig = ComputeStatusSig(devs);
        bool catalogChanged = devs.Count != _catalogCount;
        if (_refreshTimer <= 0 || sig != _statusSig || catalogChanged)
        {
            _refreshTimer = 0.25;
            _catalogCount = devs.Count;
            bool structural = sig != _statusSig || catalogChanged;
            _statusSig = sig;
            if (structural)
                RebuildClusters(devs);
            UpdateBaseHeader();
            RebuildBanners();
            RefreshDetail();
        }
    }

    // Order-independent hash of everything that can change a node's derived status.
    private long ComputeStatusSig(List<DevelopmentDef> devs)
    {
        byte team = Team;
        long sig = team + 1L + devs.Count * 131L;
        foreach (ushort t in _world!.TeamOwnedTechs(team))
            sig ^= (t + 1) * 2654435761L;
        foreach (var (id, r) in _world.AllResearch())
        {
            sig ^= unchecked((long)(id * 1000003UL));
            foreach (var a in r.Active)
                sig ^= (a.DevIndex + 7L) * 40503L;
            if (r.OnDeck is ushort od)
                sig ^= (od + 11L) * 22013L;
        }
        return sig;
    }

    private void UpdateBaseHeader()
    {
        _baseTitleLabel.Text = string.IsNullOrEmpty(_baseTitle) ? "NO BASE SELECTED" : _baseTitle;
        _baseSectorLabel.Text = _baseSector;
    }

    // ---- banners -----------------------------------------------------------

    private void RebuildBanners()
    {
        foreach (Node c in _bannersBox.GetChildren())
            c.QueueFree();
        if (_world == null || _defs == null)
            return;

        var res = _baseId != 0 ? _world.ResearchAt(_baseId) : null;
        bool commander = _net?.IsCommander ?? false;
        int active = res?.Active.Length ?? 0;
        bool onDeck = res?.OnDeck != null;

        if (res is WorldRenderer.BaseResearch r)
        {
            foreach (var a in r.Active)
            {
                string name = _defs.GetDevelopment(a.DevIndex)?.Name.ToUpperInvariant() ?? $"DEV {a.DevIndex}";
                var banner = new ActiveBanner();
                banner.ConfigureActive(_world, name, a.StartTick, a.DurationTicks,
                    commander ? () => Send(1, _baseId, a.DevIndex) : (Action?)null);
                _bannersBox.AddChild(banner);
            }
            if (r.OnDeck is ushort od)
            {
                string name = _defs.GetDevelopment(od)?.Name.ToUpperInvariant() ?? $"DEV {od}";
                var banner = new ActiveBanner();
                banner.ConfigureOnDeck(name, commander ? () => Send(2, _baseId, od) : (Action?)null);
                _bannersBox.AddChild(banner);
            }
        }

        if (active == 0 && !onDeck)
        {
            var idle = new ActiveBanner();
            idle.ConfigureIdle();
            _bannersBox.AddChild(idle);
        }
    }

    // ---- clusters + node tree ---------------------------------------------

    private sealed class TreeNode
    {
        public ushort Index;
        public DevelopmentDef Dev = null!;
        public readonly List<TreeNode> Children = new();
        public bool IsLast;
        public bool Done;
    }

    private void RebuildClusters(List<DevelopmentDef> devs)
    {
        foreach (Node c in _clusters.GetChildren())
            c.QueueFree();
        _cards.Clear();

        // Group by Group label (fallback bucket "RESEARCH").
        var groups = new List<(string name, List<(ushort idx, DevelopmentDef dev)> devs)>();
        var byName = new Dictionary<string, int>();
        for (ushort i = 0; i < devs.Count; i++)
        {
            string g = string.IsNullOrEmpty(devs[i].Group) ? "RESEARCH" : devs[i].Group.ToUpperInvariant();
            if (!byName.TryGetValue(g, out int gi))
            {
                gi = groups.Count;
                byName[g] = gi;
                groups.Add((g, new()));
            }
            groups[gi].devs.Add((i, devs[i]));
        }

        foreach (var (gname, gdevs) in groups)
            _clusters.AddChild(BuildCluster(gname, gdevs));
    }

    private Control BuildCluster(string groupName, List<(ushort idx, DevelopmentDef dev)> gdevs)
    {
        // Build the single-parent forest within this group.
        var nodes = gdevs.ToDictionary(d => d.idx, d => new TreeNode { Index = d.idx, Dev = d.dev, Done = StatusOf(d.dev, d.idx) == Status.Done });
        var roots = new List<TreeNode>();
        foreach (var (idx, dev) in gdevs)
        {
            ushort? parent = null;
            foreach (var (pidx, pdev) in gdevs)
            {
                if (pidx == idx)
                    continue;
                if (pdev.GrantedTechIdx.Length > 0 && dev.RequiredTechIdx.Any(rt => pdev.GrantedTechIdx.Contains(rt)))
                {
                    parent = pidx;
                    break; // first such parent by list order
                }
            }
            if (parent is ushort p && nodes.ContainsKey(p) && p != idx)
                nodes[p].Children.Add(nodes[idx]);
            else
                roots.Add(nodes[idx]);
        }
        MarkLast(roots);
        foreach (TreeNode n in nodes.Values)
            MarkLast(n.Children);

        int avail = gdevs.Count(d => StatusOf(d.dev, d.idx) == Status.Available);

        // Cluster = fixed-width column: collapsible header + node tree.
        var cluster = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        cluster.AddThemeConstantOverride("separation", 3);

        bool collapsed = _collapsedGroups.Contains(groupName);
        var header = new ClusterHeader();
        header.Configure(collapsed ? "▸" : "▾", groupName, $"{avail}/{gdevs.Count}");
        header.Pressed += () =>
        {
            if (!_collapsedGroups.Add(groupName))
                _collapsedGroups.Remove(groupName);
            _statusSig = long.MinValue; // force a structural rebuild
        };
        cluster.AddChild(header);

        if (collapsed)
            return cluster;

        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 3);
        cluster.AddChild(body);

        foreach (TreeNode root in roots)
            EmitNode(body, root, 0, Array.Empty<bool>(), parentDone: false);

        return cluster;
    }

    private static void MarkLast(List<TreeNode> siblings)
    {
        for (int i = 0; i < siblings.Count; i++)
            siblings[i].IsLast = i == siblings.Count - 1;
    }

    // Pre-order DFS emit. ancestorVert[j] = draw a vertical continuation at rail column j.
    private void EmitNode(VBoxContainer body, TreeNode node, int depth, bool[] ancestorVert, bool parentDone)
    {
        Status status = StatusOf(node.Dev, node.Index);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 0);
        if (depth > 0)
        {
            var rail = new RailStrip();
            rail.Configure(depth, ancestorVert, node.IsLast, parentDone, RailColor, DesignTokens.TeamAccent);
            row.AddChild(rail);
        }

        var card = new NodeCard();
        bool hasChildren = node.Children.Count > 0;
        bool nodeCollapsed = _collapsedNodes.Contains(node.Index);
        ushort idx = node.Index;
        card.Configure(_world!, status, node.Dev, TechDetailPanel.PriceText(node.Dev.Price), hasChildren, nodeCollapsed,
            ActiveInfoFor(node.Index), _selectedDev == node.Index);
        card.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        card.Pressed += () => SelectDev(idx);
        card.ChevronPressed += () =>
        {
            if (!_collapsedNodes.Add(idx))
                _collapsedNodes.Remove(idx);
            _statusSig = long.MinValue;
        };
        row.AddChild(card);
        body.AddChild(row);
        _cards.Add((node.Index, card));

        if (hasChildren && !nodeCollapsed)
        {
            var childAnc = new bool[depth + 1];
            Array.Copy(ancestorVert, childAnc, depth);
            childAnc[depth] = !node.IsLast; // this node's column continues if it has a following sibling
            foreach (TreeNode child in node.Children)
                EmitNode(body, child, depth + 1, childAnc, node.Done);
        }
    }

    // (start, duration) of a dev if it is actively researching anywhere, else null.
    private (uint start, uint dur)? ActiveInfoFor(ushort devIndex)
    {
        if (_world == null)
            return null;
        foreach (var (_, r) in _world.AllResearch())
            foreach (var a in r.Active)
                if (a.DevIndex == devIndex)
                    return (a.StartTick, a.DurationTicks);
        return null;
    }

    // ---- status resolution (client-side, streamed data only) --------------

    public Status StatusOf(DevelopmentDef dev, ushort idx)
    {
        if (_world == null)
            return Status.Locked;
        byte team = Team;
        bool AllTech(ushort[] a) => a.All(t => _world.TeamOwnsTech(team, t));
        bool AllCap(byte[] a) => a.All(c => _world.TeamOwnsCap(team, c));

        // done: all grants owned (tech-only devs vanish from "researchable" once granted).
        if ((dev.GrantedTechIdx.Length > 0 || dev.GrantedCaps.Length > 0) && AllTech(dev.GrantedTechIdx) && AllCap(dev.GrantedCaps))
            return Status.Done;
        // in-progress / on-deck at any friendly base.
        foreach (var (_, r) in _world.AllResearch())
        {
            foreach (var a in r.Active)
                if (a.DevIndex == idx)
                    return Status.InProgress;
            if (r.OnDeck == idx)
                return Status.OnDeck;
        }
        // available (mirror of BuildableResolver).
        bool obsoleted = dev.ObsoletedByTechIdx.Any(t => _world.TeamOwnsTech(team, t));
        if (!obsoleted && AllTech(dev.RequiredTechIdx) && AllCap(dev.RequiredCaps))
            return Status.Available;
        return Status.Locked;
    }

    // ---- selection + detail -----------------------------------------------

    private void SelectDev(ushort idx)
    {
        _selectedDev = idx;
        foreach (var (dev, card) in _cards)
            card.SetSelected(dev == idx);
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        if (_defs == null || _world == null)
            return;

        if (_selectedDev is not ushort sel || _defs.GetDevelopment(sel) is not DevelopmentDef dev)
        {
            _detail.SetSchematic("⚛", "// SCHEMATIC");
            _detail.SetTitle("SELECT A TECHNOLOGY");
            _detail.SetStatus("—", StatusPill.Kind.Neutral);
            _detail.SetDescription("Choose a node on the research canvas to review its cost, prerequisites, and unlocks.");
            _detail.SetMeta("—", "—", "—");
            _detail.ClearPrereqs();
            _detail.ClearUnlocks();
            SetFooter(disabled: true, "▸ CHOOSE A TECHNOLOGY", ButtonVariant.Secondary, null, null);
            return;
        }

        Status status = StatusOf(dev, sel);
        _detail.SetTitle(dev.Name.ToUpperInvariant());
        (string pillText, StatusPill.Kind pillKind, bool pulse) = PillFor(status);
        _detail.SetStatus(pillText, pillKind, pulse);
        // A single-scope station upgrade (v39) swaps the HOSTING base to its next tier — make that clear.
        string desc = string.IsNullOrEmpty(dev.Description) ? "No briefing on file." : dev.Description;
        if (dev.UpgradeScope == DevelopmentDef.UpgradeScopeSingle)
            desc += "\n\n▲ Upgrades this base to its next tier.";
        _detail.SetDescription(desc);
        _detail.SetMeta(TechDetailPanel.PriceText(dev.Price), TechDetailPanel.Mmss(dev.BuildTimeSeconds),
            string.IsNullOrEmpty(_baseTitle) ? "—" : _baseTitle);

        BuildPrereqs(dev);
        BuildUnlocks(dev);
        BuildActionFooter(dev, sel, status);
    }

    private void BuildPrereqs(DevelopmentDef dev)
    {
        byte team = Team;
        var rows = new List<(string, bool)>();
        foreach (ushort t in dev.RequiredTechIdx)
            rows.Add((_defs!.GetTech(t)?.Name ?? $"TECH {t}", _world!.TeamOwnsTech(team, t)));
        foreach (byte c in dev.RequiredCaps)
            rows.Add((TechDetailPanel.CapName(c), _world!.TeamOwnsCap(team, c)));
        _detail.SetPrereqs(rows); // empty -> "No prerequisites" row
    }

    private void BuildUnlocks(DevelopmentDef dev)
    {
        var G = new HashSet<ushort>(dev.GrantedTechIdx);
        var names = new List<string>();

        // Each granted tech itself.
        foreach (ushort t in dev.GrantedTechIdx)
            names.Add(_defs!.GetTech(t)?.Name ?? $"TECH {t}");

        // Team-wide stat multipliers this dev grants (v41), as readable signed-percent effect lines
        // ("Gun damage +10%"). Slice devs carry none ⇒ dormant, but wired for a faction that authors them.
        foreach (var m in dev.Attributes)
            names.Add($"{AttrName(m.Attr)} {SignedPercent(m.Mult)}");

        bool Intersects(ushort[] req) => req.Any(G.Contains);
        // Other developments gated by a granted tech.
        foreach (DevelopmentDef d in _defs!.AllDevelopments())
            if (d.Id != dev.Id && Intersects(d.RequiredTechIdx))
                names.Add(d.Name);
        // Station catalog entries.
        foreach (StationCatalogDef s in _defs.AllStationCatalog())
            if (Intersects(s.RequiredTechIdx))
                names.Add(s.Name);
        // Hulls certified by a granted tech (v43: ShipClassDef.RequiredTechIdx is now streamed). Names
        // the bomber / adv-fighter / devastator a dev unlocks — e.g. "Upgrade Supremacy" lists the Adv
        // Fighter. Server gating still lives in BuildableResolver; this is the display side.
        foreach (ShipClassDef sc in _defs.BuildableShips())
            if (Intersects(sc.RequiredTechIdx))
                names.Add(sc.Name);
        // Weapons (arsenal locks).
        foreach (WeaponDef w in _defs.AllWeapons())
            if (Intersects(w.RequiredTechIdx))
                names.Add(w.Name);

        _detail.SetUnlocks(names); // dedupe + "// nothing new" fallback live in the panel
    }

    // (mult - 1) as a signed percent: 1.10 -> "+10%", 0.85 -> "-15%". Rounds to the nearest whole percent.
    private static string SignedPercent(float mult)
    {
        int pct = Mathf.RoundToInt((mult - 1f) * 100f);
        return pct >= 0 ? $"+{pct}%" : $"{pct}%";
    }

    // Readable name for a GameAttribute wire byte (order mirrors the factions library GameAttribute enum,
    // shared/AttrMod carries the byte). Only the attributes a dev can plausibly carry are named; anything
    // else falls back to a generic label so a future faction never renders a blank line.
    private static string AttrName(byte attr) => attr switch
    {
        0 => "Top speed",
        1 => "Thrust",
        2 => "Turn rate",
        4 => "Station armor",
        6 => "Station shield",
        8 => "Ship armor",
        9 => "Ship shield",
        11 => "Scan range",
        12 => "Signature",
        13 => "Max energy",
        17 => "Mining rate",
        18 => "Mining yield",
        19 => "Mining capacity",
        21 => "Gun damage",
        22 => "Missile damage",
        _ => $"Attr {attr}",
    };

    // The (base-type, display-name) a single-scope station-upgrade dev must be authorized AT: the base
    // whose SUCCESSOR tier a tech this dev grants unlocks. Mirrors the sim's TriggeredUpgrades match
    // (successor.RequiredTechIdx vs dev.GrantedTechIdx). Null when the dev triggers no derivable base
    // upgrade (then the from-type gate is skipped).
    private (byte type, string name)? UpgradeFromType(DevelopmentDef dev)
    {
        if (_defs is null || dev.GrantedTechIdx.Length == 0)
            return null;
        // The tier gate (required-techs) rides the StationCatalogDef, not the BaseDef — so match a
        // runtime station whose SUCCESSOR catalog entry is gated by a tech this dev grants.
        var granted = new HashSet<ushort>(dev.GrantedTechIdx);
        var catalog = _defs.AllStationCatalog();
        foreach (StationCatalogDef from in catalog)
        {
            if (from.BaseTypeId < 0 || from.SuccessorBaseTypeId < 0)
                continue;
            foreach (StationCatalogDef to in catalog)
                if (to.BaseTypeId == from.SuccessorBaseTypeId && to.RequiredTechIdx.Any(granted.Contains))
                    return ((byte)from.BaseTypeId, from.Name);
        }
        return null;
    }

    // ---- action footer state machine --------------------------------------

    private void BuildActionFooter(DevelopmentDef dev, ushort sel, Status status)
    {
        // Optimistic PENDING after an op.
        if (_pendingDev == sel)
        {
            if (status != _pendingStatus && Time.GetTicksMsec() < _pendingUntilMsec)
                _pendingDev = null; // status moved — resolved
            else if (Time.GetTicksMsec() >= _pendingUntilMsec)
                _pendingDev = null; // timed out
            else
            {
                SetFooter(true, "◷ PENDING…", ButtonVariant.Secondary, null, null);
                return;
            }
        }

        bool commander = _net?.IsCommander ?? false;
        int price = dev.Price;
        int credits = _world!.TeamCredits(Team);

        switch (status)
        {
            case Status.Done:
                SetFooter(true, "✓ RESEARCHED", ButtonVariant.Secondary, null, null);
                return;
            case Status.InProgress:
                SetFooter(true, "◷ RESEARCHING…", ButtonVariant.Secondary,
                    commander ? ("✕ CANCEL ORDER", (Action)(() => CancelActive(sel))) : ((string, Action)?)null, null);
                return;
            case Status.OnDeck:
                SetFooter(true, "◷ ON DECK", ButtonVariant.Secondary,
                    commander ? ("✕ REMOVE FROM QUEUE", (Action)(() => CancelOnDeck(sel))) : ((string, Action)?)null, null);
                return;
            case Status.Locked:
                SetFooter(true, "⊘ LOCKED", ButtonVariant.Secondary, null, $"Needs {FirstUnmetName(dev)}");
                return;
        }

        // status == Available.
        // A single-scope station upgrade physically swaps the HOSTING base, so it can only be
        // authorized at a base of the chain's from-type. Surface that here (matching the sim's
        // ResearchOpStart guard) instead of letting a Garrison authorize "Upgrade Supremacy" and
        // relying on the server to reject it.
        if (dev.UpgradeScope == DevelopmentDef.UpgradeScopeSingle
            && UpgradeFromType(dev) is (byte fromType, string fromName)
            && _baseType != fromType)
        {
            SetFooter(true, $"▲ AUTHORIZE AT {fromName.ToUpperInvariant()}", ButtonVariant.Secondary, null,
                $"This upgrade must be authorized at a {fromName} — {_baseTitle} is the wrong base type.",
                DesignTokens.Warn);
            return;
        }
        if (!commander)
        {
            string cmdr = CommanderName();
            SetFooter(true, "▲ COMMANDER AUTHORIZATION REQUIRED", ButtonVariant.Danger, null,
                cmdr.Length > 0 ? $"Only {cmdr} can authorize research." : "Only the team commander can authorize research.",
                DesignTokens.Warn);
            return;
        }
        if (credits < price)
        {
            SetFooter(true, "INSUFFICIENT FUNDS", ButtonVariant.Secondary, null, $"Short {price - credits} CREDITS");
            return;
        }

        // Occupancy at the selected base.
        int slots = SlotCount();
        var res = _baseId != 0 ? _world.ResearchAt(_baseId) : null;
        int active = res?.Active.Length ?? 0;
        bool onDeck = res?.OnDeck != null;
        if (active < slots)
            SetFooter(false, "◆ AUTHORIZE RESEARCH", ButtonVariant.Primary, null, $"{TechDetailPanel.PriceText(price)} · {TechDetailPanel.Mmss(dev.BuildTimeSeconds)} at {_baseTitle}");
        else if (!onDeck)
            SetFooter(false, "⊕ QUEUE ON DECK", ButtonVariant.Primary, null, $"All {slots} slots busy — reserves the next slot ({TechDetailPanel.PriceText(price)})");
        else
            SetFooter(true, "◷ BASE OCCUPIED", ButtonVariant.Secondary, null, "Slots full and on-deck taken — cancel an order first.");
    }

    private Action? _primaryAction;
    private Action? _secondaryAction;

    private void SetFooter(bool disabled, string text, ButtonVariant variant,
        (string text, Action act)? secondary, string? sub, Color? subColor = null)
    {
        // Presentation goes to the shared panel; the action semantics stay here (research-specific).
        _detail.SetFooter(disabled, text, variant, secondary?.text, sub, subColor);
        _primaryAction = disabled ? null : PrimaryActionForText(text);
        _secondaryAction = secondary?.act;
    }

    // The primary button carries either AUTHORIZE (op 0 at the selected base) — the only live case.
    private Action? PrimaryActionForText(string text) =>
        text.StartsWith("◆") || text.StartsWith("⊕")
            ? () => { if (_selectedDev is ushort d) Authorize(d); }
            : null;

    private void OnFooterPrimary() => _primaryAction?.Invoke();

    private void OnFooterSecondary() => _secondaryAction?.Invoke();

    private void Authorize(ushort devIndex)
    {
        if (_baseId == 0)
            return;
        Send(0, _baseId, devIndex);
    }

    private void CancelActive(ushort devIndex)
    {
        // Cancel targets the base actually running it (may not be the selected base).
        foreach (var (id, r) in _world!.AllResearch())
            if (r.Active.Any(a => a.DevIndex == devIndex))
            {
                Send(1, id, devIndex);
                return;
            }
    }

    private void CancelOnDeck(ushort devIndex)
    {
        foreach (var (id, r) in _world!.AllResearch())
            if (r.OnDeck == devIndex)
            {
                Send(2, id, devIndex);
                return;
            }
    }

    // Send an op + arm the optimistic PENDING state for the selected dev.
    private void Send(byte op, ulong baseId, ushort devIndex)
    {
        _net?.SendResearch(op, baseId, devIndex);
        if (_selectedDev is ushort sel && _defs?.GetDevelopment(sel) is DevelopmentDef d)
        {
            _pendingDev = sel;
            _pendingStatus = StatusOf(d, sel);
            _pendingUntilMsec = Time.GetTicksMsec() + 3000;
        }
        _statusSig = long.MinValue; // reflect the pending affordance immediately
    }

    // ---- helpers -----------------------------------------------------------

    private int SlotCount() => Math.Max(1, (int)(_defs?.GetBaseDef(0)?.ResearchSlots ?? 1));

    private string FirstUnmetName(DevelopmentDef dev)
    {
        byte team = Team;
        foreach (ushort t in dev.RequiredTechIdx)
            if (!_world!.TeamOwnsTech(team, t))
                return _defs!.GetTech(t)?.Name ?? $"tech {t}";
        foreach (byte c in dev.RequiredCaps)
            if (!_world!.TeamOwnsCap(team, c))
                return TechDetailPanel.CapName(c);
        if (dev.ObsoletedByTechIdx.Any(t => _world!.TeamOwnsTech(team, t)))
            return "— superseded";
        return "an unmet requirement";
    }

    private string CommanderName()
    {
        // Best-effort: GameNetClient exposes commander client ids; a friendly name isn't guaranteed.
        int id = _net?.CommanderIdOf(Team) ?? -1;
        return id >= 0 ? $"the commander" : "";
    }

    private static (string, StatusPill.Kind, bool) PillFor(Status s) => s switch
    {
        Status.Done => ("✓ RESEARCHED", StatusPill.Kind.Ok, false),
        Status.InProgress => ("◷ RESEARCHING", StatusPill.Kind.Warn, true),
        Status.OnDeck => ("◷ ON DECK", StatusPill.Kind.Data, false),
        Status.Available => ("◈ AVAILABLE", StatusPill.Kind.Accent, false),
        _ => ("⊘ LOCKED", StatusPill.Kind.Neutral, false),
    };

    // ---- demo hooks (used by --hangar-demo harness) -----------------------

    public Vector2? DemoFirstAvailableNodeCenter()
    {
        foreach (var (dev, card) in _cards)
            if (_defs?.GetDevelopment(dev) is DevelopmentDef d && StatusOf(d, dev) == Status.Available)
                return card.GetGlobalRect().GetCenter();
        return null;
    }

    public Vector2? DemoAuthorizeCenter() =>
        !_detail.FooterPrimaryDisabled && (_detail.FooterPrimaryText.StartsWith("◆") || _detail.FooterPrimaryText.StartsWith("⊕"))
            ? _detail.FooterPrimaryCenter
            : null;
}

// =====================================================================
//  Sub-controls
// =====================================================================

// A cluster header: chevron + group label (mono, letter-spaced) + avail/total count, on a cyan
// left-border tinted bar. Cyan is chrome (a structural discipline header), not team identity.
internal partial class ClusterHeader : PanelContainer
{
    private Label _chevron = null!;
    private Label _label = null!;
    private Label _count = null!;
    public event Action? Pressed;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_chevron != null)
            return;
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccentBase, 0.08f), BorderColor = new Color(DesignTokens.TeamAccentBase, 0.55f), AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 2;
        sb.ContentMarginLeft = 10;
        sb.ContentMarginRight = 10;
        sb.ContentMarginTop = sb.ContentMarginBottom = 7;
        AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        _chevron = UiKit.MakeLabel("▾", UiKit.TextStyle.Data, DesignTokens.TeamAccent);
        _label = UiKit.MakeLabel("", UiKit.TextStyle.Label, DesignTokens.TextHi);
        _label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _count = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _count.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(_chevron);
        row.AddChild(_label);
        row.AddChild(_count);
        AddChild(row);
    }

    public void Configure(string chevron, string label, string count)
    {
        EnsureBuilt();
        _chevron.Text = chevron;
        _label.Text = label;
        _count.Text = count;
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

// The rail cells left of a nested node: vertical ancestor continuations + an elbow/tee connector.
internal partial class RailStrip : Control
{
    private int _depth;
    private bool[] _ancestorVert = Array.Empty<bool>();
    private bool _isLast;
    private bool _parentDone;
    private Color _rail;
    private Color _cyan;
    private const float Cell = 22f;

    public void Configure(int depth, bool[] ancestorVert, bool isLast, bool parentDone, Color rail, Color cyan)
    {
        _depth = depth;
        _ancestorVert = ancestorVert;
        _isLast = isLast;
        _parentDone = parentDone;
        _rail = rail;
        _cyan = cyan;
        CustomMinimumSize = new Vector2(depth * Cell, 0);
        SizeFlagsVertical = SizeFlags.Fill;
        MouseFilter = MouseFilterEnum.Ignore;
        QueueRedraw();
    }

    public override void _Ready() => Resized += QueueRedraw;

    public override void _Draw()
    {
        if (_depth <= 0)
            return;
        float h = Size.Y;
        // Ancestor columns.
        for (int j = 0; j < _depth - 1; j++)
        {
            if (j < _ancestorVert.Length && _ancestorVert[j])
            {
                float cx = j * Cell + Cell * 0.5f;
                DrawLine(new Vector2(cx, 0), new Vector2(cx, h), _rail, 1f);
            }
        }
        // Connector column.
        float ccx = (_depth - 1) * Cell + Cell * 0.5f;
        float midY = h * 0.5f;
        Color color = _parentDone ? _cyan : _rail;
        DrawLine(new Vector2(ccx, 0), new Vector2(ccx, midY), color, 1f);
        if (!_isLast)
            DrawLine(new Vector2(ccx, midY), new Vector2(ccx, h), _rail, 1f);
        DrawLine(new Vector2(ccx, midY), new Vector2(_depth * Cell, midY), color, 1f);
    }
}

// A research node card: 32px status badge + name + status label + price, with a chevron toggle
// when it has children. In-progress cards paint an amber progress gradient behind their content
// and pulse the badge (only while visible).
internal partial class NodeCard : PanelContainer
{
    public event Action? Pressed;
    public event Action? ChevronPressed;

    private WorldRenderer? _world;
    private ResearchTab.Status _status;
    private bool _selected;
    private (uint start, uint dur)? _active;

    private ProgressUnderlay _underlay = null!;
    private Label _badge = null!;
    private Label _name = null!;
    private Label _statusLabel = null!;
    private Label _price = null!;
    private Label _chevron = null!;

    public override void _Ready() => EnsureBuilt();

    private void EnsureBuilt()
    {
        if (_badge != null)
            return;

        _underlay = new ProgressUnderlay { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_underlay);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        AddChild(row);

        _badge = new Label
        {
            CustomMinimumSize = new Vector2(32, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge.AddThemeFontOverride("font", UiFonts.Mono);
        _badge.AddThemeFontSizeOverride("font_size", 16);
        row.AddChild(_badge);

        var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        texts.AddThemeConstantOverride("separation", 0);
        _name = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextHi);
        _name.AddThemeFontOverride("font", UiFonts.SairaSemi);
        _name.AddThemeFontSizeOverride("font_size", 13);
        _name.ClipText = true;
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _statusLabel = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _statusLabel.AddThemeFontSizeOverride("font_size", 9);
        texts.AddChild(_name);
        texts.AddChild(_statusLabel);
        row.AddChild(texts);

        _price = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Warn);
        _price.AddThemeFontSizeOverride("font_size", 12);
        _price.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(_price);

        _chevron = new Label { Text = "", CustomMinimumSize = new Vector2(18, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _chevron.AddThemeFontOverride("font", UiFonts.Mono);
        _chevron.AddThemeColorOverride("font_color", DesignTokens.Text2);
        _chevron.MouseFilter = MouseFilterEnum.Stop;
        row.AddChild(_chevron);
    }

    public void Configure(WorldRenderer world, ResearchTab.Status status, DevelopmentDef dev, string priceText,
        bool hasChildren, bool collapsed, (uint start, uint dur)? active, bool selected)
    {
        EnsureBuilt();
        _world = world;
        _status = status;
        _active = active;
        _selected = selected;
        _name.Text = dev.Name.ToUpperInvariant();
        _price.Text = priceText;
        _chevron.Text = hasChildren ? (collapsed ? "▸" : "▾") : "";

        (string badgeGlyph, Color badgeColor, bool badgeFilled, string label, Color labelColor) = StyleFor(status);
        _badge.Text = badgeGlyph;
        _badge.AddThemeColorOverride("font_color", badgeFilled ? DesignTokens.Void : badgeColor);
        var bsb = new StyleBoxFlat
        {
            BgColor = badgeFilled ? badgeColor : new Color(badgeColor, 0.12f),
            BorderColor = new Color(badgeColor, 0.9f),
            AntiAliasing = false,
        };
        bsb.SetCornerRadiusAll(0);
        bsb.SetBorderWidthAll(1);
        _badge.AddThemeStyleboxOverride("normal", bsb);

        _statusLabel.Text = label;
        _statusLabel.AddThemeColorOverride("font_color", labelColor);

        _underlay.Progress = 0f;
        _underlay.Visible = status == ResearchTab.Status.InProgress;

        Modulate = status == ResearchTab.Status.Locked ? new Color(1, 1, 1, 0.6f) : Colors.White;
        Restyle();
    }

    // Showcase-only: render a node card in a given status with no live world (progress is static).
    public void ConfigureMock(ResearchTab.Status status, string name, string priceText, bool hasChildren, float progress, bool selected = false)
    {
        EnsureBuilt();
        _world = null; // _Process no-ops without a world
        _status = status;
        _selected = selected;
        _name.Text = name.ToUpperInvariant();
        _price.Text = priceText;
        _chevron.Text = hasChildren ? "▾" : "";

        (string badgeGlyph, Color badgeColor, bool badgeFilled, string label, Color labelColor) = StyleFor(status);
        _badge.Text = badgeGlyph;
        _badge.AddThemeColorOverride("font_color", badgeFilled ? DesignTokens.Void : badgeColor);
        var bsb = new StyleBoxFlat { BgColor = badgeFilled ? badgeColor : new Color(badgeColor, 0.12f), BorderColor = new Color(badgeColor, 0.9f), AntiAliasing = false };
        bsb.SetCornerRadiusAll(0);
        bsb.SetBorderWidthAll(1);
        _badge.AddThemeStyleboxOverride("normal", bsb);
        _statusLabel.Text = status == ResearchTab.Status.InProgress ? "◷ 00:42" : label;
        _statusLabel.AddThemeColorOverride("font_color", labelColor);
        _underlay.Visible = status == ResearchTab.Status.InProgress;
        _underlay.Progress = progress;
        Modulate = status == ResearchTab.Status.Locked ? new Color(1, 1, 1, 0.6f) : Colors.White;
        Restyle();
    }

    public void SetSelected(bool sel)
    {
        _selected = sel;
        Restyle();
    }

    private static (string, Color, bool, string, Color) StyleFor(ResearchTab.Status s) => s switch
    {
        ResearchTab.Status.Done => ("◆", DesignTokens.Ok, true, "✓ RESEARCHED", DesignTokens.Ok),
        ResearchTab.Status.InProgress => ("◷", DesignTokens.Warn, false, "◷ --:--", DesignTokens.Warn),
        ResearchTab.Status.OnDeck => ("⊕", DesignTokens.Data, false, "◷ ON DECK", DesignTokens.Data),
        ResearchTab.Status.Available => ("◈", DesignTokens.TeamAccent, false, "AVAILABLE", DesignTokens.TeamAccent),
        _ => ("⊘", DesignTokens.TextDim, false, "⊘ LOCKED", DesignTokens.TextDim),
    };

    private void Restyle()
    {
        Color accent = _status switch
        {
            ResearchTab.Status.Done => DesignTokens.Ok,
            ResearchTab.Status.InProgress => DesignTokens.Warn,
            ResearchTab.Status.OnDeck => DesignTokens.Data,
            ResearchTab.Status.Available => DesignTokens.TeamAccent,
            _ => DesignTokens.BorderLo,
        };
        var border = _selected ? DesignTokens.TeamAccent : new Color(accent, 0.4f);
        var sb = new StyleBoxFlat
        {
            BgColor = _selected ? new Color(DesignTokens.TeamAccentBase, 0.14f) : new Color(DesignTokens.Panel, 0.85f),
            BorderColor = border,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(_selected ? 2 : 1);
        sb.SetContentMarginAll(8);
        AddThemeStyleboxOverride("panel", sb);
    }

    public override void _Process(double delta)
    {
        if (_status != ResearchTab.Status.InProgress || _world == null || _active is not (uint start, uint dur))
            return;
        if (!IsVisibleInTree())
            return;
        float p = _world.ResearchProgress(start, dur);
        _underlay.Progress = p;
        _underlay.QueueRedraw();
        float remaining = (start + dur - _world.ServerTick) / FlightModel.TickRate;
        if (remaining < 0)
            remaining = 0;
        int t = (int)MathF.Ceiling(remaining);
        _statusLabel.Text = $"◷ {t / 60:00}:{t % 60:00}";
        float pulse = 0.6f + 0.4f * Mathf.Sin(Time.GetTicksMsec() / 240f);
        _badge.Modulate = new Color(1, 1, 1, pulse);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb)
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
            // The chevron third clicks toggle collapse; the rest of the card selects.
            if (_chevron.Text.Length > 0 && mb.Position.X >= Size.X - 26f)
                ChevronPressed?.Invoke();
            else
                Pressed?.Invoke();
            AcceptEvent();
        }
    }
}

// Amber progress fill. As a node-card underlay it paints only a faint left-portion gradient behind
// content; as a banner bar (ShowTrack) it draws a subtle track + solid amber fill.
internal partial class ProgressUnderlay : Control
{
    public float Progress;
    public bool ShowTrack;

    public override void _Draw()
    {
        float p = Mathf.Clamp(Progress, 0f, 1f);
        if (ShowTrack)
        {
            DrawRect(new Rect2(0, 0, Size.X, Size.Y), new Color(DesignTokens.BorderLo, 0.6f), filled: true);
            DrawRect(new Rect2(0, 0, Size.X * p, Size.Y), DesignTokens.Warn, filled: true);
            return;
        }
        if (p <= 0f)
            return;
        DrawRect(new Rect2(0, 0, Size.X * p, Size.Y), new Color(DesignTokens.Warn, 0.16f), filled: true);
    }
}

// A status banner for the selected base's research: active order (amber, live countdown + progress
// bar + commander cancel), on-deck (data-blue), or idle (dashed dim note).
internal partial class ActiveBanner : PanelContainer
{
    private WorldRenderer? _world;
    private uint _start;
    private uint _dur;
    private Label _title = null!;
    private Label _count = null!;
    private ProgressUnderlay _bar = null!;
    private bool _live;

    public void ConfigureActive(WorldRenderer world, string devName, uint start, uint dur, Action? onCancel)
    {
        _world = world;
        _start = start;
        _dur = dur;
        _live = true;
        Build(DesignTokens.Warn, $"◷ RESEARCHING · {devName}", "--:--", onCancel, cancelText: "✕ CANCEL", withBar: true);
    }

    public void ConfigureOnDeck(string devName, Action? onCancel)
    {
        _live = false;
        Build(DesignTokens.Data, $"⊕ ON DECK — {devName}", "starts when a slot frees", onCancel, cancelText: "✕ REMOVE", withBar: false);
    }

    public void ConfigureIdle()
    {
        _live = false;
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.Void, 0.4f), BorderColor = DesignTokens.BorderLo, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(10);
        AddThemeStyleboxOverride("panel", sb);
        var lbl = UiKit.MakeLabel("◌ BASE IDLE — authorize research below", UiKit.TextStyle.Data, DesignTokens.TextDim);
        AddChild(lbl);
    }

    // Generic timed banner (reused by the Build tab for constructor production/build phases): a live
    // countdown + progress bar in the given accent, with an optional cancel button. Progress derives
    // from start+dur vs ServerTick, exactly like a research order.
    public void ConfigureTimed(WorldRenderer world, Color accent, string title, uint start, uint dur, Action? onCancel, string cancelText)
    {
        _world = world;
        _start = start;
        _dur = dur;
        _live = true;
        Build(accent, title, "--:--", onCancel, cancelText, withBar: true);
    }

    // Queued Build-tab order (waiting for a build slot at its garrison): the same layout as a timed
    // order but NOT live — a static 0% bar and a "QUEUED" marker, with an optional commander cancel.
    // Mirroring ConfigureTimed keeps a queued→producing transition visually continuous.
    public void ConfigureQueued(Color accent, string title, Action? onCancel, string cancelText)
    {
        _live = false;
        Build(accent, title, "QUEUED", onCancel, cancelText, withBar: true);
    }

    // Generic untimed status note (reused by the Build tab for idle / en-route / moving constructors):
    // a left-accent bordered label with no bar or countdown.
    public void ConfigureNote(Color accent, string title)
    {
        _live = false;
        var sb = new StyleBoxFlat { BgColor = new Color(accent, 0.06f), BorderColor = new Color(accent, 0.55f), AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 2;
        sb.ContentMarginLeft = 12;
        sb.ContentMarginRight = 10;
        sb.ContentMarginTop = sb.ContentMarginBottom = 8;
        AddThemeStyleboxOverride("panel", sb);
        AddChild(UiKit.MakeLabel(title, UiKit.TextStyle.Data, accent));
    }

    private void Build(Color accent, string title, string countText, Action? onCancel, string cancelText, bool withBar)
    {
        var sb = new StyleBoxFlat { BgColor = new Color(accent, 0.08f), BorderColor = accent, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 2;
        sb.ContentMarginLeft = 12;
        sb.ContentMarginRight = 10;
        sb.ContentMarginTop = sb.ContentMarginBottom = 8;
        AddThemeStyleboxOverride("panel", sb);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        AddChild(col);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _title = UiKit.MakeLabel(title, UiKit.TextStyle.Label, accent);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(_title);
        _count = UiKit.MakeLabel(countText, UiKit.TextStyle.Data, accent);
        _count.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_count);
        if (onCancel != null)
        {
            var cancel = UiKit.MakeButton(cancelText, onCancel, ButtonVariant.Ghost);
            cancel.CustomMinimumSize = new Vector2(90, 26);
            row.AddChild(cancel);
        }
        col.AddChild(row);

        if (withBar)
        {
            _bar = new ProgressUnderlay { ShowTrack = true, CustomMinimumSize = new Vector2(0, 4), MouseFilter = MouseFilterEnum.Ignore };
            col.AddChild(_bar);
        }
    }

    public override void _Process(double delta)
    {
        if (!_live || _world == null || _bar == null || !IsVisibleInTree())
            return;
        float remaining = (_start + _dur - _world.ServerTick) / FlightModel.TickRate;
        if (remaining < 0)
            remaining = 0;
        int t = (int)MathF.Ceiling(remaining);
        _count.Text = $"{t / 60:00}:{t % 60:00}";
        _bar.Progress = _world.ResearchProgress(_start, _dur);
        _bar.QueueRedraw();
    }
}
