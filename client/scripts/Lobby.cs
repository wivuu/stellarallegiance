using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// Game Lobby — the screen a pilot sees after joining a server and whenever they aren't flying
// (pre-match, post-match, or "joined an in-progress match but not yet deployed"). It lets them
// see the teams, pick a side, and chat with everyone on the server. Ported from the Claude
// Design "Game Lobby" spec onto the Stellar Allegiance design system.
//
// Pure overlay: it reads the server's lobby roster (GameNetClient.LobbyPlayers), match phase and
// score (WorldRenderer), and the chat relay (GameNetClient.ChatReceived); it drives the server
// with SetTeam / SetReady / SendChat. Match start/stop and balance rules are enforced server-side.
// Created by the Hud.
//
// Controls (see the plan): JOIN {TEAM} only picks a side; LAUNCH is the deploy action — it opens
// the mandatory ship-select hangar mid-match, or readies up pre-match so the match can start.
//
// PLACEHOLDERS (data the wire doesn't carry yet — rendered but clearly stubbed): per-player
// SHIP/K/D/EJ/PTS stats and team kill totals; the NOAT ("not on a team") tab + chat channel
// (spectator/unassigned — the team byte is always 0/1 today); match name/mode/sector/clock.
// TeamName() is the single hook for future streamed team names.
public partial class Lobby : Control
{
    // Team identity stays the faction colours (NOT the cyan structural accent).
    private static readonly Color Team0 = DesignTokens.Faction0;
    private static readonly Color Team1 = DesignTokens.Faction1;

    // The NOAT ("not on a team") pseudo-team. No such server state exists yet, so this tab/channel
    // is a spectator/unassigned placeholder rendered in neutral chrome.
    private const int NoatTeam = 2;

    // Chrome bars (header / status / comms) get a darker fill stacked on the scrim so they read as
    // solid UI over the dimmed game scene behind.
    private static readonly Color ChromeBar = new(DesignTokens.Void, 0.55f);

    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;
    private GameNetClient _net = null!;

    // Header / status bar.
    private Label _online = null!;
    private StatusPill _phasePill = null!;
    private Label _clock = null!;
    private Label _matchTitle = null!;
    private Label _matchSub = null!; // "{mode} · {sector} · {n} GARRISONS" — driven by the selected map
    private Label _scoreName0 = null!; // team-name labels flanking the score (live-updated on rename)
    private Label _scoreName1 = null!;
    private Label _score0 = null!;
    private Label _score1 = null!;
    private ChamferButton _joinBtn = null!;
    private ChamferButton _launchBtn = null!;

    // Body.
    private VBoxContainer _teamTabs = null!; // the two team tab cards
    private Control _noatTabHost = null!; // the pinned NOAT tab card slot
    private HBoxContainer _rosterNameRow = null!; // team name + ✎, or the inline rename editor
    private Label _rosterTeamSub = null!;
    private Label _rosterPoints = null!;
    private Label _rosterCount = null!;
    private VBoxContainer _rosterRows = null!;

    // Team-name inline editor. _editingTeam is the team currently being renamed (-1 = none); it's
    // only ever the selected real-team tab, and only for a side the local pilot is on.
    private int _editingTeam = -1;
    private LineEdit _nameEdit = null!;

    // Max team-name length; the server re-clamps to the same value (ClientHub MsgSetTeamName).
    private const int TeamNameMax = Wire.TeamNameMaxLength;

    // Right-hand sector pane (map thumbnail + Sector Intel + Garrison Control).
    private SectorMapPreview _sectorMap = null!;
    private Label _mapName = null!;
    private Label _mapMeta = null!;
    private Label _mapCta = null!; // CHANGE ▸ (host) / VIEW ▸ (non-host)
    private Label _mapHostBadge = null!; // HOST / LOCKED
    private Label _siMode = null!, _siSector = null!, _siGarrisons = null!, _siSize = null!;
    private HBoxContainer _garrisonBar = null!; // proportional owner split
    private VBoxContainer _garrisonRows = null!; // per-faction node counts

    // Comms.
    private RichTextLabel _commsLog = null!;
    private LineEdit _commsInput = null!;
    private Label _sendChip = null!;
    // Two comms channels: 0 = ALL (scope 0), 1 = your group (scope 1 — teammates, or fellow NOAT
    // pilots while unassigned). The wire can't relay team-scope across groups, so there's no
    // "watch another group" channel — the group channel just follows your own side.
    private readonly ChamferButton[] _channelBtns = new ChamferButton[2];
    private readonly List<(ChatLine Line, string Time)> _messages = new();

    private int _selectedTeam; // which roster/tab is shown (0, 1, or NoatTeam)
    private int _chatChannel = 1; // 0 = ALL, 1 = your group (scope 1); default to the group channel
    private bool _selectionInit;
    private bool _wasVisible;
    private bool _bodyDirty = true;
    private double _clockSecs; // local match clock (placeholder — no clock on the wire)

    private const int Backlog = 60;

    // Label/colour for the group (scope-1) channel, which follows your own side.
    private string GroupLabel() => IsNoat(MyTeamNow()) ? "NOAT" : "TEAM";
    private Color GroupColor() => TeamColor(MyTeamNow());

    public void Init(ConnectionManager cm, WorldRenderer world)
    {
        _cm = cm;
        _world = world;
        _net = GetNode<GameNetClient>("../../GameNetClient");

        // Full-viewport overlay. Must set anchors AND offsets — anchors alone leave the control
        // at its default 0×0 size, collapsing every child into the top-left corner.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        UiTheme.Apply(this); // cascades the design theme to the roster/inputs/etc. below
        UiFonts.EnsureLoaded();

        // The overlay can sit over the live game scene (a just-ended match, or a match joined but
        // not yet deployed into), so it DIMS that scene with a translucent Void scrim rather than
        // occluding it. The chrome bars below add their own darker fill for legibility.
        var scrim = new ColorRect { Color = new Color(DesignTokens.Void, 0.72f) };
        scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scrim.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(scrim);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildHeader());
        root.AddChild(Hairline());
        root.AddChild(BuildStatusBar());
        root.AddChild(Hairline());
        root.AddChild(BuildBody());
        root.AddChild(Hairline());
        root.AddChild(BuildComms());

        _net.LobbyChanged += OnLobbyChanged;
        _net.MapListChanged += OnLobbyChanged; // catalog arrival refreshes the sector pane too
        _net.ChatReceived += OnChat;

        UpdateChannelButtons();
        RebuildComms();
    }

    public override void _ExitTree()
    {
        _net.LobbyChanged -= OnLobbyChanged;
        _net.MapListChanged -= OnLobbyChanged;
        _net.ChatReceived -= OnChat;
    }

    // ---- layout builders ----------------------------------------------------

    // Brand header: wordmark + MATCH chip on the left; online count, settings gear, leave on the right.
    private Control BuildHeader()
    {
        var bar = BarPanel(26, 12, ChromeBar);
        var row = new HBoxContainer();
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(row);

        var brand = new HBoxContainer();
        brand.AddThemeConstantOverride("separation", 11);
        brand.AddChild(new ColorRect { Color = DesignTokens.TeamAccent, CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter });
        var word = UiKit.MakeLabel("STELLAR ALLEGIANCE", UiKit.TextStyle.Label, DesignTokens.TextHi);
        word.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.SairaBold, 3));
        word.AddThemeFontSizeOverride("font_size", 16);
        brand.AddChild(word);
        brand.AddChild(Chip("MATCH"));
        row.AddChild(brand);

        row.AddChild(Spacer());

        var right = new HBoxContainer();
        right.AddThemeConstantOverride("separation", 16);
        right.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _online = UiKit.MakeLabel("● 0 ONLINE", UiKit.TextStyle.Data, DesignTokens.Ok);
        right.AddChild(_online);
        var gear = UiKit.MakeButton("⚙", () => SettingsDialog.Open(this), ButtonVariant.Icon);
        gear.CustomMinimumSize = new Vector2(34, 34);
        gear.FocusMode = FocusModeEnum.None;
        right.AddChild(gear);
        var leave = UiKit.MakeButton("LEAVE", () => _cm.Leave(), ButtonVariant.Ghost);
        leave.FocusMode = FocusModeEnum.None;
        right.AddChild(leave);
        row.AddChild(right);
        return bar;
    }

    // Status bar: phase pill + clock + match title on the left, score in the middle, JOIN/LAUNCH right.
    private Control BuildStatusBar()
    {
        var bar = BarPanel(26, 12, ChromeBar);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 20);
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(row);

        var left = new HBoxContainer();
        left.AddThemeConstantOverride("separation", 14);
        left.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _phasePill = new StatusPill();
        left.AddChild(_phasePill);
        _clock = UiKit.MakeLabel("00:00", UiKit.TextStyle.Data, DesignTokens.TextHi);
        _clock.AddThemeFontSizeOverride("font_size", 24);
        left.AddChild(_clock);
        // Match name / mode / sector — placeholder flavour text (not carried by the protocol).
        var titleCol = new VBoxContainer();
        titleCol.AddThemeConstantOverride("separation", 0);
        _matchTitle = UiKit.MakeLabel("SKIRMISH", UiKit.TextStyle.Title, DesignTokens.TextHi);
        _matchTitle.AddThemeFontSizeOverride("font_size", 15);
        titleCol.AddChild(_matchTitle);
        _matchSub = UiKit.MakeLabel("CONQUEST · UNCHARTED SECTOR", UiKit.TextStyle.Data, DesignTokens.Text2);
        _matchSub.AddThemeFontSizeOverride("font_size", 11);
        titleCol.AddChild(_matchSub);
        left.AddChild(titleCol);
        row.AddChild(left);

        row.AddChild(Spacer());
        // Score: team-coloured names flanking the mono tally (IRON COIL 3 — 2 ASH SYNDICATE).
        var score = new HBoxContainer();
        score.AddThemeConstantOverride("separation", 12);
        score.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        score.AddChild(Diamond(Team0, false));
        _scoreName0 = UiKit.MakeLabel(TeamName(0), UiKit.TextStyle.Label, Team0);
        _scoreName0.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        score.AddChild(_scoreName0);
        _score0 = UiKit.MakeLabel("0", UiKit.TextStyle.Data, Team0);
        _score0.AddThemeFontSizeOverride("font_size", 22);
        score.AddChild(_score0);
        score.AddChild(UiKit.MakeLabel("—", UiKit.TextStyle.Data, DesignTokens.TextDim).With(l => l.AddThemeFontSizeOverride("font_size", 22)));
        _score1 = UiKit.MakeLabel("0", UiKit.TextStyle.Data, Team1);
        _score1.AddThemeFontSizeOverride("font_size", 22);
        score.AddChild(_score1);
        _scoreName1 = UiKit.MakeLabel(TeamName(1), UiKit.TextStyle.Label, Team1);
        _scoreName1.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        score.AddChild(_scoreName1);
        score.AddChild(Diamond(Team1, false));
        row.AddChild(score);
        row.AddChild(Spacer());

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 10);
        actions.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _joinBtn = UiKit.MakeButton("JOIN", OnJoin, ButtonVariant.Primary);
        _launchBtn = UiKit.MakeButton("LAUNCH ▸", OnLaunch, ButtonVariant.Primary);
        _joinBtn.FocusMode = FocusModeEnum.None;
        _launchBtn.FocusMode = FocusModeEnum.None;
        actions.AddChild(_joinBtn);
        actions.AddChild(_launchBtn);
        row.AddChild(actions);
        return bar;
    }

    // Body: left team-tab column + right roster.
    private Control BuildBody()
    {
        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 0);
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        body.SizeFlagsVertical = SizeFlags.ExpandFill;

        // Left: team tabs (two teams at top, NOAT pinned at the bottom).
        var leftMargin = new MarginContainer { CustomMinimumSize = new Vector2(228, 0) };
        Margins(leftMargin, 12, 14);
        var leftCol = new VBoxContainer();
        leftCol.AddThemeConstantOverride("separation", 8);
        leftMargin.AddChild(leftCol);
        leftCol.AddChild(UiKit.MakeLabel("TEAMS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _teamTabs = new VBoxContainer();
        _teamTabs.AddThemeConstantOverride("separation", 8);
        leftCol.AddChild(_teamTabs);
        leftCol.AddChild(Spacer(vertical: true));
        leftCol.AddChild(new DiamondDivider());
        _noatTabHost = new VBoxContainer();
        leftCol.AddChild(_noatTabHost);
        body.AddChild(leftMargin);

        body.AddChild(Hairline(vertical: true));

        // Right: roster header + column header + scrolling rows.
        var rightCol = new VBoxContainer();
        rightCol.AddThemeConstantOverride("separation", 0);
        rightCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.SizeFlagsVertical = SizeFlags.ExpandFill;

        var head = PaddedRow(24, 16);
        var headRow = new HBoxContainer();
        headRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(headRow);
        var nameCol = new VBoxContainer();
        nameCol.AddThemeConstantOverride("separation", 2);
        // The name line is rebuilt each RebuildBody — it's either "{name} ✎" or the inline editor.
        _rosterNameRow = new HBoxContainer();
        _rosterNameRow.AddThemeConstantOverride("separation", 10);
        _rosterTeamSub = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _rosterTeamSub.AddThemeFontSizeOverride("font_size", 11);
        nameCol.AddChild(_rosterNameRow);
        nameCol.AddChild(_rosterTeamSub);
        headRow.AddChild(nameCol);
        headRow.AddChild(Spacer());
        _rosterPoints = StatCol(headRow, "TEAM PTS", DesignTokens.Data);
        _rosterCount = StatCol(headRow, "PILOTS", DesignTokens.TextHi);
        rightCol.AddChild(head);

        rightCol.AddChild(ColumnHeader());

        var scroll = new ScrollContainer();
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _rosterRows = new VBoxContainer();
        _rosterRows.AddThemeConstantOverride("separation", 0);
        _rosterRows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_rosterRows);
        rightCol.AddChild(scroll);

        body.AddChild(rightCol);

        // Third column: the sector pane (map thumbnail + Sector Intel + Garrison Control).
        body.AddChild(Hairline(vertical: true));
        var sectorMargin = new MarginContainer { CustomMinimumSize = new Vector2(320, 0) };
        Margins(sectorMargin, 16, 16);
        sectorMargin.AddChild(BuildSectorPane());
        body.AddChild(sectorMargin);

        return body;
    }

    // Right-hand sector pane: the map thumbnail (opens the picker), a Sector Intel stat grid, and
    // Garrison Control. Static skeleton here; UpdateSectorPane fills the live values each RebuildBody.
    private Control BuildSectorPane()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 16);

        // --- SECTOR MAP: header (label + HOST/LOCKED badge) + a clickable map card ---
        var mapSection = new VBoxContainer();
        mapSection.AddThemeConstantOverride("separation", 10);
        var mapHead = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        mapHead.AddChild(UiKit.MakeLabel("SECTOR MAP", UiKit.TextStyle.Label, DesignTokens.TextDim).With(l => l.SizeFlagsHorizontal = SizeFlags.ExpandFill));
        _mapHostBadge = Mono("LOCKED", DesignTokens.Text2);
        mapHead.AddChild(_mapHostBadge);
        mapSection.AddChild(mapHead);

        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        var cardSb = new StyleBoxFlat { BgColor = DesignTokens.Well, BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
        cardSb.SetCornerRadiusAll(0);
        cardSb.SetBorderWidthAll(1);
        card.AddThemeStyleboxOverride("panel", cardSb);
        card.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) OpenMapPicker(); };
        var cardCol = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        cardCol.AddThemeConstantOverride("separation", 0);
        card.AddChild(cardCol);

        _sectorMap = new SectorMapPreview { CustomMinimumSize = new Vector2(0, 150), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cardCol.AddChild(_sectorMap);

        var bar = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        Margins(bar, 12, 10);
        var barRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        barRow.AddThemeConstantOverride("separation", 8);
        var barNames = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        barNames.AddThemeConstantOverride("separation", 2);
        _mapName = UiKit.MakeLabel("—", UiKit.TextStyle.Label, DesignTokens.TextHi);
        _mapName.MouseFilter = MouseFilterEnum.Ignore;
        _mapMeta = Mono("—", DesignTokens.Text2);
        _mapMeta.AddThemeFontSizeOverride("font_size", 9);
        barNames.AddChild(_mapName);
        barNames.AddChild(_mapMeta);
        barRow.AddChild(barNames);
        _mapCta = Mono("VIEW ▸", DesignTokens.TeamAccent);
        _mapCta.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        barRow.AddChild(_mapCta);
        bar.AddChild(barRow);
        cardCol.AddChild(bar);
        mapSection.AddChild(card);
        col.AddChild(mapSection);

        // --- SECTOR INTEL: 2-col stat grid ---
        var intel = new VBoxContainer();
        intel.AddThemeConstantOverride("separation", 10);
        intel.AddChild(UiKit.MakeLabel("SECTOR INTEL", UiKit.TextStyle.Label, DesignTokens.TextDim));
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 1);
        grid.AddThemeConstantOverride("v_separation", 1);
        grid.AddChild(StatCell("MODE", out _siMode, DesignTokens.TextHi));
        grid.AddChild(StatCell("SECTOR", out _siSector, DesignTokens.Data));
        grid.AddChild(StatCell("GARRISONS", out _siGarrisons, DesignTokens.TextHi));
        grid.AddChild(StatCell("MAP SIZE", out _siSize, DesignTokens.Data));
        intel.AddChild(grid);
        col.AddChild(intel);

        // --- GARRISON CONTROL: proportional owner bar + per-faction node counts ---
        var garr = new VBoxContainer();
        garr.AddThemeConstantOverride("separation", 10);
        garr.AddChild(UiKit.MakeLabel("GARRISON CONTROL", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _garrisonBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 10), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _garrisonBar.AddThemeConstantOverride("separation", 2);
        garr.AddChild(_garrisonBar);
        _garrisonRows = new VBoxContainer();
        _garrisonRows.AddThemeConstantOverride("separation", 7);
        garr.AddChild(_garrisonRows);
        col.AddChild(garr);

        return col;
    }

    // The current/"next" map, resolved from the streamed catalog by SelectedMap (falls back to the
    // first advertised map, or null before the catalog arrives).
    private MapInfo? CurrentMap()
    {
        foreach (var m in _net.Maps)
            if (string.Equals(m.Name, _net.SelectedMap, StringComparison.OrdinalIgnoreCase))
                return m;
        return _net.Maps.Count > 0 ? _net.Maps[0] : null;
    }

    private void OpenMapPicker()
    {
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        MapPickerModal.Open(this, _net);
    }

    // Refresh the sector pane from the current map + host/team state (called from RebuildBody).
    private void UpdateSectorPane()
    {
        var cm = CurrentMap();
        bool host = _net.IsHost;
        _mapHostBadge.Text = host ? "HOST" : "LOCKED";
        _mapHostBadge.AddThemeColorOverride("font_color", host ? DesignTokens.Ok : DesignTokens.Text2);
        _mapCta.Text = host ? "CHANGE ▸" : "VIEW ▸";
        _mapCta.AddThemeColorOverride("font_color", host ? DesignTokens.TeamAccent : DesignTokens.Text2);

        _sectorMap.SetMap(cm?.Layout);
        _mapName.Text = cm?.Name ?? "NO MAP";
        _mapMeta.Text = cm != null ? $"{cm.SectorLabel} · {cm.GarrisonCount} GARRISONS · {cm.SizeLabel}" : "—";
        _siMode.Text = cm?.Mode ?? "—";
        _siSector.Text = cm?.SectorLabel ?? "—";
        _siGarrisons.Text = cm != null ? cm.GarrisonCount.ToString() : "—";
        _siSize.Text = cm?.SizeLabel ?? "—";

        // Garrison ownership counts across every sector base. Neutral (team != 0/1) is always 0 on
        // current maps — supported here for forward-compat but never fabricated.
        int t0 = 0, t1 = 0, neu = 0;
        if (cm != null)
            foreach (var s in cm.Layout.Sectors)
                foreach (var b in s.Bases)
                {
                    if (b.Team == 0)
                        t0++;
                    else if (b.Team == 1)
                        t1++;
                    else
                        neu++;
                }
        int total = t0 + t1 + neu;

        foreach (var c in _garrisonBar.GetChildren())
            c.QueueFree();
        AddBarSeg(t0, DesignTokens.Faction0);
        AddBarSeg(neu, DesignTokens.Text2);
        AddBarSeg(t1, DesignTokens.Faction1);
        if (total == 0)
            _garrisonBar.AddChild(new ColorRect { Color = DesignTokens.BorderLo, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });

        foreach (var c in _garrisonRows.GetChildren())
            c.QueueFree();
        _garrisonRows.AddChild(GarrisonRow(TeamName(0), t0, total, DesignTokens.Faction0));
        _garrisonRows.AddChild(GarrisonRow("NEUTRAL / UNCLAIMED", neu, total, DesignTokens.Text2));
        _garrisonRows.AddChild(GarrisonRow(TeamName(1), t1, total, DesignTokens.Faction1));
    }

    private void AddBarSeg(int count, Color color)
    {
        if (count <= 0)
            return;
        _garrisonBar.AddChild(new ColorRect
        {
            Color = color,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = count,
            MouseFilter = MouseFilterEnum.Ignore,
        });
    }

    private Control GarrisonRow(string label, int count, int total, Color color)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var left = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 8);
        left.AddChild(Diamond(color, false));
        left.AddChild(Mono(label, DesignTokens.TextHi).With(l => l.AddThemeFontSizeOverride("font_size", 11)));
        row.AddChild(left);
        var cnt = Mono($"{count}/{total}", color, HorizontalAlignment.Right);
        cnt.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(cnt);
        return row;
    }

    // One Sector Intel cell: caps caption over a value, on a recessed panel.
    private Control StatCell(string caption, out Label value, Color valueColor)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat { BgColor = DesignTokens.PanelDeep, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 13;
        sb.ContentMarginTop = sb.ContentMarginBottom = 11;
        panel.AddThemeStyleboxOverride("panel", sb);
        var c = new VBoxContainer();
        c.AddThemeConstantOverride("separation", 4);
        c.AddChild(Mono(caption, DesignTokens.TextDim).With(l => l.AddThemeFontSizeOverride("font_size", 9)));
        value = UiKit.MakeLabel("—", UiKit.TextStyle.Label, valueColor);
        value.AddThemeFontSizeOverride("font_size", 14);
        c.AddChild(value);
        panel.AddChild(c);
        return panel;
    }

    // ---- team-name inline editor -------------------------------------------

    // Only a real side (0/1), and only that side's LEADER — the earliest-joined pilot on it (the
    // roster's top row) — may rename it. Mirrors the server's gate (ClientHub MsgSetTeamName).
    private bool CanEditTeam(int team) => (team == 0 || team == 1) && LeaderOf(team) == _net.LocalClientId;

    // Rebuild the roster header's name line: display ("{NAME} ✎") or the inline editor. Called from
    // RebuildBody so it tracks tab selection, edit state, and streamed name changes.
    private void BuildRosterNameRow()
    {
        // Preserve in-progress text across an incidental rebuild (e.g. a roster churn mid-edit).
        string? prevDraft = _editingTeam == _selectedTeam && _nameEdit != null && GodotObject.IsInstanceValid(_nameEdit)
            ? _nameEdit.Text : null;
        foreach (var c in _rosterNameRow.GetChildren())
            c.QueueFree();

        bool editing = _editingTeam == _selectedTeam && _selectedTeam is 0 or 1;
        if (editing)
        {
            _nameEdit = new LineEdit
            {
                MaxLength = TeamNameMax,
                Text = prevDraft ?? TeamName(_selectedTeam),
                CustomMinimumSize = new Vector2(240, 0),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            _rosterNameRow.AddChild(_nameEdit);
            var ok = IconBtn("✓", SaveTeamName, ButtonVariant.Primary);
            var cancel = IconBtn("✕", CancelEditTeam, ButtonVariant.Secondary);
            _rosterNameRow.AddChild(ok);
            _rosterNameRow.AddChild(cancel);
            _nameEdit.GrabFocus();
            if (prevDraft == null)
                _nameEdit.SelectAll();
        }
        else
        {
            var name = UiKit.MakeLabel(TeamName(_selectedTeam), UiKit.TextStyle.Title, TeamColor(_selectedTeam));
            name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            _rosterNameRow.AddChild(name);
            if (CanEditTeam(_selectedTeam))
                _rosterNameRow.AddChild(IconBtn("✎", () => { _editingTeam = _selectedTeam; _bodyDirty = true; }, ButtonVariant.Icon));
        }
    }

    private static ChamferButton IconBtn(string text, Action onPressed, ButtonVariant variant)
    {
        var b = UiKit.MakeButton(text, onPressed, variant);
        b.CustomMinimumSize = new Vector2(32, 32);
        b.FocusMode = FocusModeEnum.None; // never steal keyboard focus from the name/chat inputs
        b.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return b;
    }

    private void SaveTeamName()
    {
        if (_editingTeam is not (0 or 1))
            return;
        string v = _nameEdit.Text.Trim().ToUpper();
        if (v.Length > TeamNameMax)
            v = v[..TeamNameMax];
        if (v.Length > 0)
            _net.SetTeamName((byte)_editingTeam, v); // server re-validates + rebroadcasts to everyone
        _editingTeam = -1;
        _bodyDirty = true;
    }

    private void CancelEditTeam()
    {
        _editingTeam = -1;
        _bodyDirty = true;
    }

    // Comms panel: channel tabs, scrolling log, and an input row scoped to the active channel.
    private Control BuildComms()
    {
        var wrap = new VBoxContainer { CustomMinimumSize = new Vector2(0, 200) };
        wrap.AddThemeConstantOverride("separation", 0);

        var tabs = PaddedRow(22, 8);
        var tabRow = new HBoxContainer();
        tabRow.AddThemeConstantOverride("separation", 8);
        tabRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        tabs.AddChild(tabRow);
        tabRow.AddChild(UiKit.MakeLabel("COMMS", UiKit.TextStyle.Label, DesignTokens.TextDim).With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter));
        // Group channel first (TEAM/NOAT — label set live in UpdateChannelButtons), then ALL.
        AddChannelButton(tabRow, 1);
        AddChannelButton(tabRow, 0);
        wrap.AddChild(tabs);

        var logWrap = PaddedRow(22, 6);
        logWrap.SizeFlagsVertical = SizeFlags.ExpandFill;
        _commsLog = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            ScrollFollowing = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _commsLog.AddThemeFontSizeOverride("normal_font_size", 15);
        logWrap.AddChild(_commsLog);
        wrap.AddChild(logWrap);

        var inputWrap = PaddedRow(22, 10);
        var inputRow = new HBoxContainer();
        inputRow.AddThemeConstantOverride("separation", 0);
        inputRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        inputWrap.AddChild(inputRow);
        _sendChip = new Label();
        _sendChip.AddThemeFontOverride("font", UiFonts.MonoMedium);
        _sendChip.AddThemeFontSizeOverride("font_size", 14);
        var chipStyle = new StyleBoxFlat { BgColor = DesignTokens.PanelFill, BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
        chipStyle.SetBorderWidthAll(1);
        chipStyle.ContentMarginLeft = chipStyle.ContentMarginRight = 12;
        chipStyle.ContentMarginTop = chipStyle.ContentMarginBottom = 8;
        _sendChip.AddThemeStyleboxOverride("normal", chipStyle);
        inputRow.AddChild(_sendChip);
        _commsInput = new LineEdit
        {
            PlaceholderText = "message your team…",
            MaxLength = 240,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        // NOTE: we deliberately do NOT use LineEdit.TextSubmitted. In Godot, handling Enter through
        // text_submitted leaves the LineEdit swallowing the next keystrokes until another Enter
        // (you'd have to press Enter twice to type a second message). Instead we intercept Enter in
        // _Input (below), before the LineEdit sees it, so it never enters that state.
        inputRow.AddChild(_commsInput);
        var send = UiKit.MakeButton("SEND ▸", () => OnCommsSubmit(_commsInput.Text), ButtonVariant.Primary);
        send.FocusMode = FocusModeEnum.None; // never take keyboard focus from the chat input
        inputRow.AddChild(send);
        wrap.AddChild(inputWrap);

        // Darker fill behind the whole comms strip (design's rgba(5,7,15,0.7) bar).
        var panel = new PanelContainer();
        var bg = new StyleBoxFlat { BgColor = new Color(DesignTokens.Void, 0.6f), AntiAliasing = false };
        bg.SetCornerRadiusAll(0);
        panel.AddThemeStyleboxOverride("panel", bg);
        panel.AddChild(wrap);
        return panel;
    }

    // ---- events -------------------------------------------------------------

    private void OnLobbyChanged() => _bodyDirty = true;

    private void OnChat(ChatLine line)
    {
        _messages.Add((line, DateTime.Now.ToString("HH:mm")));
        if (_messages.Count > Backlog)
            _messages.RemoveRange(0, _messages.Count - Backlog);
        if (Visible)
            RebuildComms();
    }

    private void OnCommsSubmit(string text)
    {
        text = text.Trim();
        _commsInput.Clear();
        if (text.Length > 0)
            // scope 1 = group (teammates, or fellow NOAT pilots while unassigned); scope 0 = all.
            _net.SendChat(text, teamOnly: _chatChannel == 1);
        // Keep the caret in the box so the player can keep typing without re-clicking. _Process
        // also re-asserts this whenever focus would otherwise be lost to nothing.
        _commsInput.GrabFocus();
    }

    private void OnJoin()
    {
        if (_selectedTeam is 0 or 1)
            _net.SetTeam((byte)_selectedTeam);
        else if (!IsNoat(MyTeamNow()))
            _net.SetTeam(GameNetClient.NoTeam); // LEAVE TEAM — stand back down to unassigned
    }

    // LAUNCH is the deploy action. Mid-match it promotes the pilot into the mandatory ship-select
    // hangar (via the Hud); pre-match it toggles ready, carrying the deploy intent through the
    // lobby→active flip so match-start flows straight into the hangar.
    private void OnLaunch()
    {
        if (IsNoat(MyTeamNow()))
            return; // must pick a side first (the button is disabled, but guard the deploy anyway)
        var hud = GetParent<Hud>();
        switch (_world.Phase)
        {
            case MatchPhase.Active:
                hud?.RequestDeploy();
                break;
            case MatchPhase.Lobby:
                bool nowReady = !(Me()?.Ready ?? false);
                _net.SetReady(nowReady);
                hud?.RequestDeploy(nowReady);
                break;
            // Ended: the button is disabled.
        }
    }

    // Send on Enter ourselves, intercepting it before the comms LineEdit's own handler runs — see
    // the note in BuildComms (routing Enter through LineEdit.TextSubmitted wedges the input until
    // the next Enter). Only when the comms box has focus, so Enter elsewhere is untouched.
    public override void _Input(InputEvent @event)
    {
        if (!Visible)
            return;
        if (@event is not InputEventKey { Pressed: true, Echo: false } k)
            return;

        // Team-name editor: Enter commits, Esc cancels — intercepted before the LineEdit's own
        // handler runs (same reason as the comms box below).
        if (_editingTeam >= 0 && _nameEdit != null && GodotObject.IsInstanceValid(_nameEdit) && _nameEdit.HasFocus())
        {
            if (k.Keycode is Key.Enter or Key.KpEnter)
            {
                SaveTeamName();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (k.Keycode == Key.Escape)
            {
                CancelEditTeam();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if ((k.Keycode == Key.Enter || k.Keycode == Key.KpEnter) && _commsInput.HasFocus())
        {
            OnCommsSubmit(_commsInput.Text);
            GetViewport().SetInputAsHandled();
        }
    }

    // Esc opens the escape menu. _UnhandledKeyInput so anything that owns Esc first — a
    // focused LineEdit, the chat box, the hangar, or the menus themselves — wins naturally.
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Visible || ShipLoadout.Active || EscapeMenu.Active || SettingsDialog.Active || MapPickerModal.Active
            || Chat.Capturing || _editingTeam >= 0)
            return;
        if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            EscapeMenu.Open(this, EscapeMenu.Context.Lobby);
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- per-frame ----------------------------------------------------------

    public override void _Process(double delta)
    {
        // The Lobby owns the screen whenever we're connected and not flying — pre-match,
        // post-match, and "joined but not yet deployed" mid-match. While flying, the flight HUD
        // and the ConnectLinkModal own it.
        //
        // But once the pilot has committed to the fight (deploy intent raised on first LAUNCH), the
        // hangar — not the team picker — owns the not-flying screen for the rest of the active match.
        // Without this the lobby would flash up during the death-cam beat / respawn gap on every
        // death (LocalShip is null but the hangar is held back for the blast beat). So: dying returns
        // you to the hangar, never back to the team picker mid-match. It reverts to the lobby when the
        // match ends (Hud clears DeployRequested on MatchPhase.Ended → post-match end screen).
        bool committed = _world.Phase == MatchPhase.Active && Hud.DeployRequested;
        // Let the pilot peek at the F3 sector map from the pre-launch lobby: this overlay is opaque
        // and would otherwise occlude the overview camera. Hiding it (SectorOverview stays Active, so
        // flight/lobby input stays neutralized) mirrors the spawn hangar's F3 peek; F3-close reveals
        // the lobby again next frame. See SectorOverview / ShipLoadout.
        bool show = _cm.State == ConnectionManager.ConnState.Connected && _world.LocalShip == null && !committed
            && !SectorOverview.Active;
        if (!show)
        {
            _wasVisible = Visible;
            Visible = false;
            return;
        }

        if (!_selectionInit)
        {
            // Start on the tab for the side you're on — the NOAT tab for a fresh (unassigned) joiner.
            _selectedTeam = IsNoat(MyTeamNow()) ? NoatTeam : MyTeamNow();
            _selectionInit = true;
            _bodyDirty = true;
        }
        if (!_wasVisible)
        {
            _bodyDirty = true;
            RebuildComms();
        }
        _wasVisible = true;
        Visible = true;
        _clockSecs += delta;

        if (_bodyDirty)
        {
            RebuildBody();
            _bodyDirty = false;
        }
        UpdateStatusBar();

        // The comms input is the only keyboard-focusable control in the lobby (every button is
        // FocusMode.None), so keep the caret in it whenever focus would otherwise be on nothing.
        // This is what lets the player keep typing after Enter or a SEND/tab click, regardless of
        // what transiently dropped focus. Suppressed while a higher overlay (hangar / sector map /
        // escape menu / settings) is up so it never fights (or types under) their controls.
        if (!ShipLoadout.Active && !SectorOverview.Active && !EscapeMenu.Active && !SettingsDialog.Active
            && !MapPickerModal.Active && _editingTeam < 0
            && GetViewport().GuiGetFocusOwner() == null)
            _commsInput.GrabFocus();
    }

    private void UpdateStatusBar()
    {
        _online.Text = $"● {_net.LobbyPlayers.Count} ONLINE";

        var cm = CurrentMap();
        string mapTitle = cm?.Name ?? "SKIRMISH";
        switch (_world.Phase)
        {
            case MatchPhase.Active:
                _phasePill.Configure("● LIVE", StatusPill.Kind.Danger, pulse: true);
                _matchTitle.Text = mapTitle;
                break;
            case MatchPhase.Ended:
                byte w = _world.Winner ?? 0;
                _phasePill.Configure("ENDED", StatusPill.Kind.Warn);
                _matchTitle.Text = $"TEAM {TeamName(w)} WINS";
                break;
            default:
                _phasePill.Configure("LOBBY", StatusPill.Kind.Accent);
                _matchTitle.Text = mapTitle;
                break;
        }
        _matchSub.Text = cm != null
            ? $"{cm.Mode} · {cm.SectorLabel} · {cm.GarrisonCount} GARRISONS"
            : "CONQUEST · UNCHARTED SECTOR";

        // Team names can be renamed at runtime — keep the score-bar labels in sync.
        _scoreName0.Text = TeamName(0);
        _scoreName1.Text = TeamName(1);

        _clock.Text = Fmt(_clockSecs);
        _score0.Text = _world.TeamScore(0).ToString();
        _score1.Text = _world.TeamScore(1).ToString();

        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        int myTeam = MyTeamNow();
        bool noatMe = IsNoat(myTeam);
        bool realTeam = _selectedTeam is 0 or 1;

        // JOIN {TEAM} on a real-team tab; on the NOAT tab it becomes LEAVE TEAM (stand back down)
        // when you're currently on a side, and is inert when you're already unassigned.
        if (realTeam)
        {
            bool mine = _selectedTeam == myTeam;
            _joinBtn.AccentOverride = DesignTokens.Faction(_selectedTeam);
            _joinBtn.Text = mine ? $"◆ ON {TeamName(_selectedTeam)}" : $"JOIN {TeamName(_selectedTeam)}";
            _joinBtn.Disabled = mine;
        }
        else
        {
            _joinBtn.AccentOverride = null;
            _joinBtn.Text = noatMe ? "UNASSIGNED" : "LEAVE TEAM";
            _joinBtn.Disabled = noatMe;
        }
        _joinBtn.QueueRedraw();

        // LAUNCH: deploy mid-match, ready/stand-down pre-match, disabled post-match. Always blocked
        // while NOAT — you must pick a side before you can deploy.
        if (noatMe && _world.Phase != MatchPhase.Ended)
        {
            _launchBtn.Text = "PICK A TEAM";
            _launchBtn.Disabled = true;
        }
        else
        {
            switch (_world.Phase)
            {
                case MatchPhase.Active:
                    _launchBtn.Text = "LAUNCH ▸";
                    _launchBtn.Disabled = false;
                    break;
                case MatchPhase.Ended:
                    _launchBtn.Text = "MATCH OVER";
                    _launchBtn.Disabled = true;
                    break;
                default:
                    _launchBtn.Text = (Me()?.Ready ?? false) ? "STAND DOWN" : "LAUNCH ▸";
                    _launchBtn.Disabled = false;
                    break;
            }
        }
        _launchBtn.QueueRedraw();
    }

    // ---- body rebuild -------------------------------------------------------

    private void RebuildBody()
    {
        // Team tab cards.
        foreach (var c in _teamTabs.GetChildren())
            c.QueueFree();
        for (byte t = 0; t < 2; t++)
            _teamTabs.AddChild(TeamTab(t));

        foreach (var c in _noatTabHost.GetChildren())
            c.QueueFree();
        _noatTabHost.AddChild(TeamTab(NoatTeam));

        // Roster header (name + rename affordance / inline editor).
        BuildRosterNameRow();
        int count = CountFor(_selectedTeam);
        _rosterTeamSub.Text = _selectedTeam == NoatTeam ? "Not on a team — spectators & unassigned" : $"{count} pilot{(count == 1 ? "" : "s")}";
        _rosterPoints.Text = _selectedTeam == NoatTeam ? "—" : _world.TeamScore((byte)_selectedTeam).ToString();
        _rosterCount.Text = count.ToString();

        // Roster rows. The NOAT tab lists everyone who hasn't picked a side yet.
        foreach (var c in _rosterRows.GetChildren())
            c.QueueFree();
        bool noatTab = _selectedTeam == NoatTeam;
        // Order by client id so the earliest-joined pilot — the team leader who owns the rename
        // affordance — is literally the top row.
        var roster = new List<LobbyPlayer>();
        foreach (var p in _net.LobbyPlayers)
            if (noatTab ? IsNoat(p.Team) : p.Team == _selectedTeam)
                roster.Add(p);
        roster.Sort((a, b) => a.Id.CompareTo(b.Id));
        bool any = roster.Count > 0;
        foreach (var p in roster)
            _rosterRows.AddChild(RosterRow(p));
        if (!any)
            _rosterRows.AddChild(EmptyNote(noatTab ? "No unassigned pilots." : "Waiting for pilots…"));

        // Keep the group comms channel labelled for your current side (TEAM ⇄ NOAT on join/leave).
        UpdateChannelButtons();

        // Refresh the right-hand sector pane (map/host/garrison state).
        UpdateSectorPane();
    }

    // A clickable team tab card (a Button with design styleboxes + non-interactive content on top).
    private Button TeamTab(int team)
    {
        bool selected = team == _selectedTeam;
        Color accent = TeamColor(team);
        var btn = new Button { CustomMinimumSize = new Vector2(0, 62), ClipContents = true, FocusMode = FocusModeEnum.None };
        foreach (string s in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            btn.AddThemeStyleboxOverride(s, TabStyle(accent, selected));
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            btn.AddThemeColorOverride(c, Colors.Transparent);
        int captured = team;
        btn.Pressed += () =>
        {
            if (_selectedTeam == captured)
                return;
            _selectedTeam = captured;
            _bodyDirty = true;
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        };

        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Margins(pad, 13, 10);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 7);
        pad.AddChild(col);

        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 9);
        top.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(Diamond(accent, team == NoatTeam));
        var name = UiKit.MakeLabel(TeamName(team), UiKit.TextStyle.Label, DesignTokens.TextHi);
        name.AddThemeFontSizeOverride("font_size", 14);
        name.MouseFilter = MouseFilterEnum.Ignore;
        top.AddChild(name);
        top.AddChild(Spacer());
        string bigNum = team == NoatTeam ? CountFor(team).ToString() : _world.TeamScore((byte)team).ToString();
        var num = UiKit.MakeLabel(bigNum, UiKit.TextStyle.Data, accent);
        num.AddThemeFontSizeOverride("font_size", 18);
        num.MouseFilter = MouseFilterEnum.Ignore;
        top.AddChild(num);
        col.AddChild(top);

        var sub = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        sub.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (team == NoatTeam)
        {
            sub.AddChild(Mono("NOT ON A TEAM", DesignTokens.TextDim));
        }
        else
        {
            sub.AddChild(Mono($"{CountFor(team)} PILOTS", DesignTokens.Text2));
            sub.AddChild(Spacer());
            sub.AddChild(Mono("— KILLS", DesignTokens.Text2)); // PLACEHOLDER: no per-team kills on the wire
        }
        col.AddChild(sub);

        btn.AddChild(pad);
        return btn;
    }

    private Control RosterRow(LobbyPlayer p)
    {
        bool isMe = p.Id == _net.LocalClientId;
        Color team = TeamColor(p.Team);
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = isMe ? new Color(team, 0.10f) : Colors.Transparent, BorderColor = DesignTokens.BorderLo, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthBottom = 1;
        if (isMe)
            sb.BorderWidthLeft = 2; // accent bar marking "me"
        sb.BorderColor = isMe ? team : DesignTokens.BorderLo;
        sb.ContentMarginLeft = sb.ContentMarginRight = 24;
        sb.ContentMarginTop = sb.ContentMarginBottom = 11;
        panel.AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        row.AddChild(Mono(isMe ? "◆" : "▸", isMe ? team : DesignTokens.TextDim).With(l => l.CustomMinimumSize = new Vector2(20, 0)));

        // CALLSIGN (+ YOU badge). PLACEHOLDER: commander/CMDR badge omitted (no such state on the wire).
        var nameCell = new HBoxContainer();
        nameCell.AddThemeConstantOverride("separation", 8);
        Cell(nameCell, 1.6f);
        string who = string.IsNullOrEmpty(p.Name) ? $"Pilot{p.Id}" : p.Name;
        nameCell.AddChild(UiKit.MakeLabel(who, UiKit.TextStyle.Body, isMe ? team : DesignTokens.TextHi));
        if (isMe)
            nameCell.AddChild(Badge("YOU", team));
        row.AddChild(nameCell);

        // K/D/EJ/PTS — PLACEHOLDER columns (not carried by MsgLobbyState). PTS right-aligned.
        row.AddChild(Cell(Mono("—", DesignTokens.Data, HorizontalAlignment.Center), 0.5f));
        row.AddChild(Cell(Mono("—", DesignTokens.Data, HorizontalAlignment.Center), 0.5f));
        row.AddChild(Cell(Mono("—", DesignTokens.Data, HorizontalAlignment.Center), 0.5f));
        row.AddChild(Cell(Mono("—", DesignTokens.Data, HorizontalAlignment.Right), 0.7f));
        return panel;
    }

    private Control ColumnHeader()
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccent, 0.04f), BorderColor = DesignTokens.BorderLo, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthBottom = 1;
        sb.ContentMarginLeft = sb.ContentMarginRight = 24;
        sb.ContentMarginTop = sb.ContentMarginBottom = 8;
        panel.AddThemeStyleboxOverride("panel", sb);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        row.AddChild(Lbl("", 20));
        row.AddChild(Cell(Lbl("CALLSIGN"), 1.6f));
        row.AddChild(Cell(Lbl("K", align: HorizontalAlignment.Center), 0.5f));
        row.AddChild(Cell(Lbl("D", align: HorizontalAlignment.Center), 0.5f));
        row.AddChild(Cell(Lbl("EJ", align: HorizontalAlignment.Center), 0.5f));
        row.AddChild(Cell(Lbl("PTS", align: HorizontalAlignment.Right), 0.7f));
        return panel;
    }

    // ---- comms rebuild ------------------------------------------------------

    private void AddChannelButton(HBoxContainer parent, int channel)
    {
        var b = UiKit.MakeButton(channel == 0 ? "ALL" : "TEAM", null, ButtonVariant.Secondary);
        b.CustomMinimumSize = new Vector2(64, 28);
        b.FocusMode = FocusModeEnum.None;
        b.Pressed += () =>
        {
            _chatChannel = channel;
            UpdateChannelButtons();
            RebuildComms();
        };
        _channelBtns[channel] = b;
        parent.AddChild(b);
    }

    private void UpdateChannelButtons()
    {
        // The group channel's label/colour follow your own side (TEAM, or NOAT while unassigned).
        if (_channelBtns[1] is ChamferButton grp)
            grp.Text = GroupLabel();
        for (int i = 0; i < _channelBtns.Length; i++)
        {
            if (_channelBtns[i] is not ChamferButton b)
                continue;
            b.Variant = i == _chatChannel ? ButtonVariant.Primary : ButtonVariant.Secondary;
            b.QueueRedraw();
        }
        // Input chip + placeholder track the active channel.
        bool group = _chatChannel == 1;
        _sendChip.Text = group ? GroupLabel() : "ALL";
        _sendChip.AddThemeColorOverride("font_color", group ? GroupColor() : DesignTokens.TextHi);
        _commsInput.PlaceholderText = group
            ? $"message {(IsNoat(MyTeamNow()) ? "the unassigned" : "your team")}…"
            : "message all pilots…";
    }

    private void RebuildComms()
    {
        var sb = new StringBuilder();
        foreach (var (line, time) in _messages)
        {
            if (!ChannelShows(line))
                continue;
            string stamp = $"[color=#{DesignTokens.TextDim.ToHtml(false)}]{time}[/color]";
            if (string.IsNullOrEmpty(line.Name))
            {
                sb.Append($"{stamp} [color=#{DesignTokens.Text2.ToHtml(false)}]◆ {Escape(line.Text)}[/color]\n");
                continue;
            }
            Color nameCol = TeamColor(line.FromTeam);
            string tag = line.Scope == 1
                ? $"[color=#{DesignTokens.Text2.ToHtml(false)}]\\[{(IsNoat(line.FromTeam) ? "noat" : "team")}][/color] "
                : "";
            sb.Append($"{stamp} {tag}[color=#{nameCol.ToHtml(false)}]{Escape(line.Name)}[/color]: [color=#{DesignTokens.TextHi.ToHtml(false)}]{Escape(line.Text)}[/color]\n");
        }
        _commsLog.Text = sb.ToString();
    }

    // Which log lines the active channel shows. ALL shows everything; the group channel shows
    // scope-1 lines (your teammates / fellow NOAT pilots) plus locally-generated system lines.
    private bool ChannelShows(ChatLine line) =>
        _chatChannel == 1 ? line.Scope == 1 || string.IsNullOrEmpty(line.Name) : true;

    // ---- small helpers ------------------------------------------------------

    private LobbyPlayer? Me()
    {
        foreach (var p in _net.LobbyPlayers)
            if (p.Id == _net.LocalClientId)
                return p;
        return null;
    }

    // The team's leader: the earliest-joined (lowest client-id) pilot on that side, or -1 if empty.
    // The leader is the roster's top row and the only pilot allowed to rename the team.
    private int LeaderOf(int team)
    {
        int leader = -1;
        foreach (var p in _net.LobbyPlayers)
            if (p.Team == team && (leader == -1 || p.Id < leader))
                leader = p.Id;
        return leader;
    }

    private int CountFor(int team)
    {
        int n = 0;
        foreach (var p in _net.LobbyPlayers)
            if (team == NoatTeam ? IsNoat(p.Team) : p.Team == team)
                n++;
        return n;
    }

    // Team names are now streamed (server-held, editable via the roster header). Reads live from the
    // net client so a rename by any pilot shows everywhere; NOAT is the client-only pseudo-team.
    private string TeamName(int team) => team switch { 0 => _net.Team0Name, 1 => _net.Team1Name, _ => "NOAT" };

    private static Color TeamColor(int team) => IsNoat(team) ? DesignTokens.Text2 : DesignTokens.Faction(team);

    // Anything that isn't a real side (0/1) is NOAT — covers both the wire sentinel
    // (GameNetClient.NoTeam) and the UI tab id (NoatTeam).
    private static bool IsNoat(int team) => team != 0 && team != 1;

    // The local pilot's CURRENT team. MyTeam is only set at Welcome, so after a SetTeam the
    // authoritative value is the roster row the server just re-broadcast — read that first.
    private int MyTeamNow() => Me() is LobbyPlayer m ? m.Team : _net.MyTeam;

    private static string Fmt(double secs)
    {
        int s = (int)secs;
        return $"{s / 60:00}:{s % 60:00}";
    }

    private static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("[", "[lb]");

    private static Label Mono(string text, Color color, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, color);
        l.AddThemeFontSizeOverride("font_size", 13);
        l.HorizontalAlignment = align;
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    private static Label Lbl(string text, float minWidth = 0, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Label, DesignTokens.TextDim);
        l.AddThemeFontSizeOverride("font_size", 10);
        l.HorizontalAlignment = align;
        if (minWidth > 0)
            l.CustomMinimumSize = new Vector2(minWidth, 0);
        return l;
    }

    private static Control Cell(Control c, float ratio)
    {
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        c.SizeFlagsStretchRatio = ratio;
        return c;
    }

    private static Label Badge(string text, Color color)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, DesignTokens.Void);
        l.AddThemeFontSizeOverride("font_size", 9);
        var sb = new StyleBoxFlat { BgColor = color, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 5;
        sb.ContentMarginTop = sb.ContentMarginBottom = 1;
        l.AddThemeStyleboxOverride("normal", sb);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return l;
    }

    private static Control Diamond(Color color, bool hollow)
    {
        // A rotated square would need a custom draw; the ◆ glyph reads as the design's team diamond.
        var l = new Label { Text = hollow ? "◇" : "◆", MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeFontOverride("font", UiFonts.Mono);
        l.AddThemeFontSizeOverride("font_size", 12);
        l.AddThemeColorOverride("font_color", color);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return l;
    }

    private static Control EmptyNote(string text)
    {
        var m = new MarginContainer();
        Margins(m, 24, 20);
        m.AddChild(UiKit.MakeLabel(text, UiKit.TextStyle.Body, DesignTokens.TextDim));
        return m;
    }

    private Label StatCol(HBoxContainer parent, string caption, Color valueColor)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        col.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        col.AddChild(Mono(caption, DesignTokens.TextDim).With(l => l.AddThemeFontSizeOverride("font_size", 10)));
        var v = UiKit.MakeLabel("—", UiKit.TextStyle.Data, valueColor);
        v.AddThemeFontSizeOverride("font_size", 20);
        col.AddChild(v);
        var wrap = new MarginContainer();
        wrap.AddThemeConstantOverride("margin_left", 26);
        wrap.AddChild(col);
        parent.AddChild(wrap);
        return v;
    }

    private Label Chip(string text)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Label, DesignTokens.Void);
        l.AddThemeFontSizeOverride("font_size", 12);
        var sb = new StyleBoxFlat { BgColor = DesignTokens.TeamAccent, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = 16;
        sb.ContentMarginTop = sb.ContentMarginBottom = 6;
        l.AddThemeStyleboxOverride("normal", sb);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return l;
    }

    private static StyleBoxFlat TabStyle(Color accent, bool selected)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = selected ? new Color(accent, 0.12f) : DesignTokens.PanelFill,
            BorderColor = selected ? accent : DesignTokens.BorderLo,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        if (selected)
            sb.BorderWidthLeft = 3;
        return sb;
    }

    // A chrome bar: horizontal + vertical padding via a filled panel stylebox (so it draws a
    // background) rather than a bare MarginContainer.
    private static PanelContainer BarPanel(int h, int v, Color bg)
    {
        var p = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = bg, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.ContentMarginLeft = sb.ContentMarginRight = h;
        sb.ContentMarginTop = sb.ContentMarginBottom = v;
        p.AddThemeStyleboxOverride("panel", sb);
        return p;
    }

    // A row with horizontal + vertical padding, sized to its content height.
    private static MarginContainer PaddedRow(int h, int v)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", h);
        m.AddThemeConstantOverride("margin_right", h);
        m.AddThemeConstantOverride("margin_top", v);
        m.AddThemeConstantOverride("margin_bottom", v);
        return m;
    }

    private static void Margins(MarginContainer m, int h, int v)
    {
        m.AddThemeConstantOverride("margin_left", h);
        m.AddThemeConstantOverride("margin_right", h);
        m.AddThemeConstantOverride("margin_top", v);
        m.AddThemeConstantOverride("margin_bottom", v);
    }

    private static Control Spacer(bool vertical = false)
    {
        var c = new Control { MouseFilter = MouseFilterEnum.Ignore };
        if (vertical)
            c.SizeFlagsVertical = SizeFlags.ExpandFill;
        else
            c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return c;
    }

    private static Control Hairline(bool vertical = false)
    {
        var r = new ColorRect { Color = DesignTokens.BorderHi, MouseFilter = MouseFilterEnum.Ignore };
        if (vertical)
        {
            r.CustomMinimumSize = new Vector2(1, 0);
            r.SizeFlagsVertical = SizeFlags.ExpandFill;
        }
        else
        {
            r.CustomMinimumSize = new Vector2(0, 1);
        }
        return r;
    }
}

// Tiny fluent helper so the builders above can tweak a freshly-made control inline (UiKit keeps
// its own copy private).
internal static class LobbyControlExt
{
    public static T With<T>(this T node, Action<T> configure)
        where T : Node
    {
        configure(node);
        return node;
    }
}
