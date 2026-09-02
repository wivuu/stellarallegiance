using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  Scoreboard.cs — the match scoreboard, in two modes over one ledger
//
//  Both modes render WorldRenderer.MatchStats (the MsgMatchStats ledger) with the shared
//  RosterCells primitives, so the board and the lobby roster can't drift apart:
//
//   • LIVE (F5 in flight) — a centred bracket panel over the running sector, both teams side by
//     side, READ-ONLY. Deliberately NOT part of InputGate.FlightInputFree and it never touches
//     Input.MouseMode: the server replays held input, so freezing the client would leave the pilot
//     thrusting blind. Adds a SHIP/STATUS column that follows fog of war — an enemy your team can't
//     see reads "· · ·"; K/D/EJ/PTS are match RECORD and always shown.
//   • POST-MATCH — a full-screen result screen the Hud auto-opens on the Active→Ended edge. That
//     edge fires while you're still flying (the sim holds the ships for ~6s), with the lobby hidden
//     and the cursor captured, so this mode frees the cursor itself on open. Sortable columns, a
//     team filter, the team-summary comparison and the Top Gun callout.
//
//  Created ONCE by the Hud (last child, so it draws above Lobby + Chat but under the ConnectLayer /
//  ModalHost canvas layers) and visibility-toggled, never freed — the sort column, direction and
//  team filter survive an F5 toggle. Rebuilds only when the ledger/roster changes or the pilot
//  sorts/filters; the live SHIP/STATUS labels refresh on their own slow timer.
// =====================================================================
public partial class Scoreboard : Control
{
    public enum Mode
    {
        Live,
        PostMatch,
    }

    // True while EITHER mode is up. The Lobby's Esc handler yields to it (post-match sits on top of
    // the lobby); the flight-input gate deliberately does NOT consult it — see the header.
    public static bool Active { get; private set; }

    // True only while the POST-MATCH board is up — i.e. while this overlay owns the cursor and Esc.
    // ShipController's `_Input` consults it exactly like ShipLoadout.Active/EscapeMenu.Active, so a
    // click on this board doesn't re-capture the cursor for mouse-look (the pilot is usually still
    // flying at that moment — see trap 3). The LIVE board must never appear in such a gate.
    public static bool PostMatchActive { get; private set; }

    private WorldRenderer _world = null!;
    private GameNetClient _net = null!;
    private DefRegistry _defs = null!;
    private ConnectionManager _cm = null!;

    private Mode _mode = Mode.Live;

    // Which board is up (meaningful only while Active). The Hud reads it to decide whether a board
    // has lost its subject — a LIVE board is pointless once the match stops being live.
    public Mode CurrentMode => _mode;

    private Control _body = null!; // torn down + rebuilt on _dirty; sort/filter live in the fields below
    private ColorRect _scrim = null!;
    private bool _dirty = true;

    // Post-match view state, kept across a close/reopen. Default sort is PTS descending (the mock's
    // resting state); clicking the active column flips it, a new column starts descending except
    // CALLSIGN, which reads better ascending first.
    private MatchStatsStore.SortKey _sortKey = MatchStatsStore.SortKey.Points;
    private bool _sortDesc = true;
    private byte _filterTeam = MatchStatsStore.AllTeams;

    // Live SHIP/STATUS refresh. The hull a pilot is flying changes far faster than the ledger, but
    // it's only a handful of labels — so they're re-texted in place on this cadence instead of
    // rebuilding the board every frame.
    private const double LiveRefreshSec = 0.25;
    private double _liveRefreshAccum;
    private readonly List<(int ClientId, Label Text, Control BadgeHost)> _liveCells = new();

    // Match duration, derived client-side: the wire carries no match clock, so we edge-detect the
    // Lobby→Active flip and count server ticks from there, latching the total when the match ends.
    // The edge only counts if we were WATCHING the lobby when it fired — `_sawLiveLobby` requires a
    // real streamed tick under the Lobby phase, which a mid-match joiner never sees (their very first
    // frames read the MatchClock's pre-snapshot Lobby default at tick 0, and would otherwise look
    // like a match start). Without that edge the board honestly reads "--:--".
    private uint _matchStartTick;
    private bool _haveStartTick;
    private bool _sawLiveLobby;
    private uint _matchEndTick;
    private MatchPhase _prevPhase = MatchPhase.Lobby;

    public void Init(WorldRenderer world, GameNetClient net, DefRegistry defs, ConnectionManager cm)
    {
        _world = world;
        _net = net;
        _defs = defs;
        _cm = cm;

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore; // Open() raises this to Stop for the post-match mode
        UiTheme.Apply(this);
        UiFonts.EnsureLoaded();
        Visible = false;

        _scrim = new ColorRect { MouseFilter = MouseFilterEnum.Ignore };
        _scrim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_scrim);

        _body = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _body.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_body);

        _net.MatchStatsChanged += MarkDirty;
        _net.LobbyChanged += MarkDirty;
    }

    public override void _ExitTree()
    {
        _net.MatchStatsChanged -= MarkDirty;
        _net.LobbyChanged -= MarkDirty;
    }

    private void MarkDirty() => _dirty = true;

    // ---- open / close -------------------------------------------------------

    public void Open(Mode mode)
    {
        _mode = mode;
        Active = true;
        PostMatchActive = mode == Mode.PostMatch;
        Visible = true;
        _dirty = true;
        // Live is a pure read-out laid over the flight view: it must not swallow the mouse (the
        // pilot is still steering with it). Post-match is a screen you click, and it opens while the
        // cursor is still captured for flight — free it here, since the lobby that normally owns the
        // cursor is hidden at the Active→Ended edge.
        if (mode == Mode.PostMatch)
        {
            MouseFilter = MouseFilterEnum.Stop;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else
        {
            MouseFilter = MouseFilterEnum.Ignore;
        }
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
    }

    public void Close()
    {
        if (!Active)
            return;
        Active = false;
        PostMatchActive = false;
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
    }

    public void Toggle(Mode mode)
    {
        // Same key, two boards: pressing F5 while the OTHER mode is up switches to this one rather
        // than closing (the post-match board auto-opens, so F5 there means "show me the live view").
        if (Active && _mode == mode)
            Close();
        else
            Open(mode);
    }

    // ---- per-frame ----------------------------------------------------------

    public override void _Process(double delta)
    {
        // Runs even while hidden — this is where the match clock's Lobby→Active edge is caught, and
        // a pilot can open the board later expecting a sane duration.
        var phase = _world.Phase;
        if (phase == MatchPhase.Lobby && _world.ServerTick > 0)
            _sawLiveLobby = true; // a streamed lobby tick — we're here for whatever match starts next
        if (phase == MatchPhase.Active && _prevPhase != MatchPhase.Active)
        {
            _matchStartTick = _world.ServerTick;
            _haveStartTick = _sawLiveLobby;
            _sawLiveLobby = false;
            _matchEndTick = 0; // a new match is running — the previous one's final length is spent
        }
        if (phase == MatchPhase.Ended && _prevPhase == MatchPhase.Active)
            _matchEndTick = _world.ServerTick;
        _prevPhase = phase;

        if (!Visible)
            return;

        if (_dirty)
        {
            Rebuild();
            _dirty = false;
        }

        if (_mode != Mode.Live)
            return;
        // Live only: re-text the hull/status cells (and the clock) on a slow cadence. Everything else
        // on the board is match record and only moves when a fresh ledger arrives.
        _liveRefreshAccum += delta;
        if (_liveRefreshAccum < LiveRefreshSec)
            return;
        _liveRefreshAccum = 0;
        RefreshLiveCells();
        if (_clock != null && GodotObject.IsInstanceValid(_clock))
            _clock.Text = MatchDuration();
    }

    // Esc closes the POST-MATCH board (the live one is closed with F5 — Esc in flight belongs to the
    // two-step escape menu). _UnhandledKeyInput so a focused control or an open modal wins first.
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Active || _mode != Mode.PostMatch || EscapeMenu.Active || SettingsDialog.Active)
            return;
        if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- shared data helpers ------------------------------------------------

    private string TeamName(int team) =>
        team switch
        {
            0 => _net.Team0Name,
            1 => _net.Team1Name,
            _ => "ALL PILOTS",
        };

    private static Color TeamColor(int team) => team is 0 or 1 ? DesignTokens.Faction(team) : DesignTokens.Text2;

    private MatchStatsStore Stats => _world.MatchStats;

    // The local pilot's side, for the "me" row highlight and the fog gate. Falls back to the lobby
    // pick so the board reads correctly before/after a sortie too.
    private byte MyTeam() => _world.LocalTeam ?? _net.MyTeam;

    // mm:ss of the match so far (or its final length once ended); "--:--" when we never saw the start.
    private string MatchDuration()
    {
        if (!_haveStartTick)
            return "--:--";
        // Latched end tick wins whenever we have one — the board outlives the Ended phase (the sim
        // drops back to Lobby ~6s after the win while the result screen is still up), so gating this
        // on Phase == Ended would let the "final" duration start counting again over the lobby.
        uint now = _matchEndTick != 0 ? _matchEndTick : _world.ServerTick;
        int secs = (int)Math.Max(0, (now - (long)_matchStartTick) * FlightModel.Dt);
        return $"{secs / 60:00}:{secs % 60:00}";
    }

    private string MapName() => _net.CurrentMap?.Name ?? "UNCHARTED SECTOR";

    // ---- shared cell builders ----------------------------------------------

    // A bordered key cap ("F5" / "ESC").
    private static Control KeyCap(string key)
    {
        var l = UiKit.MakeLabel(key, UiKit.TextStyle.Data, DesignTokens.Data);
        l.AddThemeFontSizeOverride("font_size", 12);
        var sb = new StyleBoxFlat
        {
            BgColor = Colors.Transparent,
            BorderColor = new Color(120f / 255f, 190f / 255f, 255f / 255f, 0.40f),
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.SetBorderWidthAll(1);
        sb.ContentMarginLeft = sb.ContentMarginRight = 8;
        sb.ContentMarginTop = sb.ContentMarginBottom = 3;
        l.AddThemeStyleboxOverride("normal", sb);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return l;
    }

    private static Label Caption(string text, int size = 11) =>
        RosterCells.Mono(text, DesignTokens.TextDim).With(l => l.AddThemeFontSizeOverride("font_size", size));

    private static Label Num(string text, Color color, int size)
    {
        var l = UiKit.MakeLabel(text, UiKit.TextStyle.Data, color);
        l.AddThemeFontSizeOverride("font_size", size);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    // "{name} n — n {name}" garrison-demolition tally in faction colours, at the caller's number size.
    private Control GarrisonTally(int numSize, int dashSize, int nameSize)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        row.AddChild(
            UiKit
                .MakeLabel(TeamName(0), UiKit.TextStyle.Label, DesignTokens.Faction0)
                .With(l => l.AddThemeFontSizeOverride("font_size", nameSize))
                .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
        );
        row.AddChild(Num(Stats.Garrisons(0).ToString(), DesignTokens.Faction0, numSize));
        row.AddChild(Num("—", DesignTokens.TextDim, dashSize));
        row.AddChild(Num(Stats.Garrisons(1).ToString(), DesignTokens.Faction1, numSize));
        row.AddChild(
            UiKit
                .MakeLabel(TeamName(1), UiKit.TextStyle.Label, DesignTokens.Faction1)
                .With(l => l.AddThemeFontSizeOverride("font_size", nameSize))
                .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
        );
        return row;
    }

    // The callsign cell: name (faction-coloured when it's you) + the commander star + a YOU / LEFT tag.
    private Control CallsignCell(in MatchStatsStore.PilotStat p, bool isMe, float ratio)
    {
        var cell = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        cell.AddThemeConstantOverride("separation", 7);
        RosterCells.Cell(cell, ratio);
        cell.AddChild(UiKit.MakeLabel(p.Name, UiKit.TextStyle.Body, isMe ? TeamColor(p.Team) : DesignTokens.TextHi));
        if (p.Team is 0 or 1 && p.ClientId == _net.CommanderIdOf(p.Team))
            cell.AddChild(
                UiKit
                    .MakeLabel("★", UiKit.TextStyle.Body, DesignTokens.CmdrGold)
                    .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
            );
        if (isMe)
            cell.AddChild(RosterCells.Badge("YOU", TeamColor(p.Team)));
        else if (!p.Connected)
            cell.AddChild(RosterCells.TintBadge("LEFT", DesignTokens.TextDim, new Color(DesignTokens.TextDim, 0.16f)));
        return cell;
    }

    // The K / D / EJ / PTS run, shared by both modes (only the column ratios differ).
    private static void AddNumberCells(
        HBoxContainer row,
        in MatchStatsStore.PilotStat p,
        bool isMe,
        Color team,
        float n,
        float pts
    )
    {
        Color ptsColor = isMe ? team : DesignTokens.Data;
        row.AddChild(
            RosterCells.Cell(RosterCells.Mono(p.Kills.ToString(), DesignTokens.Data, HorizontalAlignment.Center), n)
        );
        row.AddChild(
            RosterCells.Cell(RosterCells.Mono(p.Deaths.ToString(), DesignTokens.Data, HorizontalAlignment.Center), n)
        );
        row.AddChild(
            RosterCells.Cell(RosterCells.Mono(p.Ejects.ToString(), DesignTokens.Data, HorizontalAlignment.Center), n)
        );
        row.AddChild(RosterCells.Cell(RosterCells.Mono(p.Points.ToString(), ptsColor, HorizontalAlignment.Right), pts));
    }

    // The row gutter: a filled diamond for you, a dim caret for everyone else.
    private static Control Gutter(bool isMe, Color team, float width) =>
        RosterCells
            .Mono(isMe ? "◆" : "▸", isMe ? team : DesignTokens.TextDim)
            .With(l => l.CustomMinimumSize = new Vector2(width, 0));

    // ---- rebuild ------------------------------------------------------------

    private Label? _clock;

    private void Rebuild()
    {
        foreach (var c in _body.GetChildren())
            c.QueueFree();
        _liveCells.Clear();
        _clock = null;
        if (_mode == Mode.Live)
        {
            _scrim.Color = new Color(DesignTokens.Void, 0.55f);
            _body.AddChild(BuildLive());
        }
        else
        {
            // Opaque: the post-match screen is a full page, and it stacks on the Lobby — at anything
            // less the lobby's own roster reads straight through this one's.
            _scrim.Color = DesignTokens.Void;
            _body.AddChild(BuildPostMatch());
        }
    }

    // ---- LIVE mode ----------------------------------------------------------

    private Control BuildLive()
    {
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var panel = new BracketPanel
        {
            CustomMinimumSize = new Vector2(1240, 600),
            FillOverride = new Color(8f / 255f, 14f / 255f, 24f / 255f, 0.88f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        center.AddChild(panel);

        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 0);
        panel.AddChild(col);

        // --- title row: brand mark, LIVE pill, clock, garrison tally, map, F5 hint ---
        var titleWrap = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        titleWrap.AddThemeConstantOverride("margin_bottom", 14);
        var title = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        title.AddThemeConstantOverride("separation", 14);
        titleWrap.AddChild(title);
        title.AddChild(
            new ColorRect
            {
                Color = DesignTokens.TeamAccent,
                CustomMinimumSize = new Vector2(12, 12),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore,
            }
        );
        var word = UiKit.MakeLabel("SCOREBOARD", UiKit.TextStyle.Label, DesignTokens.TextHi);
        word.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.SairaBold, 3));
        word.AddThemeFontSizeOverride("font_size", 16);
        word.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        title.AddChild(word);
        var pill = new StatusPill { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        title.AddChild(pill);
        pill.Configure("● LIVE", StatusPill.Kind.Danger, pulse: true);
        _clock = Num(MatchDuration(), DesignTokens.TextHi, 22);
        title.AddChild(_clock);

        title.AddChild(RosterCells.Spacer());
        var tally = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        tally.AddThemeConstantOverride("separation", 12);
        tally.AddChild(GarrisonTally(numSize: 26, dashSize: 18, nameSize: 13));
        tally.AddChild(Caption("GARRISONS", 10).With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter));
        title.AddChild(tally);
        title.AddChild(RosterCells.Spacer());

        title.AddChild(
            RosterCells
                .Mono(MapName().ToUpperInvariant(), DesignTokens.Text2)
                .With(l => l.AddThemeFontSizeOverride("font_size", 12))
                .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
        );
        var close = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        close.AddThemeConstantOverride("separation", 8);
        close.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        close.AddChild(KeyCap("F5"));
        close.AddChild(Caption("CLOSE").With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter));
        title.AddChild(close);
        col.AddChild(titleWrap);

        col.AddChild(RosterCells.Hairline());

        // --- the two team tables, side by side ---
        var tablesWrap = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        tablesWrap.AddThemeConstantOverride("margin_top", 16);
        tablesWrap.SizeFlagsVertical = SizeFlags.ExpandFill;
        var tables = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        tables.AddThemeConstantOverride("separation", 24);
        tablesWrap.AddChild(tables);
        byte myTeam = MyTeam();
        for (byte t = 0; t < 2; t++)
            tables.AddChild(LiveTeamColumn(t, myTeam));
        col.AddChild(tablesWrap);

        // --- footer legend ---
        var footWrap = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        footWrap.AddThemeConstantOverride("margin_top", 14);
        var foot = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        foot.AddThemeConstantOverride("separation", 10);
        footWrap.AddChild(foot);
        foot.AddChild(RosterCells.Diamond(DesignTokens.TextDim, hollow: true));
        foot.AddChild(
            Caption(
                "KILLS, LOSSES AND POINTS ARE MATCH RECORD — ALWAYS SHOWN. LIVE HULL AND STATUS FOLLOW FOG OF WAR: ENEMY PILOTS YOUR TEAM CANNOT SEE READ · · ·"
            )
        );
        foot.AddChild(RosterCells.Spacer());
        foot.AddChild(Caption("UPDATES LIVE"));
        col.AddChild(footWrap);

        RefreshLiveCells();
        return center;
    }

    // Live column ratios. Both the header and the rows pass these, so the columns line up.
    private const float LiveGutter = 18f;
    private const float LiveName = 1.5f;
    private const float LiveShip = 1.25f;
    private const float LiveNum = 0.45f;
    private const float LivePts = 0.7f;
    private const int LivePad = 14; // the live board is narrower than the lobby — tighter side padding

    private Control LiveTeamColumn(byte team, byte myTeam)
    {
        Color accent = DesignTokens.Faction(team);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 0);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.SizeFlagsStretchRatio = 1f;

        // Team strip: faction wash + 3px bar, name, pilot/kill summary, team points.
        var strip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(accent, 0.10f),
            BorderColor = accent,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthLeft = 3;
        sb.ContentMarginLeft = sb.ContentMarginRight = LivePad;
        sb.ContentMarginTop = sb.ContentMarginBottom = 10;
        strip.AddThemeStyleboxOverride("panel", sb);
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 10);
        strip.AddChild(head);
        head.AddChild(RosterCells.Diamond(accent, hollow: false));
        head.AddChild(
            UiKit
                .MakeLabel(TeamName(team), UiKit.TextStyle.Label, DesignTokens.TextHi)
                .With(l => l.AddThemeFontSizeOverride("font_size", 15))
                .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
        );
        head.AddChild(RosterCells.Spacer());
        head.AddChild(
            RosterCells
                .Mono($"{Stats.PilotCount(team)} PILOTS · {Stats.TeamKills(team)} KILLS", DesignTokens.Text2)
                .With(l => l.AddThemeFontSizeOverride("font_size", 11))
                .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
        );
        head.AddChild(Num(_world.TeamState.Score(team).ToString(), accent, 18));
        col.AddChild(strip);

        // Column header (read-only in live mode — sorting is a post-match affordance).
        var hdr = RosterCells.HeaderPanel(LivePad);
        hdr.MouseFilter = MouseFilterEnum.Ignore;
        var hdrRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hdrRow.AddThemeConstantOverride("separation", 8);
        hdr.AddChild(hdrRow);
        hdrRow.AddChild(RosterCells.Lbl("", LiveGutter));
        hdrRow.AddChild(RosterCells.Cell(RosterCells.Lbl("CALLSIGN"), LiveName));
        hdrRow.AddChild(RosterCells.Cell(RosterCells.Lbl("SHIP / STATUS"), LiveShip));
        hdrRow.AddChild(RosterCells.Cell(RosterCells.Lbl("K", align: HorizontalAlignment.Center), LiveNum));
        hdrRow.AddChild(RosterCells.Cell(RosterCells.Lbl("D", align: HorizontalAlignment.Center), LiveNum));
        hdrRow.AddChild(RosterCells.Cell(RosterCells.Lbl("EJ", align: HorizontalAlignment.Center), LiveNum));
        hdrRow.AddChild(RosterCells.Cell(RosterCells.Lbl("PTS", align: HorizontalAlignment.Right), LivePts));
        col.AddChild(hdr);

        var rows = Stats.Sorted(team, MatchStatsStore.SortKey.Points, descending: true);
        if (rows.Count == 0)
        {
            col.AddChild(RosterCells.EmptyNote("No pilots flew for this side."));
            return col;
        }
        foreach (var p in rows)
            col.AddChild(LiveRow(p, myTeam));
        return col;
    }

    private Control LiveRow(in MatchStatsStore.PilotStat p, byte myTeam)
    {
        bool isMe = p.ClientId == _net.LocalClientId;
        Color team = TeamColor(p.Team);
        var panel = RosterCells.RowPanel(isMe, team, vPad: 11, hPad: LivePad);
        panel.MouseFilter = MouseFilterEnum.Ignore;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        row.AddChild(Gutter(isMe, team, LiveGutter));
        row.AddChild(CallsignCell(p, isMe, LiveName));

        // SHIP / STATUS — filled by RefreshLiveCells (both now and on its own cadence).
        var shipCell = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        shipCell.AddThemeConstantOverride("separation", 6);
        RosterCells.Cell(shipCell, LiveShip);
        var shipText = RosterCells.Mono("", DesignTokens.Text2);
        shipCell.AddChild(shipText);
        var badgeHost = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        badgeHost.AddThemeConstantOverride("separation", 6);
        shipCell.AddChild(badgeHost);
        row.AddChild(shipCell);
        _liveCells.Add((p.ClientId, shipText, badgeHost));

        AddNumberCells(row, p, isMe, team, LiveNum, LivePts);
        return panel;
    }

    // Re-text every live SHIP/STATUS cell from the current roster + render state. Cheap enough to run
    // four times a second; nothing here allocates a control unless the badge actually changed shape.
    private void RefreshLiveCells()
    {
        if (_liveCells.Count == 0)
            return;
        byte myTeam = MyTeam();
        var roster = new Dictionary<int, LobbyPlayer>();
        foreach (var lp in _net.LobbyPlayers)
            roster[lp.Id] = lp;

        foreach (var (clientId, text, badgeHost) in _liveCells)
        {
            if (!GodotObject.IsInstanceValid(text) || !GodotObject.IsInstanceValid(badgeHost))
                continue;
            var (label, color, badge) = LiveFor(roster.TryGetValue(clientId, out var p) ? p : null, myTeam);
            text.Text = label;
            text.AddThemeColorOverride("font_color", color);
            foreach (var c in badgeHost.GetChildren())
                c.QueueFree();
            if (badge != null)
                badgeHost.AddChild(badge);
        }
    }

    // What a pilot is flying right now, fog-gated. Own team is always readable; an enemy your team
    // can't see on radar reads "· · ·" with no badge, so the board never leaks their hull.
    private (string Text, Color Color, Control? Badge) LiveFor(LobbyPlayer? player, byte myTeam)
    {
        if (player is not LobbyPlayer p || p.ShipId == 0)
            return (
                "—",
                DesignTokens.TextDim,
                RosterCells.TintBadge("DOCKED", DesignTokens.Text2, new Color(DesignTokens.Text2, 0.16f))
            );

        bool enemy = p.Team != myTeam;

        // The local ship is a PredictionController, never a node in Ships.Nodes — resolve it first.
        if (_world.Ships.LocalShip is { } local && local.ShipId == p.ShipId)
            return (
                HullName((byte)local.Class, local.IsPod),
                DesignTokens.Text2,
                local.IsPod ? RosterCells.Badge("POD", DesignTokens.Warn) : null
            );

        if (enemy && _defs.FogOfWar && !_world.IsRadarVisible(p.ShipId))
            return ("· · ·", DesignTokens.TextDim, null);

        if (!_world.Ships.Nodes.TryGetValue(p.ShipId, out var node) || node is not RemoteShip rs)
            return ("· · ·", DesignTokens.TextDim, null);

        Control? badge =
            rs.IsPod ? RosterCells.Badge("POD", DesignTokens.Warn)
            : enemy ? RosterCells.TintBadge("CONTACT", DesignTokens.DangerText, new Color(DesignTokens.Danger, 0.16f))
            : null;
        return (HullName((byte)rs.Class, rs.IsPod), DesignTokens.Text2, badge);
    }

    private string HullName(byte classId, bool isPod) =>
        _defs.TryGetShipDef(isPod ? DefRegistry.PodClassId : classId, out var d) && !string.IsNullOrEmpty(d.Name)
            ? d.Name
            : "UNKNOWN";

    // ---- POST-MATCH mode ----------------------------------------------------

    // Post-match column ratios (no SHIP column — a pilot flies many hulls over a match).
    private const float PostGutter = 20f;
    private const float PostName = 1.6f;
    private const float PostNum = 0.5f;
    private const float PostPts = 0.7f;

    private Control BuildPostMatch()
    {
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);

        root.AddChild(PostHeader());
        root.AddChild(RosterCells.Hairline());
        root.AddChild(PostResultBand());
        root.AddChild(RosterCells.Hairline());

        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 0);
        body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(PostFilterColumn());
        body.AddChild(RosterCells.Hairline(vertical: true));
        body.AddChild(PostRosterColumn());
        body.AddChild(RosterCells.Hairline(vertical: true));
        body.AddChild(PostSummaryColumn());
        root.AddChild(body);
        return root;
    }

    // Brand header, mirroring the Lobby's so the two screens stack seamlessly.
    private Control PostHeader()
    {
        var bar = RosterCells.BarPanel(26, 12, new Color(DesignTokens.Void, 0.55f));
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddChild(row);

        var brand = new HBoxContainer();
        brand.AddThemeConstantOverride("separation", 11);
        brand.AddChild(
            new ColorRect
            {
                Color = DesignTokens.TeamAccent,
                CustomMinimumSize = new Vector2(12, 12),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            }
        );
        var word = UiKit.MakeLabel("STELLAR ALLEGIANCE", UiKit.TextStyle.Label, DesignTokens.TextHi);
        word.AddThemeFontOverride("font", UiFonts.WithGlyphSpacing(UiFonts.SairaBold, 3));
        word.AddThemeFontSizeOverride("font_size", 16);
        brand.AddChild(word);
        brand.AddChild(UiChips.AccentChip("MATCH RESULT", 16, 6, 12));
        row.AddChild(brand);

        row.AddChild(RosterCells.Spacer());

        var right = new HBoxContainer();
        right.AddThemeConstantOverride("separation", 16);
        right.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        right.AddChild(UiKit.MakeLabel($"● {_net.LobbyPlayers.Count} ONLINE", UiKit.TextStyle.Data, DesignTokens.Ok));
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

    // Result band: ENDED pill + duration, "{winner} WINS", the win reason, the garrison tally, and
    // the close affordances.
    private Control PostResultBand()
    {
        var bar = RosterCells.BarPanel(26, 20, new Color(DesignTokens.Void, 0.55f));
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 24);
        bar.AddChild(row);

        var left = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        left.AddThemeConstantOverride("separation", 7);
        var line1 = new HBoxContainer();
        line1.AddThemeConstantOverride("separation", 14);
        var pill = new StatusPill { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        line1.AddChild(pill);
        pill.Configure("ENDED", StatusPill.Kind.Warn);
        line1.AddChild(Num(MatchDuration(), DesignTokens.TextHi, 22));
        line1.AddChild(Caption("MATCH DURATION").With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter));
        left.AddChild(line1);

        int winner = _world.Winner ?? -1;
        var line2 = new HBoxContainer();
        line2.AddThemeConstantOverride("separation", 14);
        if (winner is 0 or 1)
        {
            line2.AddChild(RosterCells.Diamond(DesignTokens.Faction(winner), hollow: false));
            line2.AddChild(
                UiKit.MakeLabel(TeamName(winner).ToUpperInvariant(), UiKit.TextStyle.Display, DesignTokens.Faction(winner))
            );
            line2.AddChild(UiKit.MakeLabel("WINS", UiKit.TextStyle.Display, DesignTokens.TextHi));
        }
        else
        {
            line2.AddChild(RosterCells.Diamond(DesignTokens.TextDim, hollow: true));
            line2.AddChild(UiKit.MakeLabel("NO WINNER", UiKit.TextStyle.Display, DesignTokens.TextHi));
        }
        left.AddChild(line2);
        // Win reason. The only win condition today is "all of a side's win-condition garrisons fell",
        // so it's stated outright when there IS a winner — never on a board with no result to explain
        // (before the first match of a session, where the winner byte is still 255).
        string where = $"{MapName().ToUpperInvariant()} · {(_net.CurrentMap?.Mode ?? "CONQUEST").ToUpperInvariant()}";
        left.AddChild(
            RosterCells
                .Mono(winner is 0 or 1 ? $"ALL WIN-CONDITION GARRISONS DESTROYED · {where}" : where, DesignTokens.Text2)
                .With(l => l.AddThemeFontSizeOverride("font_size", 12))
        );
        row.AddChild(left);

        row.AddChild(RosterCells.Spacer());
        var tally = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        tally.AddThemeConstantOverride("separation", 3);
        tally.AddChild(Caption("GARRISONS DESTROYED", 10).With(l => l.HorizontalAlignment = HorizontalAlignment.Center));
        tally.AddChild(GarrisonTally(numSize: 44, dashSize: 30, nameSize: 13));
        row.AddChild(tally);
        row.AddChild(RosterCells.Spacer());

        var actions = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        actions.AddThemeConstantOverride("separation", 14);
        var caps = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        caps.AddThemeConstantOverride("separation", 8);
        caps.AddChild(KeyCap("F5"));
        caps.AddChild(KeyCap("ESC"));
        caps.AddChild(Caption("CLOSE").With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter));
        actions.AddChild(caps);
        var back = UiKit.MakeButton("BACK TO LOBBY", Close, ButtonVariant.Primary);
        back.FocusMode = FocusModeEnum.None;
        actions.AddChild(back);
        row.AddChild(actions);
        return bar;
    }

    // Left column: the three team-filter cards + the key-cap legend.
    private Control PostFilterColumn()
    {
        var margin = new MarginContainer { CustomMinimumSize = new Vector2(228, 0) };
        RosterCells.Margins(margin, 12, 14);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        margin.AddChild(col);

        col.AddChild(UiKit.MakeLabel("SCOREBOARD", UiKit.TextStyle.Label, DesignTokens.TextDim));
        var cards = new VBoxContainer();
        cards.AddThemeConstantOverride("separation", 8);
        col.AddChild(cards);
        cards.AddChild(FilterCard(0));
        cards.AddChild(FilterCard(1));
        cards.AddChild(FilterCard(MatchStatsStore.AllTeams));

        col.AddChild(RosterCells.Spacer(vertical: true));
        col.AddChild(new DiamondDivider());

        var hint = new InsetWell();
        var hintCol = new VBoxContainer();
        hintCol.AddThemeConstantOverride("separation", 8);
        hint.AddChild(hintCol);
        hintCol.AddChild(Caption("SUMMONED ANY TIME", 10));
        hintCol.AddChild(HintRow("F5", "TOGGLE SCOREBOARD"));
        hintCol.AddChild(HintRow("ESC", "BACK TO LOBBY"));
        col.AddChild(hint);
        return margin;
    }

    private static Control HintRow(string key, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 9);
        row.AddChild(KeyCap(key));
        row.AddChild(
            RosterCells
                .Mono(text, DesignTokens.Text2)
                .With(l => l.AddThemeFontSizeOverride("font_size", 11))
                .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
        );
        return row;
    }

    // One team-filter card. The neutral ALL PILOTS card uses the HOLLOW diamond in Text2 — it is not
    // a side, so it never borrows a faction colour.
    private Button FilterCard(byte team)
    {
        bool all = team == MatchStatsStore.AllTeams;
        bool selected = _filterTeam == team;
        Color accent = TeamColor(team);
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(0, 62),
            ClipContents = true,
            FocusMode = FocusModeEnum.None,
        };
        foreach (string s in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            btn.AddThemeStyleboxOverride(s, RosterCells.TabStyle(accent, selected));
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            btn.AddThemeColorOverride(c, Colors.Transparent);
        byte captured = team;
        btn.Pressed += () =>
        {
            if (_filterTeam == captured)
                return;
            _filterTeam = captured;
            _dirty = true;
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        };

        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        RosterCells.Margins(pad, 13, 10);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 7);
        pad.AddChild(col);

        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 9);
        top.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(RosterCells.Diamond(accent, hollow: all));
        top.AddChild(
            UiKit
                .MakeLabel(TeamName(team), UiKit.TextStyle.Label, DesignTokens.TextHi)
                .With(l => l.AddThemeFontSizeOverride("font_size", 14))
                .With(l => l.MouseFilter = MouseFilterEnum.Ignore)
        );
        top.AddChild(RosterCells.Spacer());
        // The team cards lead with their SCORE (what the match is decided on); the neutral card has
        // no score of its own, so it leads with the pilot count.
        top.AddChild(Num(all ? Stats.PilotCount(team).ToString() : _world.TeamState.Score(team).ToString(), accent, 18));
        col.AddChild(top);

        var sub = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        sub.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        sub.AddChild(RosterCells.Mono(all ? "BOTH TEAMS" : $"{Stats.PilotCount(team)} PILOTS", DesignTokens.Text2));
        sub.AddChild(RosterCells.Spacer());
        sub.AddChild(RosterCells.Mono($"{Stats.TeamKills(team)} KILLS", DesignTokens.Text2));
        col.AddChild(sub);

        btn.AddChild(pad);
        return btn;
    }

    // Centre column: the filtered roster title + summary stats, the sortable header, and the rows.
    private Control PostRosterColumn()
    {
        bool all = _filterTeam == MatchStatsStore.AllTeams;
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.SizeFlagsVertical = SizeFlags.ExpandFill;

        var head = RosterCells.PaddedRow(24, 16);
        var headRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        head.AddChild(headRow);
        var nameCol = new VBoxContainer();
        nameCol.AddThemeConstantOverride("separation", 2);
        nameCol.AddChild(
            UiKit
                .MakeLabel(TeamName(_filterTeam), UiKit.TextStyle.Title, all ? DesignTokens.TextHi : TeamColor(_filterTeam))
                .With(l => l.AddThemeFontSizeOverride("font_size", 22))
        );
        int pilots = Stats.PilotCount(_filterTeam);
        nameCol.AddChild(
            RosterCells
                .Mono(
                    $"{pilots} pilot{(pilots == 1 ? "" : "s")} · {Stats.TeamKills(_filterTeam)} kills · "
                        + $"{Stats.TeamEjects(_filterTeam)} ejections · {Stats.TeamDeaths(_filterTeam)} pods lost",
                    DesignTokens.Text2
                )
                .With(l => l.AddThemeFontSizeOverride("font_size", 11))
        );
        headRow.AddChild(nameCol);
        headRow.AddChild(RosterCells.Spacer());
        // TEAM PTS reads the authoritative team score (TeamState) for a side; the ALL card has no
        // team score, so it sums the pilots' points instead.
        headRow.AddChild(
            StatCol(
                all ? "TOTAL PTS" : "TEAM PTS",
                all ? Stats.TeamPoints(_filterTeam).ToString() : _world.TeamState.Score(_filterTeam).ToString(),
                DesignTokens.Data
            )
        );
        headRow.AddChild(StatCol("KILLS", Stats.TeamKills(_filterTeam).ToString(), DesignTokens.TextHi));
        headRow.AddChild(StatCol("LOSSES", Stats.TeamEjects(_filterTeam).ToString(), DesignTokens.TextHi));
        col.AddChild(head);

        col.AddChild(PostColumnHeader());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 0);
        scroll.AddChild(rows);
        col.AddChild(scroll);

        var list = Stats.Sorted(_filterTeam, _sortKey, _sortDesc);
        if (list.Count == 0)
            rows.AddChild(RosterCells.EmptyNote("No pilots flew for this side."));
        foreach (var p in list)
            rows.AddChild(PostRow(p));
        return col;
    }

    // A caption-over-value stat column in the roster header (the Lobby's StatCol, read-only here).
    private static Control StatCol(string caption, string value, Color valueColor)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        col.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        col.AddChild(Caption(caption, 10));
        col.AddChild(Num(value, valueColor, 20));
        var wrap = new MarginContainer();
        wrap.AddThemeConstantOverride("margin_left", 26);
        wrap.AddChild(col);
        return wrap;
    }

    private Control PostColumnHeader()
    {
        var panel = RosterCells.HeaderPanel();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        row.AddChild(RosterCells.Lbl("", PostGutter));
        row.AddChild(SortHeader("CALLSIGN", MatchStatsStore.SortKey.Name, PostName, HorizontalAlignment.Left));
        row.AddChild(SortHeader("K", MatchStatsStore.SortKey.Kills, PostNum, HorizontalAlignment.Center));
        row.AddChild(SortHeader("D", MatchStatsStore.SortKey.Deaths, PostNum, HorizontalAlignment.Center));
        row.AddChild(SortHeader("EJ", MatchStatsStore.SortKey.Ejects, PostNum, HorizontalAlignment.Center));
        row.AddChild(SortHeader("PTS", MatchStatsStore.SortKey.Points, PostPts, HorizontalAlignment.Right));
        return panel;
    }

    // A sortable column header: a flat Button carrying the caps label plus a direction caret. Only the
    // ACTIVE column shows its caret (and reads in the accent); the rest stay dim and caret-less.
    private Control SortHeader(string text, MatchStatsStore.SortKey key, float ratio, HorizontalAlignment align)
    {
        bool on = _sortKey == key;
        var btn = new Button { Flat = true, FocusMode = FocusModeEnum.None };
        RosterCells.Cell(btn, ratio);
        foreach (string c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            btn.AddThemeColorOverride(c, Colors.Transparent);
        btn.Pressed += () =>
        {
            if (_sortKey == key)
                _sortDesc = !_sortDesc;
            else
            {
                _sortKey = key;
                // Numbers read best biggest-first; a callsign list reads best A→Z.
                _sortDesc = key != MatchStatsStore.SortKey.Name;
            }
            _dirty = true;
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        };

        var content = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        content.AddThemeConstantOverride("separation", 5);
        content.Alignment = align switch
        {
            HorizontalAlignment.Center => BoxContainer.AlignmentMode.Center,
            HorizontalAlignment.Right => BoxContainer.AlignmentMode.End,
            _ => BoxContainer.AlignmentMode.Begin,
        };
        var label = RosterCells.Lbl(text);
        label.AddThemeColorOverride("font_color", on ? DesignTokens.TeamAccent : DesignTokens.TextDim);
        content.AddChild(label);
        if (on)
            // A custom-drawn triangle would need its own Control; the caret glyph reads the same at
            // this size, the way RosterCells.Diamond uses ◆.
            content.AddChild(
                RosterCells
                    .Mono(_sortDesc ? "▼" : "▲", DesignTokens.TeamAccent)
                    .With(l => l.AddThemeFontSizeOverride("font_size", 9))
                    .With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter)
            );
        btn.AddChild(content);
        return btn;
    }

    private Control PostRow(in MatchStatsStore.PilotStat p)
    {
        bool isMe = p.ClientId == _net.LocalClientId;
        Color team = TeamColor(p.Team);
        var panel = RosterCells.RowPanel(isMe, team);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);
        row.AddChild(Gutter(isMe, team, PostGutter));
        row.AddChild(CallsignCell(p, isMe, PostName));
        AddNumberCells(row, p, isMe, team, PostNum, PostPts);
        return panel;
    }

    // Right column: the team-summary comparison bars, the Top Gun callout, and a usage hint.
    private Control PostSummaryColumn()
    {
        var margin = new MarginContainer { CustomMinimumSize = new Vector2(320, 0) };
        RosterCells.Margins(margin, 16, 16);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 14);
        margin.AddChild(col);

        var summary = new HairlinePanel { Title = "TEAM SUMMARY" };
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 13);
        summary.AddChild(rows);
        rows.AddChild(CompareRow("SCORE", _world.TeamState.Score(0), _world.TeamState.Score(1)));
        rows.AddChild(CompareRow("KILLS", Stats.TeamKills(0), Stats.TeamKills(1)));
        rows.AddChild(CompareRow("EJECTIONS", Stats.TeamEjects(0), Stats.TeamEjects(1)));
        rows.AddChild(CompareRow("GARRISONS", Stats.Garrisons(0), Stats.Garrisons(1)));
        col.AddChild(summary);

        if (Stats.TopGun() is { } best)
        {
            var alert = new AlertBox();
            col.AddChild(alert);
            alert.Configure(
                $"TOP GUN — {best.Name}",
                $"{best.Kills} KILLS · {best.Ejects} LOSSES · {best.Points} PTS\n{TeamName(best.Team).ToUpperInvariant()}",
                StatusPill.Kind.Data
            );
        }

        col.AddChild(RosterCells.Spacer(vertical: true));
        col.AddChild(
            Caption("SORT BY ANY COLUMN · SELECT A TEAM AT LEFT\nSCORES FINAL AT MATCH END", 10)
                .With(l => l.AutowrapMode = TextServer.AutowrapMode.WordSmart)
        );
        return margin;
    }

    // One comparison row: blue value | caps label | red value, over a split bar sized by blue's share.
    private static Control CompareRow(string label, int blue, int red)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);

        var line = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        line.AddChild(Num(blue.ToString(), DesignTokens.Faction0, 16));
        line.AddChild(RosterCells.Spacer());
        line.AddChild(RosterCells.Lbl(label).With(l => l.SizeFlagsVertical = SizeFlags.ShrinkCenter));
        line.AddChild(RosterCells.Spacer());
        line.AddChild(Num(red.ToString(), DesignTokens.Faction1, 16));
        col.AddChild(line);

        // Share of the total, clamped so one side never collapses to an invisible sliver at 0-0
        // (both zero => a dead-even split).
        int total = Math.Max(0, blue) + Math.Max(0, red);
        float share = total > 0 ? Math.Max(0, blue) / (float)total : 0.5f;
        var bar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 3), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddThemeConstantOverride("separation", 0);
        bar.AddChild(
            new ColorRect
            {
                Color = DesignTokens.Faction0,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(share, 0.001f),
                MouseFilter = MouseFilterEnum.Ignore,
            }
        );
        bar.AddChild(
            new ColorRect
            {
                Color = DesignTokens.Faction1,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsStretchRatio = Math.Max(1f - share, 0.001f),
                MouseFilter = MouseFilterEnum.Ignore,
            }
        );
        col.AddChild(bar);
        return col;
    }
}
