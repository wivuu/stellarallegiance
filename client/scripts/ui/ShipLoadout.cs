using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  ShipLoadout.cs — DOCKED SCREEN (tab host + HANGAR)
//
//  Full-screen overlay (F4, or the HANGAR button on the spawn menu). It hosts three tabs —
//  HANGAR / BUILD / RESEARCH — over a shared 340px CommandSidebar (the "command network" mini-map
//  + your-bases list). Only the HANGAR tab is live in Phase A; BUILD/RESEARCH are server-catalog
//  guards (ResearchTab/BuildTab) that Phase C/D fill in. The tab BODY (center + right columns,
//  card strip, demo harness) lives in the partial ShipLoadout.Hangar.cs; this file owns the shell,
//  the top/launch bars, tab switching, ship selection, and the spawn gate.
//
//  Everything the player edits here lives in LoadoutState (CLIENT-LOCAL) — cargo counts and the
//  per-slot weapon overrides ride LAUNCH's MsgSpawn (RequestSpawn ships CargoFor + WeaponOverridesFor),
//  and the server validates and spawns with the accepted loadout. The launch-base pick (CommandSidebar)
//  is stored in LoadoutState.Shared.SelectedBaseId and also rides MsgSpawn as the launch base.
//
//  This IS the ship-select screen: the Hud auto-opens it (OpenedForSpawn) whenever you're in an
//  active match without a ship and closes it once the ship spawns — LAUNCH is the only way out of a
//  spawn hangar (no ESC/F4 dismiss). F4 while flying opens it as a browsable loadout viewer instead.
// =====================================================================
public partial class ShipLoadout : Control
{
    public static bool Active { get; private set; }

    // True when this hangar is the mandatory ship-select (Hud auto-open, no dismiss);
    // false for a browse-while-flying F4 open. The Hud promotes an open browse hangar
    // to for-spawn if the ship dies under it.
    public bool OpenedForSpawn;

    private DefRegistry _defs = null!;
    private ShipController _ship = null!;
    private WorldRenderer _world = null!;
    private GameNetClient _net = null!;

    // The shared process-wide loadout: the hangar edits it and RequestSpawn reads it, so the chosen
    // hold persists across open/close and rides MsgSpawn to the server.
    private readonly LoadoutState _state = LoadoutState.Shared;

    private const int PayloadSegments = 20;

    // -- tab host ------------------------------------------------------------
    private CommandSidebar _sidebar = null!;
    private Control _tabContent = null!;
    private Control _hangarContent = null!;
    private ResearchTab? _researchTab;
    private BuildTab? _buildTab;
    private int _activeTab;

    // -- hangar: ship-class card strip --------------------------------------
    private HBoxContainer _cardStrip = null!;
    private readonly List<(byte classId, ShipCard card)> _shipCards = new();
    private int _builtShipCount = -1;
    private long _cardGateSig = long.MinValue;

    // -- hangar: center column ----------------------------------------------
    private Label _roleLabel = null!;
    private Label _nameLabel = null!;
    private Label _hullLabel = null!;
    private Label _descLabel = null!;
    private LoadoutPreview _preview = null!;
    private readonly List<(Label value, SegmentedBar bar)> _statBars = new();
    private static readonly string[] StatNames = ["VELOCITY", "ARMOR", "PAYLOAD", "SIGNATURE"];

    // -- hangar: right column -----------------------------------------------
    private Label _slotCount = null!;
    private VBoxContainer _slotList = null!;
    private readonly List<(byte hpIndex, LoadoutSlot row)> _slotRows = new();
    private Label _payloadText = null!;
    private SegmentedBar _payloadBar = null!;
    private Label _overCapacity = null!;

    // Cached by RefreshPayload's PayloadUsed walk; IsOverCapacity reads this instead of re-running
    // the full hardpoint+cargo walk itself (RefreshLaunchGate calls it every _Process frame).
    // RefreshPayload always runs before this is read each frame — either earlier in the same
    // _Process call (via SelectShip/RefreshLoadoutViews when the ship-card list first builds) or
    // from a prior frame's loadout edit (equip/unequip, cargo step, reset) — so it's never stale.
    private bool _payloadOverCap;
    private PanelContainer _arsenalFrame = null!;
    private Label _arsenalTitle = null!;
    private Label _arsenalFit = null!;
    private VBoxContainer _arsenalRows = null!;
    private VBoxContainer _cargoList = null!;
    private readonly List<(uint itemId, Label count)> _cargoCounts = new();
    private int _builtCargoCount = -1;

    // -- top / launch bars ---------------------------------------------------
    private Label _topReadout = null!;
    private StatReadout _costReadout = null!;
    private StatReadout _payloadReadout = null!;
    private StatReadout _fromReadout = null!;
    private ChamferButton _launch = null!;
    private ChamferButton _reset = null!;
    private ChamferButton? _firstCargoPlus;
    private Label _launchHint = null!;
    private Control _launchBar = null!;

    private byte? _classId;
    private byte? _selectedHp;

    // A LAUNCH was clicked and the spawn hasn't landed yet — the button holds
    // "LAUNCHING…" until the ship exists (Hud then closes us) or the gate refuses
    // (ShipController.SpawnHint names why; re-enabled for a retry).
    private bool _launchPending;

    // Signature of the friendly-base set the sidebar was last built from; the sidebar is only
    // re-pulled when it changes (rebuilding rows every frame would churn selection).
    private long _baseSig = long.MinValue;

    public void Init(DefRegistry defs, ShipController ship, WorldRenderer world, GameNetClient net)
    {
        _defs = defs;
        _ship = ship;
        _world = world;
        _net = net;
    }

    // The local pilot's team: the world's authoritative LocalTeam once known, else the net
    // handshake's MyTeam (set before the world ever confirms it). Used everywhere this screen
    // needs "my team" — spawn gates, tech-gated arsenal filtering, tier migration.
    private byte Team => _world.LocalTeam ?? _net.MyTeam;

    public override void _EnterTree()
    {
        Active = true;
        Input.MouseMode = Input.MouseModeEnum.Visible; // cursor UI; flight re-captures on click after close
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuOpen);
        SfxManager.Instance?.StartAmbient(); // the hangar owns the ambient hum bed
    }

    public override void _ExitTree()
    {
        Active = false;
        SfxManager.Instance?.PlayUi(SfxManager.SfxId.MenuClose);
        SfxManager.Instance?.StopAmbient();
        DemoAfterLaunch();
    }

    public override void _Ready()
    {
        // AnchorsAndOffsets: plain SetAnchorsPreset adjusts offsets to KEEP the current
        // (zero) size — as a CanvasLayer child this control must claim the viewport rect.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        UiFonts.EnsureLoaded();
        UiTheme.Apply(this);
        MouseFilter = MouseFilterEnum.Stop; // nothing leaks to the game view below

        foreach (string a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--hangar-demo="))
                _demoDir = a["--hangar-demo=".Length..];

        var bg = new ColorRect { Color = DesignTokens.Void };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var rows = new VBoxContainer();
        rows.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        rows.AddThemeConstantOverride("separation", 0);
        AddChild(rows);

        rows.AddChild(BuildTopBar());

        // Build the launch bar's controls up-front (before wiring the sidebar) so _fromReadout exists
        // when the sidebar's first auto-select fires OnBaseSelected → UpdateBaseReadouts. It's added to
        // `rows` last so it still sits at the bottom.
        _launchBar = BuildLaunchBar();

        // Body = [ shared CommandSidebar | active tab content ].
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 0);
        rows.AddChild(body);

        _tabContent = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _hangarContent = BuildHangarContent();
        _tabContent.AddChild(_hangarContent);

        _sidebar = new CommandSidebar();
        _sidebar.Init(_world, _net, _defs);
        _sidebar.BaseSelected += OnBaseSelected;
        body.AddChild(_sidebar);
        body.AddChild(_tabContent);

        rows.AddChild(_launchBar);

        OnTabSelected(0);
    }

    // ---- top bar -------------------------------------------------------------

    private Control BuildTopBar()
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(DesignTokens.Void, 0.55f),
            BorderColor = DesignTokens.BorderHi,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthBottom = 1;
        sb.ContentMarginLeft = sb.ContentMarginRight = 26;
        sb.ContentMarginTop = sb.ContentMarginBottom = 12;
        panel.AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 24);
        panel.AddChild(row);

        var dot = new ColorRect
        {
            Color = DesignTokens.TeamAccent,
            CustomMinimumSize = new Vector2(12, 12),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(dot);
        row.AddChild(UiKit.MakeLabel("STELLAR ALLEGIANCE", UiKit.TextStyle.Label, DesignTokens.TextHi));

        // MAP + the tab strip share one HBox so the MAP↔HANGAR gap matches the inter-tab gap (12px);
        // if MAP joined `row` directly it'd inherit the outer 24px separation and sit too far out.
        var navGroup = new HBoxContainer();
        navGroup.AddThemeConstantOverride("separation", 12);
        row.AddChild(navGroup);

        // MAP opens the F3 sector overview. It's a one-shot action, not a tab: this shell hides
        // itself while the map is up (see _Process) and reappears when F3 closes it, so the button
        // sits to the LEFT of the HANGAR/BUILD/RESEARCH tab strip rather than joining it.
        var mapBtn = UiKit.MakeButton("MAP", SectorOverview.RequestOpen, ButtonVariant.Secondary);
        mapBtn.CustomMinimumSize = new Vector2(96, 38);
        navGroup.AddChild(mapBtn);

        // Real tab strip — HANGAR live; BUILD / RESEARCH are server-catalog guards this phase.
        var tabs = UiKit.MakeSegmented(["HANGAR", "BUILD", "RESEARCH"], 0, OnTabSelected);
        tabs.AddThemeConstantOverride("separation", 12); // wider gap between the three docked-screen tabs
        tabs.CustomMinimumSize = new Vector2(380, 0);
        navGroup.AddChild(tabs);

        // (Launch base is shown in the bottom launch footer's FROM readout, not up here — the top
        // bar's left region overlaps the in-game chat comms.)
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(spacer);

        _topReadout = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        row.AddChild(_topReadout);
        return panel;
    }

    // ---- tab switching -------------------------------------------------------

    private void OnTabSelected(int idx)
    {
        _activeTab = idx;
        _hangarContent.Visible = idx == 0;
        _launchBar.Visible = idx == 0; // launching a ship only makes sense from the hangar

        if (idx == 1)
        {
            if (_buildTab == null)
            {
                _buildTab = new BuildTab();
                _tabContent.AddChild(_buildTab);
                _buildTab.Init(_defs, _world, _net);
                _buildTab.SetBase(_sidebar.SelectedBaseId, _sidebar.SelectedTitle, _sidebar.SelectedSectorName);
            }
            _buildTab.Visible = true;
        }
        else if (_buildTab != null)
            _buildTab.Visible = false;

        if (idx == 2)
        {
            if (_researchTab == null)
            {
                _researchTab = new ResearchTab();
                _tabContent.AddChild(_researchTab);
                _researchTab.Init(_defs, _world, _net);
                _researchTab.SetBase(
                    _sidebar.SelectedBaseId,
                    _sidebar.SelectedTitle,
                    _sidebar.SelectedSectorName,
                    _sidebar.SelectedBaseType
                );
            }
            _researchTab.Visible = true;
        }
        else if (_researchTab != null)
            _researchTab.Visible = false;
    }

    // ---- bottom: launch bar --------------------------------------------------

    private Control BuildLaunchBar()
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(DesignTokens.Void, 0.6f),
            BorderColor = DesignTokens.BorderHi,
            AntiAliasing = false,
        };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthTop = 1;
        sb.ContentMarginLeft = sb.ContentMarginRight = 26;
        sb.ContentMarginTop = sb.ContentMarginBottom = 12;
        panel.AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        panel.AddChild(row);

        _costReadout = new StatReadout();
        _payloadReadout = new StatReadout();
        _fromReadout = new StatReadout();
        _fromReadout.Set("—", "FROM");
        row.AddChild(_costReadout);
        row.AddChild(_payloadReadout);
        row.AddChild(_fromReadout);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // Why a queued launch was refused (locked / can't afford) — the old buy menu's
        // hint line, relocated next to the button it explains.
        _launchHint = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Warn);
        _launchHint.Visible = false;
        row.AddChild(_launchHint);

        _reset = UiKit.MakeButton("RESET", OnReset, ButtonVariant.Ghost);
        row.AddChild(_reset);
        _launch = UiKit.MakeButton("◆ LAUNCH", OnLaunch, ButtonVariant.Primary);
        _launch.CustomMinimumSize = new Vector2(180, 40);
        row.AddChild(_launch);
        return panel;
    }

    private void OnReset()
    {
        if (_classId is not byte classId)
            return;
        _state.ResetClass(classId);
        RefreshLoadoutViews();
        foreach ((uint _, Label count) in _cargoCounts)
            count.Text = "00";
    }

    // LAUNCH = the spawn request. The screen stays open showing "LAUNCHING…" until the
    // ship actually exists (the Hud closes a spawn hangar then) or the gate refuses.
    // The local weapon/cargo assignments and the launch-base pick all ride the request
    // (ShipController.RequestSpawn ships CargoFor, WeaponOverridesFor, and
    // LoadoutState.Shared.SelectedBaseId on MsgSpawn) — the server validates and spawns
    // with the accepted loadout.
    private void OnLaunch()
    {
        if (_classId is not byte classId)
            return;
        _launchPending = true;
        _ship.RequestSpawn((ShipClass)classId);
    }

    private void Close() => QueueFree();

    // ---- launch-base selection (sidebar) -------------------------------------

    private void OnBaseSelected(ulong baseId)
    {
        _state.SelectedBaseId = baseId; // rides MsgSpawn as the launch base (Phase B)
        UpdateBaseReadouts();
        _researchTab?.SetBase(baseId, _sidebar.SelectedTitle, _sidebar.SelectedSectorName, _sidebar.SelectedBaseType);
        _buildTab?.SetBase(baseId, _sidebar.SelectedTitle, _sidebar.SelectedSectorName);
    }

    private void UpdateBaseReadouts()
    {
        string title = _sidebar.SelectedTitle;
        if (string.IsNullOrEmpty(title))
        {
            _fromReadout.Set("—", "FROM");
            return;
        }
        _fromReadout.Set(title, "FROM");
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        // A game menu / settings overlay stacks above and owns Esc while it's up (its own
        // handler closes it) — don't also dismiss/re-open underneath it.
        if (EscapeMenu.Active || SettingsDialog.Active)
            return;

        if (key.Keycode == Key.Escape)
        {
            // Browse-while-flying hangar: Esc dismisses it back to flight. The mandatory
            // spawn-select ("base") has no dismiss — LAUNCH is the only way out — so there
            // Esc opens the game menu instead, keeping SETTINGS / LEAVE MATCH / QUIT reachable
            // without a ship. The cursor is already free here (UI mode), so there's no
            // two-step release like flight — Esc opens the menu directly.
            if (OpenedForSpawn)
                EscapeMenu.Open(this, EscapeMenu.Context.Lobby);
            else
                Close();
            GetViewport().SetInputAsHandled();
            return;
        }

        // 1..9 select the Nth hull — the old buy menu's 1/2/3 spawn hotkeys, now scoped
        // to selection (LAUNCH/Enter-free so chat and launch stay deliberate). The order
        // matches the VISIBLE card strip: tech-locked hulls are hidden there, so they
        // don't consume a number either.
        int slot = (int)(key.Keycode - Key.Key1);
        if (slot >= 0 && slot < 9)
        {
            byte hotkeyTeam = Team;
            List<ShipClassDef> ships = _defs.BuildableShips();
            ships.RemoveAll(s => _world.TeamState.CheckSpawnGate(hotkeyTeam, s.ClassId) == TeamStateStore.SpawnGate.Locked);
            if (slot < ships.Count)
            {
                SelectShip(ships[slot].ClassId);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    // ---- selection + refresh -----------------------------------------------------

    public override void _Process(double delta)
    {
        // Let the pilot peek at the F3 sector map from the hangar: this overlay is opaque and
        // would otherwise occlude the map. Hiding it (rather than closing) keeps `Active` true so
        // flight input stays neutralized and the Hud won't recreate this instance; F3-close flips
        // it back. See SectorOverview.
        Visible = !SectorOverview.Active;

        if (_defs == null)
            return;

        if (_demoDir != null)
            RunDemo(delta);

        // Build (or rebuild) the ship card strip once the streamed defs land / change.
        List<ShipClassDef> ships = _defs.BuildableShips();
        if (ships.Count != _builtShipCount && ships.Count > 0)
        {
            _builtShipCount = ships.Count;
            RebuildShipCards(ships);
            // Prefer an in-session pick, then the last-flown hull, else the first buildable. Loadouts
            // themselves aren't restored — SelectShip → SeedDefaults opens each hull on its authored
            // default (loadouts are per-match; see LoadoutState).
            SelectShip(_classId ?? DefaultShipClassId(ships));
        }

        // Same for the cargo hold — its items are streamed defs too.
        List<CargoItemDef> cargoItems = _defs.AllCargoItems();
        if (cargoItems.Count != _builtCargoCount)
        {
            _builtCargoCount = cargoItems.Count;
            RefreshCargoSection(cargoItems);
        }

        // Live telemetry + spawn gating.
        byte team = Team;
        _topReadout.Text = $"CREDITS {_world.TeamState.Credits(team)}   ·   PING {_ship.PingMs, 3:0} ms";
        RefreshLaunchGate(team);
        RefreshShipCardStates(team);

        // Re-pull the CommandSidebar only when the friendly-base set actually changed.
        long sig = ComputeBaseSig(team);
        if (sig != _baseSig)
        {
            _baseSig = sig;
            _sidebar.Refresh();
        }
        UpdateBaseReadouts();
    }

    // Cheap order-independent signature of this team's known bases (id + alive) so the sidebar
    // rebuilds only on a real change (new/lost base, destroyed flip), not every frame.
    private long ComputeBaseSig(byte team)
    {
        long sig = team + 1L;
        foreach (var (id, _, bteam, alive, _) in _world.Bases.Known())
            if (bteam == team)
                sig ^= unchecked((long)(id * 1000003UL) + (alive ? 1L : 2L));
        return sig;
    }

    private void RefreshLaunchGate(byte team)
    {
        if (_classId is not byte classId || !_defs.TryGetShipDef(classId, out _))
            return;
        bool flying = _world.Ships.LocalShip != null;
        string? hint = _ship.SpawnHint;
        if (flying || hint != null)
            _launchPending = false; // landed, or refused (the hint names why) — let the pilot retry
        bool overCap = IsOverCapacity();
        var gate = _world.TeamState.CheckSpawnGate(team, classId);
        _launch.Disabled = overCap || flying || _launchPending || gate != TeamStateStore.SpawnGate.Allow;
        _launch.Text =
            flying ? "IN FLIGHT"
            : _launchPending ? "LAUNCHING…"
            : gate == TeamStateStore.SpawnGate.Locked ? "⚿ LOCKED"
            : overCap ? "OVER CAPACITY"
            : "◆ LAUNCH";
        _launchHint.Visible = hint != null;
        if (hint != null)
            _launchHint.Text = hint;
    }

    // The card to highlight when the picker first opens with no in-session pick yet: the hull the
    // pilot last docked with (UserPrefs.LastShip), if it's still in the buildable set AND not
    // tech-locked (locked hulls have no card — a fresh match can lock a previously flown hull),
    // otherwise the first unlocked ship. Keeps a returning pilot in their preferred ship.
    private byte DefaultShipClassId(List<ShipClassDef> ships)
    {
        byte team = Team;
        bool Unlocked(byte cls) => _world.TeamState.CheckSpawnGate(team, cls) != TeamStateStore.SpawnGate.Locked;
        int last = UserPrefs.LastShip;
        if (last >= 0 && Unlocked((byte)last))
            foreach (ShipClassDef s in ships)
                if (s.ClassId == last)
                    return (byte)last;
        foreach (ShipClassDef s in ships)
            if (Unlocked(s.ClassId))
                return s.ClassId;
        return ships[0].ClassId;
    }

    private void SelectShip(byte classId)
    {
        if (!_defs.TryGetShipDef(classId, out ShipClassDef def))
            return;
        _classId = classId;
        _selectedHp = null;
        _state.SeedDefaults(classId, def); // open on the hull's authored default hold (once per class)

        foreach ((byte id, ShipCard card) in _shipCards)
            card.Selected = id == classId;

        (string _, string role, string desc) = FlavorOf(classId);
        _roleLabel.Text = role;
        _nameLabel.Text = def.Name.ToUpperInvariant();
        _hullLabel.Text = $"CLASS HULL\n{def.MaxHull:0} HP";
        _descLabel.Text = desc;

        RefreshStatBars(def);
        _preview.ShowShip(_defs, classId);
        // Rebuild the cargo rows for THIS hull (fuel rows only exist on fuel-modeled hulls),
        // then relabel — RefreshCargoSection already seeds per-class counts, so the loop is
        // just the legacy relabel for any row it kept.
        RefreshCargoSection(_defs.AllCargoItems());
        foreach ((uint itemId, Label count) in _cargoCounts)
            count.Text = _state.GetCargoCount(classId, itemId).ToString("00"); // counts are per-class

        RefreshLoadoutViews();
    }

    // Normalize each stat against the biggest hull in the buildable set so the bars
    // compare ships, not absolutes. SIGNATURE is mass as a sensor-loudness proxy.
    private void RefreshStatBars(ShipClassDef def)
    {
        float maxSpeed = 1f,
            maxHull = 1f,
            maxCap = 1f,
            maxMass = 1f;
        foreach (ShipClassDef s in _defs.BuildableShips())
        {
            maxSpeed = MathF.Max(maxSpeed, s.MaxSpeed);
            maxHull = MathF.Max(maxHull, s.MaxHull);
            maxCap = MathF.Max(maxCap, s.PayloadCapacity);
            maxMass = MathF.Max(maxMass, s.Mass);
        }
        float cap = def.PayloadCapacity;
        Span<(float frac, string text)> vals =
        [
            (def.MaxSpeed / maxSpeed, $"{def.MaxSpeed:0} u/s"),
            (def.MaxHull / maxHull, $"{def.MaxHull:0}"),
            (cap / maxCap, $"{cap:0}"),
            (def.Mass / maxMass, $"{def.Mass:0} t"),
        ];
        for (int i = 0; i < _statBars.Count; i++)
        {
            (Label value, SegmentedBar bar) = _statBars[i];
            value.Text = vals[i].text;
            bar.Set(Mathf.RoundToInt(vals[i].frac * bar.Segments));
        }
    }

    // Whether a weapon slot currently holds a weapon (marker fill + slot text share this).
    private bool IsSlotFilled(byte hpIndex)
    {
        if (_classId is not byte classId || _defs.GetHardpoints(classId) is not List<HardpointDef> hps)
            return false;
        foreach (HardpointDef hp in hps)
            if (hp.Kind == HardpointKind.Weapon && hp.Index == hpIndex)
                return _state.AssignedWeapon(classId, hp) != null;
        return false;
    }

    private void SelectSlot(byte hpIndex)
    {
        _selectedHp = hpIndex;
        _preview.SelectedIndex = hpIndex;
        foreach ((byte idx, LoadoutSlot row) in _slotRows)
            row.Selected = idx == hpIndex;
        RefreshArsenal();
    }

    // Rebuild slot rows + payload + arsenal for the current class (after ship switch,
    // equip/unequip, or reset).
    private void RefreshLoadoutViews()
    {
        foreach (var child in _slotList.GetChildren())
            child.QueueFree();
        _slotRows.Clear();

        if (_classId is not byte classId || _defs.GetHardpoints(classId) is not List<HardpointDef> hps)
            return;

        byte team = Team;
        int slots = 0;
        foreach (HardpointDef hp in hps)
        {
            if (hp.Kind != HardpointKind.Weapon)
                continue; // engines/thrusters/docking aren't assignable — 3D dots only
            if (hp.Mount == WeaponMountKind.NonMountable)
                continue; // unauthored mesh mount: not a loadout slot, hidden (no row, no marker)
            slots++;
            byte hpIndex = hp.Index;
            // Show the CURRENT tier: a saved/authored Gat Gun 1 reads as Gat Gun 2 once the team owns
            // gat-2 (the server migrates the same chain at spawn, so display == what actually flies).
            WeaponDef? w = _state.AssignedWeapon(classId, hp) is uint id ? _defs.GetWeapon(MigrateTier(id, team)) : null;
            var row = new LoadoutSlot();
            row.Configure(
                $"P{hpIndex + 1} · WEAPON MOUNT",
                w?.Name.ToUpperInvariant() ?? "— EMPTY —",
                w != null ? WeaponStatLine(w) : ""
            );
            row.Selected = _selectedHp == hpIndex;
            row.Pressed += () => SelectSlot(hpIndex);
            _slotList.AddChild(row);
            _slotRows.Add((hpIndex, row));
        }
        _slotCount.Text = $"{slots} SLOTS";

        RefreshPayload();
        RefreshArsenal();
    }

    private void RefreshPayload()
    {
        if (_classId is not byte classId || !_defs.TryGetShipDef(classId, out ShipClassDef def))
            return;
        float used = _state.PayloadUsed(classId, def.Hardpoints, _defs.GetWeapon, _defs.GetCargoItem);
        float cap = def.PayloadCapacity;
        bool over = used > cap;
        _payloadOverCap = over; // cache for IsOverCapacity — avoids a second walk every _Process frame
        _payloadText.Text = $"{used:0} / {cap:0}";
        _payloadText.AddThemeColorOverride("font_color", over ? DesignTokens.DangerText : DesignTokens.Data);
        _payloadBar.Fill = over ? DesignTokens.Danger : DesignTokens.TeamAccent;
        _payloadBar.Set(cap <= 0f ? 0 : Mathf.RoundToInt(Mathf.Min(1f, used / cap) * PayloadSegments)); // pod: no hold
        _payloadBar.QueueRedraw(); // Fill changes don't self-invalidate
        _overCapacity.Visible = over;
        _payloadReadout.Set($"{used:0}/{cap:0}", "PAYLOAD", over ? DesignTokens.DangerText : null);
        _costReadout.Set($"{def.Cost}", "UNIT COST · CREDITS", DesignTokens.Warn);
    }

    // Reads the flag RefreshPayload cached from its own PayloadUsed walk — RefreshLaunchGate calls
    // this every _Process frame, so this stays O(1) rather than re-walking hardpoints+cargo per frame.
    private bool IsOverCapacity() => _payloadOverCap;

    // The arsenal: every streamed weapon that fits the selected slot, plus the empty-slot
    // row. Weapons gated behind unresearched tech are hidden outright (no locked rows) —
    // the arsenal only lists what the pilot can actually equip; research makes rows appear.
    private void RefreshArsenal()
    {
        foreach (var child in _arsenalRows.GetChildren())
            child.QueueFree();

        if (
            _classId is not byte classId
            || _selectedHp is not byte hpIndex
            || _defs.GetHardpoints(classId) is not List<HardpointDef> hps
        )
        {
            _arsenalFrame.Visible = false;
            return;
        }
        HardpointDef? slot = null;
        foreach (HardpointDef hp in hps)
            if (hp.Kind == HardpointKind.Weapon && hp.Index == hpIndex && hp.Mount != WeaponMountKind.NonMountable)
                slot = hp;
        if (slot == null)
        {
            _arsenalFrame.Visible = false;
            return;
        }

        _arsenalFrame.Visible = true;
        // Name the slot by its mount type so the filtered arsenal is self-explanatory (a missile
        // mount only lists racks, a gun mount only guns; an untyped mount lists both).
        _arsenalTitle.Text =
            $"[P{hpIndex + 1}]  "
            + slot.Mount switch
            {
                WeaponMountKind.Gun => "GUN HARDPOINT",
                WeaponMountKind.Missile => "MISSILE HARDPOINT",
                _ => "WEAPON HARDPOINT",
            };
        byte team = Team;
        // Migrate the equipped id up its tier chain so an obsoleted Gat Gun 1 (now hidden below) still
        // marks its successor Gat Gun 2 as EQUIPPED.
        uint? equipped = _state.AssignedWeapon(classId, slot) is uint eid ? MigrateTier(eid, team) : (uint?)null;

        if (equipped != null)
        {
            var strip = new LoadoutSlot { Accent = DesignTokens.TextDim };
            strip.Configure("⊘", "LEAVE SLOT EMPTY", "");
            strip.Pressed += () =>
            {
                _state.Assign(classId, hpIndex, null);
                RefreshLoadoutViews();
            };
            _arsenalRows.AddChild(strip);
        }

        int fit = 0;
        foreach (WeaponDef w in _defs.AllWeapons())
        {
            if (!LoadoutState.Compatible(slot, w))
                continue;
            // A tier the team has outgrown (an owned tech obsoletes it) is retired from the arsenal
            // entirely. Its successor tier carries the mount instead.
            if (w.ObsoletedByTechIdx.Length > 0 && w.ObsoletedByTechIdx.Any(t => _world.TeamState.OwnsTech(team, t)))
                continue;
            // A weapon gated behind tech the team hasn't fully researched can't be equipped —
            // it's hidden entirely (heavy-cannon is the stock case), not rendered as a locked row.
            if (w.RequiredTechIdx.Length > 0 && !w.RequiredTechIdx.All(t => _world.TeamState.OwnsTech(team, t)))
                continue;
            fit++;
            uint weaponId = w.WeaponId;
            bool isEquipped = equipped == weaponId;
            var row = new LoadoutSlot { Selected = isEquipped };
            row.Configure(
                isEquipped ? "◆ EQUIPPED" : "+ EQUIP",
                w.Name.ToUpperInvariant(),
                $"{WeaponStatLine(w)} · PAYLOAD {w.Mass:0}"
            );
            row.Pressed += () =>
            {
                _state.Assign(classId, hpIndex, weaponId);
                RefreshLoadoutViews();
            };
            _arsenalRows.AddChild(row);
        }
        _arsenalFit.Text = $"{fit} FIT";
    }

    // Walk the weapon-tier successor chain — the DISPLAY mirror of Simulation.ResolveLoadout's
    // server-side migrate (the authoritative one at spawn). Shared with WeaponsPanel via
    // DefRegistry.MigrateWeaponTier; see that method for the predicate-by-predicate rationale.
    private uint MigrateTier(uint weaponId, byte team) => _defs.MigrateWeaponTier(weaponId, team, _world);

    private static string WeaponStatLine(WeaponDef w)
    {
        float rof = w.FireIntervalTicks > 0 ? FlightModel.TickRate / w.FireIntervalTicks : 0f;
        // A healing gun (ER Nanite line) restores hull, so its "Damage" is a heal magnitude — label
        // it HEAL, not DMG, so the arsenal doesn't read a support gun as a damage weapon.
        string mag = w.IsHealing ? $"HEAL {w.Damage:0}" : $"DMG {w.Damage:0}";
        return $"{mag} · {rof:0.#}/s · {w.ProjectileSpeed:0} u/s";
    }

    // Presentation flavor is authored per-hull and streamed (ShipClassDef.Glyph/Role/Description);
    // the empty-string fallbacks are purely cosmetic for a hull that authored none.
    private (string Icon, string Role, string Desc) FlavorOf(byte classId) =>
        _defs.TryGetShipDef(classId, out ShipClassDef d)
            ? (
                d.Glyph.Length > 0 ? d.Glyph : "◇",
                d.Role.Length > 0 ? d.Role : "HULL",
                d.Description.Length > 0 ? d.Description : "Uncatalogued hull."
            )
            : ("◇", "HULL", "Uncatalogued hull.");
}
