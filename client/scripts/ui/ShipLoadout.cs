using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

namespace StellarAllegiance.Ui;

// =====================================================================
//  ShipLoadout.cs — HANGAR / SHIP LOADOUT SCREEN (skeleton)
//
//  Full-screen overlay (F4, or the HANGAR button on the spawn menu) implementing the
//  Claude Design "Ship Loadout" spec against the REAL streamed content: the ship list
//  is DefRegistry.BuildableShips(), the 3D preview is the actual hull ShipModelLoader
//  builds (hardpoints included), and the arsenal is the streamed weapon defs.
//
//  Everything the player edits here lives in LoadoutState, which is CLIENT-LOCAL and
//  cosmetic for now — LAUNCH spawns the hull with the server's authored loadout. The
//  screen exists so the interaction model (select ship -> pick hardpoint via the 3D
//  view or the slot list -> equip from the arsenal -> watch payload) is real before
//  the server-side persistence (MsgSetLoadout) lands.
//
//  Layout mirrors the design: top bar / [ship list | 3D render | hardpoints+arsenal] /
//  launch bar. While open, `Active` gates flight input (ShipController).
//
//  This IS the ship-select screen: the Hud auto-opens it (OpenedForSpawn) whenever
//  you're in an active match without a ship and closes it once the ship spawns —
//  LAUNCH is the only way out of a spawn hangar (no ESC/F4 dismiss). F4 while flying
//  opens it as a browsable loadout viewer instead.
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

    // -- built controls ------------------------------------------------------
    private VBoxContainer _shipList = null!;
    private readonly List<(byte classId, LoadoutSlot row)> _shipRows = new();
    private int _builtShipCount = -1;

    private Label _roleLabel = null!;
    private Label _nameLabel = null!;
    private Label _hullLabel = null!;
    private Label _descLabel = null!;
    private LoadoutPreview _preview = null!;
    private readonly List<(Label value, SegmentedBar bar)> _statBars = new();
    private static readonly string[] StatNames = ["VELOCITY", "ARMOR", "PAYLOAD", "SIGNATURE"];

    private Label _slotCount = null!;
    private VBoxContainer _slotList = null!;
    private readonly List<(byte hpIndex, LoadoutSlot row)> _slotRows = new();
    private Label _payloadText = null!;
    private SegmentedBar _payloadBar = null!;
    private Label _overCapacity = null!;
    private PanelContainer _arsenalFrame = null!;
    private Label _arsenalTitle = null!;
    private Label _arsenalFit = null!;
    private VBoxContainer _arsenalRows = null!;
    private VBoxContainer _cargoList = null!;
    private readonly List<(uint itemId, Label count)> _cargoCounts = new();
    private int _builtCargoCount = -1;

    private Label _topReadout = null!;
    private StatReadout _costReadout = null!;
    private StatReadout _payloadReadout = null!;
    private ChamferButton _launch = null!;
    private ChamferButton _reset = null!;
    private ChamferButton? _firstCargoPlus;
    private Label _launchHint = null!;

    private byte? _classId;
    private byte? _selectedHp;

    // A LAUNCH was clicked and the spawn hasn't landed yet — the button holds
    // "LAUNCHING…" until the ship exists (Hud then closes us) or the gate refuses
    // (ShipController.SpawnHint names why; re-enabled for a retry).
    private bool _launchPending;

    public void Init(DefRegistry defs, ShipController ship, WorldRenderer world, GameNetClient net)
    {
        _defs = defs;
        _ship = ship;
        _world = world;
        _net = net;
    }

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

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 0);
        rows.AddChild(body);
        body.AddChild(BuildShipListColumn());
        body.AddChild(BuildCenterColumn());
        body.AddChild(BuildRightColumn());

        rows.AddChild(BuildLaunchBar());
    }

    // ---- top bar -------------------------------------------------------------

    private Control BuildTopBar()
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.Void, 0.55f), BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
        sb.SetCornerRadiusAll(0);
        sb.BorderWidthBottom = 1;
        sb.ContentMarginLeft = sb.ContentMarginRight = 26;
        sb.ContentMarginTop = sb.ContentMarginBottom = 12;
        panel.AddThemeStyleboxOverride("panel", sb);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 24);
        panel.AddChild(row);

        var dot = new ColorRect { Color = DesignTokens.TeamAccent, CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(dot);
        row.AddChild(UiKit.MakeLabel("STELLAR ALLEGIANCE", UiKit.TextStyle.Label, DesignTokens.TextHi));

        // Tab strip. TECH TREE is a navigation stub until that screen exists in-engine.
        var tabs = UiKit.MakeSegmented(["HANGAR", "TECH TREE"], 0, null);
        tabs.CustomMinimumSize = new Vector2(280, 0);
        row.AddChild(tabs);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(spacer);

        _topReadout = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        row.AddChild(_topReadout);
        return panel;
    }

    // ---- left: ship classes ----------------------------------------------------

    private Control BuildShipListColumn()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        col.AddThemeConstantOverride("separation", 8);

        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 18);
        pad.AddThemeConstantOverride("margin_right", 18);
        pad.AddThemeConstantOverride("margin_top", 18);
        pad.AddChild(col);

        col.AddChild(UiKit.MakeLabel("SHIP CLASS", UiKit.TextStyle.Label, DesignTokens.TextDim));
        _shipList = new VBoxContainer();
        _shipList.AddThemeConstantOverride("separation", 8);
        col.AddChild(_shipList);

        // Defs stream in shortly after connect; until then the list politely waits.
        _shipList.AddChild(UiKit.MakeLabel("AWAITING HULL TELEMETRY…", UiKit.TextStyle.Data, DesignTokens.TextDim));
        return pad;
    }

    private void RebuildShipList(List<ShipClassDef> ships)
    {
        foreach (var child in _shipList.GetChildren())
            child.QueueFree();
        _shipRows.Clear();

        foreach (ShipClassDef def in ships)
        {
            byte classId = def.ClassId;
            (string icon, string role, _) = FlavorOf(classId);
            int weaponSlots = CountWeaponSlots(def);
            var row = new LoadoutSlot();
            row.Configure($"{icon}  {role}", def.Name.ToUpperInvariant(), $"{weaponSlots} HP · {def.Cost} CREDITS");
            row.Pressed += () => SelectShip(classId);
            _shipList.AddChild(row);
            _shipRows.Add((classId, row));
        }
    }

    // ---- center: ship detail + 3D render ---------------------------------------

    private Control BuildCenterColumn()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 12);

        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 32);
        pad.AddThemeConstantOverride("margin_right", 32);
        pad.AddThemeConstantOverride("margin_top", 20);
        pad.AddThemeConstantOverride("margin_bottom", 12);
        pad.AddChild(col);

        var header = new HBoxContainer();
        var titleCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titleCol.AddThemeConstantOverride("separation", 0);
        _roleLabel = UiKit.MakeLabel("", UiKit.TextStyle.Label, DesignTokens.TeamAccent);
        _nameLabel = UiKit.MakeLabel("", UiKit.TextStyle.Display);
        titleCol.AddChild(_roleLabel);
        titleCol.AddChild(_nameLabel);
        header.AddChild(titleCol);
        _hullLabel = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _hullLabel.VerticalAlignment = VerticalAlignment.Top;
        header.AddChild(_hullLabel);
        col.AddChild(header);

        // Render bay: backdrop (hatch/scanline/glow) under the 3D viewport under the
        // marker overlay — PanelContainer stacks all children over the same rect.
        var bay = new BracketPanel { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 260) };
        col.AddChild(bay);
        bay.AddChild(new HoloBackdrop());
        _preview = new LoadoutPreview();
        bay.AddChild(_preview);
        var overlay = new HardpointMarkerOverlay();
        overlay.Init(_preview, IsSlotFilled);
        bay.AddChild(overlay);
        _preview.HardpointClicked += SelectSlot;

        // Stats + description.
        var lower = new HBoxContainer();
        lower.AddThemeConstantOverride("separation", 28);
        col.AddChild(lower);
        var stats = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.3f };
        stats.AddThemeConstantOverride("separation", 10);
        lower.AddChild(stats);
        foreach (string stat in StatNames)
        {
            var head = new HBoxContainer();
            var name = UiKit.MakeLabel(stat, UiKit.TextStyle.Data, DesignTokens.TextDim);
            name.AddThemeFontSizeOverride("font_size", 11);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var value = UiKit.MakeLabel("", UiKit.TextStyle.Data);
            value.AddThemeFontSizeOverride("font_size", 11);
            head.AddChild(name);
            head.AddChild(value);
            var bar = new SegmentedBar { Segments = 24, CustomMinimumSize = new Vector2(0, 8) };
            bar.Fill = stat switch
            {
                "ARMOR" => DesignTokens.Ok,
                "PAYLOAD" => DesignTokens.Warn,
                "SIGNATURE" => DesignTokens.Secondary,
                _ => DesignTokens.TeamAccent,
            };
            stats.AddChild(head);
            stats.AddChild(bar);
            _statBars.Add((value, bar));
        }
        _descLabel = UiKit.MakeLabel("", UiKit.TextStyle.Body, DesignTokens.Data);
        _descLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _descLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lower.AddChild(_descLabel);

        return pad;
    }

    // ---- right: hardpoints + arsenal + cargo ------------------------------------

    private Control BuildRightColumn()
    {
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(380, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            // Fixed-width column: content must fit; long labels clip instead of pushing
            // a horizontal scrollbar (the cargo steppers were sliding off-screen).
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 12);
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 14);
        pad.AddThemeConstantOverride("margin_right", 14);
        pad.AddThemeConstantOverride("margin_top", 18);
        pad.AddThemeConstantOverride("margin_bottom", 18);
        pad.AddChild(col);
        scroll.AddChild(pad);

        var head = new HBoxContainer();
        var title = UiKit.MakeLabel("▶ HARDPOINTS", UiKit.TextStyle.Label, DesignTokens.TextDim);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _slotCount = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        head.AddChild(title);
        head.AddChild(_slotCount);
        col.AddChild(head);

        // Payload capacity readout — placeholder numbers (LoadoutState) with real behavior.
        var payHead = new HBoxContainer();
        var payLabel = UiKit.MakeLabel("PAYLOAD CAPACITY", UiKit.TextStyle.Data, DesignTokens.TextDim);
        payLabel.AddThemeFontSizeOverride("font_size", 11);
        payLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _payloadText = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        payHead.AddChild(payLabel);
        payHead.AddChild(_payloadText);
        col.AddChild(payHead);
        _payloadBar = new SegmentedBar { Segments = PayloadSegments, Fill = DesignTokens.TeamAccent, CustomMinimumSize = new Vector2(0, 8) };
        col.AddChild(_payloadBar);
        _overCapacity = UiKit.MakeLabel("⚠ OVER CAPACITY — strip a slot to launch", UiKit.TextStyle.Data, DesignTokens.DangerText);
        _overCapacity.AddThemeFontSizeOverride("font_size", 11);
        _overCapacity.Visible = false;
        col.AddChild(_overCapacity);

        _slotList = new VBoxContainer();
        _slotList.AddThemeConstantOverride("separation", 7);
        col.AddChild(_slotList);

        col.AddChild(new DiamondDivider());

        // Arsenal frame — tinted container listing what fits the selected slot.
        _arsenalFrame = new PanelContainer { Visible = false };
        var frameSb = new StyleBoxFlat { BgColor = new Color(DesignTokens.TeamAccentBase, 0.08f), BorderColor = DesignTokens.TeamAccentBase, AntiAliasing = false };
        frameSb.SetCornerRadiusAll(0);
        frameSb.SetBorderWidthAll(1);
        frameSb.SetContentMarginAll(12);
        _arsenalFrame.AddThemeStyleboxOverride("panel", frameSb);
        var frameCol = new VBoxContainer();
        frameCol.AddThemeConstantOverride("separation", 8);
        _arsenalFrame.AddChild(frameCol);
        var frameHead = new HBoxContainer();
        _arsenalTitle = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Data);
        _arsenalTitle.AddThemeFontSizeOverride("font_size", 11);
        _arsenalTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _arsenalFit = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Text2);
        _arsenalFit.AddThemeFontSizeOverride("font_size", 10);
        frameHead.AddChild(_arsenalTitle);
        frameHead.AddChild(_arsenalFit);
        frameCol.AddChild(frameHead);
        _arsenalRows = new VBoxContainer();
        _arsenalRows.AddThemeConstantOverride("separation", 7);
        frameCol.AddChild(_arsenalRows);
        col.AddChild(_arsenalFrame);

        col.AddChild(BuildCargoSection());
        return scroll;
    }

    private Control BuildCargoSection()
    {
        var panel = new HairlinePanel { Title = "CARGO HOLD" };
        _cargoList = new VBoxContainer();
        _cargoList.AddThemeConstantOverride("separation", 6);
        panel.AddChild(_cargoList);
        // Rows are streamed content (CargoItemDef) — populated by RefreshCargoSection once
        // the defs land (_Process), never from baked stubs.
        return panel;
    }

    private void RefreshCargoSection(List<CargoItemDef> items)
    {
        foreach (var child in _cargoList.GetChildren())
            child.QueueFree();
        _cargoCounts.Clear();
        _firstCargoPlus = null;

        foreach (CargoItemDef item in items)
        {
            uint itemId = item.CargoId;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var glyph = UiKit.MakeLabel(item.Glyph, UiKit.TextStyle.Data, DesignTokens.Secondary);
            glyph.CustomMinimumSize = new Vector2(20, 0);
            row.AddChild(glyph);
            var nameCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            nameCol.AddThemeConstantOverride("separation", 0);
            var name = UiKit.MakeLabel(item.Name.ToUpperInvariant(), UiKit.TextStyle.Data, DesignTokens.TextHi);
            name.AddThemeFontSizeOverride("font_size", 12);
            // Dispensers load in PACKS of ChargesPerPack charges (one per press); show the multiplier
            // so the count reads as packs. Legacy single-charge items (ChargesPerPack 1) stay "EA".
            string cargoSub = item.ChargesPerPack > 1
                ? $"{item.Mass:0} PAYLOAD/PACK · {item.ChargesPerPack}× CHARGES · {item.Description}"
                : $"{item.Mass:0} PAYLOAD EA · {item.Description}";
            var sub = UiKit.MakeLabel(cargoSub, UiKit.TextStyle.Data, DesignTokens.TextDim);
            sub.AddThemeFontSizeOverride("font_size", 9);
            sub.ClipText = true;
            sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            nameCol.AddChild(name);
            nameCol.AddChild(sub);
            row.AddChild(nameCol);

            // Count stepper (− NN +). Rebuilt cheap: the count label is ours, the
            // buttons write straight into LoadoutState.
            var minus = UiKit.MakeButton("−", null, ButtonVariant.Secondary);
            var plus = UiKit.MakeButton("+", null, ButtonVariant.Secondary);
            minus.CustomMinimumSize = plus.CustomMinimumSize = new Vector2(30, 28);
            var count = UiKit.MakeLabel("00", UiKit.TextStyle.Data);
            count.CustomMinimumSize = new Vector2(32, 0);
            count.HorizontalAlignment = HorizontalAlignment.Center;
            count.VerticalAlignment = VerticalAlignment.Center;
            if (_classId is byte classId)
                count.Text = _state.GetCargoCount(classId, itemId).ToString("00");
            minus.Pressed += () => StepCargo(itemId, -1, count);
            plus.Pressed += () => StepCargo(itemId, +1, count);
            _firstCargoPlus ??= plus;
            row.AddChild(minus);
            row.AddChild(count);
            row.AddChild(plus);
            _cargoList.AddChild(row);
            _cargoCounts.Add((itemId, count));
        }
    }

    private void StepCargo(uint itemId, int delta, Label count)
    {
        if (_classId is not byte classId || !_defs.TryGetShipDef(classId, out ShipClassDef def))
            return;
        int cur = _state.GetCargoCount(classId, itemId);
        int want = Math.Clamp(cur + delta, 0, 12);
        // The per-kind charge total (packs × ChargesPerPack) rides a wire byte — never let the pack
        // count push past 255 charges (the hard 12-pack cap already covers sane pack sizes).
        if (_defs.GetCargoItem(itemId) is CargoItemDef packItem && packItem.ChargesPerPack > 1)
            want = Math.Min(want, 255 / packItem.ChargesPerPack);
        // A bump is additionally clamped to the hull's REMAINING payload budget — you can't stock
        // past what the hull can carry (the server would just fall back to the hull default anyway).
        if (want > cur && _defs.GetCargoItem(itemId) is CargoItemDef item && item.Mass > 0f)
        {
            float used = _state.PayloadUsed(classId, def.Hardpoints, _defs.GetWeapon, _defs.GetCargoItem);
            int canAdd = Mathf.FloorToInt((def.PayloadCapacity - used) / item.Mass);
            if (canAdd < want - cur)
                want = cur + Math.Max(0, canAdd);
        }
        _state.SetCargoCount(classId, itemId, want);
        count.Text = want.ToString("00");
        RefreshPayload();
    }

    // ---- bottom: launch bar ------------------------------------------------------

    private Control BuildLaunchBar()
    {
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(DesignTokens.Void, 0.6f), BorderColor = DesignTokens.BorderHi, AntiAliasing = false };
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
        row.AddChild(_costReadout);
        row.AddChild(_payloadReadout);
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
    // The local weapon/cargo assignments do NOT ship with the request — the server
    // spawns the authored loadout until MsgSetLoadout exists.
    private void OnLaunch()
    {
        if (_classId is not byte classId)
            return;
        _launchPending = true;
        _ship.RequestSpawn((ShipClass)classId);
    }

    private void Close() => QueueFree();

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
        // to selection (LAUNCH/Enter-free so chat and launch stay deliberate).
        int slot = (int)(key.Keycode - Key.Key1);
        if (slot >= 0 && slot < 9)
        {
            List<ShipClassDef> ships = _defs.BuildableShips();
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

        // Build (or rebuild) the ship list once the streamed defs land / change.
        List<ShipClassDef> ships = _defs.BuildableShips();
        if (ships.Count != _builtShipCount && ships.Count > 0)
        {
            _builtShipCount = ships.Count;
            RebuildShipList(ships);
            SelectShip(_classId ?? ships[0].ClassId);
        }

        // Same for the cargo hold — its items are streamed defs too.
        List<CargoItemDef> cargoItems = _defs.AllCargoItems();
        if (cargoItems.Count != _builtCargoCount)
        {
            _builtCargoCount = cargoItems.Count;
            RefreshCargoSection(cargoItems);
        }

        // Live telemetry + spawn gating.
        byte team = _world.LocalTeam ?? _net.MyTeam;
        _topReadout.Text = $"CREDITS {_world.TeamCredits(team)}   ·   PING {_ship.PingMs,3:0} ms";
        RefreshLaunchGate(team);
    }

    private void RefreshLaunchGate(byte team)
    {
        if (_classId is not byte classId || !_defs.TryGetShipDef(classId, out var def))
            return;
        bool flying = _world.LocalShip != null;
        string? hint = _ship.SpawnHint;
        if (flying || hint != null)
            _launchPending = false; // landed, or refused (the hint names why) — let the pilot retry
        bool overCap = IsOverCapacity(def);
        var gate = _world.CheckSpawnGate(team, classId);
        _launch.Disabled = overCap || flying || _launchPending || gate != WorldRenderer.SpawnGate.Allow;
        _launch.Text = flying
            ? "IN FLIGHT"
            : _launchPending
                ? "LAUNCHING…"
                : gate == WorldRenderer.SpawnGate.Locked
                    ? "⚿ LOCKED"
                    : overCap
                        ? "OVER CAPACITY"
                        : "◆ LAUNCH";
        _launchHint.Visible = hint != null;
        if (hint != null)
            _launchHint.Text = hint;
    }

    private void SelectShip(byte classId)
    {
        if (!_defs.TryGetShipDef(classId, out ShipClassDef def))
            return;
        _classId = classId;
        _selectedHp = null;
        _state.SeedDefaults(classId, def); // open on the hull's authored default hold (once per class)

        foreach ((byte id, LoadoutSlot row) in _shipRows)
            row.Selected = id == classId;

        (string _, string role, string desc) = FlavorOf(classId);
        _roleLabel.Text = role;
        _nameLabel.Text = def.Name.ToUpperInvariant();
        _hullLabel.Text = $"CLASS HULL\n{def.MaxHull:0} HP";
        _descLabel.Text = desc;

        RefreshStatBars(def);
        _preview.ShowShip(_defs, classId);
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

        int slots = 0;
        foreach (HardpointDef hp in hps)
        {
            if (hp.Kind != HardpointKind.Weapon)
                continue; // engines/thrusters/docking aren't assignable — 3D dots only
            slots++;
            byte hpIndex = hp.Index;
            WeaponDef? w = _state.AssignedWeapon(classId, hp) is uint id ? _defs.GetWeapon(id) : null;
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
        _payloadText.Text = $"{used:0} / {cap:0}";
        _payloadText.AddThemeColorOverride("font_color", over ? DesignTokens.DangerText : DesignTokens.Data);
        _payloadBar.Fill = over ? DesignTokens.Danger : DesignTokens.TeamAccent;
        _payloadBar.Set(cap <= 0f ? 0 : Mathf.RoundToInt(Mathf.Min(1f, used / cap) * PayloadSegments)); // pod: no hold
        _payloadBar.QueueRedraw(); // Fill changes don't self-invalidate
        _overCapacity.Visible = over;
        _payloadReadout.Set($"{used:0}/{cap:0}", "PAYLOAD", over ? DesignTokens.DangerText : null);
        _costReadout.Set($"{def.Cost}", "UNIT COST · CREDITS", DesignTokens.Warn);
    }

    private bool IsOverCapacity(ShipClassDef def) =>
        _classId is byte classId && _state.PayloadUsed(classId, def.Hardpoints, _defs.GetWeapon, _defs.GetCargoItem) > def.PayloadCapacity;

    // The arsenal: every streamed weapon that fits the selected slot, plus the empty-slot
    // row and a placeholder tech-locked entry (the lock becomes real with the tech tree).
    private void RefreshArsenal()
    {
        foreach (var child in _arsenalRows.GetChildren())
            child.QueueFree();

        if (_classId is not byte classId || _selectedHp is not byte hpIndex || _defs.GetHardpoints(classId) is not List<HardpointDef> hps)
        {
            _arsenalFrame.Visible = false;
            return;
        }
        HardpointDef? slot = null;
        foreach (HardpointDef hp in hps)
            if (hp.Kind == HardpointKind.Weapon && hp.Index == hpIndex)
                slot = hp;
        if (slot == null)
        {
            _arsenalFrame.Visible = false;
            return;
        }

        _arsenalFrame.Visible = true;
        _arsenalTitle.Text = $"[P{hpIndex + 1}]  WEAPON HARDPOINT";
        uint? equipped = _state.AssignedWeapon(classId, slot);

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

        // Tech-locked affordance: real once the tech tree gates weapon defs.
        var locked = new LoadoutSlot { Accent = DesignTokens.TextDim };
        locked.Configure("⚿ LOCKED", "LANCE CANNON", "REQUIRES TECH II — TECH TREE (SOON)");
        locked.Modulate = new Color(1, 1, 1, 0.6f);
        _arsenalRows.AddChild(locked);
    }

    private static string WeaponStatLine(WeaponDef w)
    {
        float rof = w.FireIntervalTicks > 0 ? FlightModel.TickRate / w.FireIntervalTicks : 0f;
        return $"DMG {w.Damage:0} · {rof:0.#}/s · {w.ProjectileSpeed:0} u/s";
    }

    // Presentation flavor is authored per-hull and streamed (ShipClassDef.Glyph/Role/Description);
    // the empty-string fallbacks are purely cosmetic for a hull that authored none.
    private (string Icon, string Role, string Desc) FlavorOf(byte classId) =>
        _defs.TryGetShipDef(classId, out ShipClassDef d)
            ? (d.Glyph.Length > 0 ? d.Glyph : "◇",
               d.Role.Length > 0 ? d.Role : "HULL",
               d.Description.Length > 0 ? d.Description : "Uncatalogued hull.")
            : ("◇", "HULL", "Uncatalogued hull.");

    // ---- --hangar-demo=<dir>: scripted self-drive for screenshot verification --------
    // Synthesizes real mouse events through Input.ParseInputEvent (the normal viewport
    // input pipeline — SubViewport routing, drag/click logic, the hardpoint raycast, the
    // Control buttons), snapshotting after each step. Pair with --hangar; quits when done.

    private string? _demoDir;
    private int _demoStep;
    private double _demoWait = 1.2; // let the first hull + defs settle

    private void RunDemo(double delta)
    {
        _demoWait -= delta;
        if (_demoWait > 0 || _classId == null)
            return;
        _demoWait = 0.8;
        switch (_demoStep++)
        {
            case 0: Snap("01-open"); break;
            case 1: DragPreview(new Vector2(150, -40)); break;
            case 2: Snap("02-rotated"); break;
            case 3: ClickFirstMarker(); break;
            case 4: Snap("03-slot-selected"); break;
            case 5: ClickArsenalRow(); break;
            case 6: Snap("04-equipped"); break;
            case 7: ClickAt(_firstCargoPlus!.GetGlobalRect().GetCenter()); break;
            case 8: ClickAt(_firstCargoPlus!.GetGlobalRect().GetCenter()); break;
            case 9: Snap("05-overcap"); break;
            case 10: ClickAt(_reset.GetGlobalRect().GetCenter()); break;
            case 11: Snap("06-reset"); break;
            case 12: _demoLaunched = true; ClickAt(_launch.GetGlobalRect().GetCenter()); break;
            // Only reached if the spawn never landed — the ship spawning closes this
            // screen first and DemoAfterLaunch takes the final shot instead.
            case 13: Snap("07-launch-stuck"); GetTree().Quit(); break;
        }
    }

    private bool _demoLaunched;

    // The demo's LAUNCH landed: this hangar was auto-closed by the Hud, so the final
    // "back in flight" frame is captured from the surviving tree, then quit.
    private void DemoAfterLaunch()
    {
        if (_demoDir is not string dir || !_demoLaunched)
            return;
        SceneTree tree = GetTree();
        SceneTreeTimer t = tree.CreateTimer(1.0);
        t.Timeout += () =>
        {
            tree.Root.GetTexture().GetImage().SavePng($"{dir}/07-after-launch.png");
            GD.Print("HANGAR_DEMO_SHOT:07-after-launch");
            tree.Quit();
        };
    }

    private void Snap(string name)
    {
        GetViewport().GetTexture().GetImage().SavePng($"{_demoDir}/{name}.png");
        GD.Print($"HANGAR_DEMO_SHOT:{name}");
    }

    private static void ClickAt(Vector2 pos)
    {
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pos, GlobalPosition = pos });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pos, GlobalPosition = pos });
    }

    private void DragPreview(Vector2 total)
    {
        Vector2 c = _preview.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = c, GlobalPosition = c });
        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            Vector2 p = c + total * i / steps;
            Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, Relative = total / steps });
        }
        Vector2 end = c + total;
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end, GlobalPosition = end });
    }

    private void ClickFirstMarker()
    {
        foreach (LoadoutPreview.Mount m in _preview.Mounts)
            if (m.Assignable && _preview.MountScreenPos(m) is Vector2 sp)
            {
                ClickAt(_preview.GetGlobalRect().Position + sp);
                return;
            }
        GD.PrintErr("HANGAR_DEMO: no assignable marker on screen");
    }

    // Equip the last weapon row (just above the locked placeholder) so the demo swaps
    // away from the authored default.
    private void ClickArsenalRow()
    {
        int n = _arsenalRows.GetChildCount();
        if (n < 2)
        {
            GD.PrintErr("HANGAR_DEMO: arsenal not open");
            return;
        }
        var row = _arsenalRows.GetChild<Control>(n - 2);
        ClickAt(row.GetGlobalRect().GetCenter());
    }

    private static int CountWeaponSlots(ShipClassDef def)
    {
        int n = 0;
        foreach (HardpointDef hp in def.Hardpoints)
            if (hp.Kind == HardpointKind.Weapon)
                n++;
        return n;
    }
}

// Render-bay backdrop: the design's diagonal hatch, radial glow, and sweeping scanline,
// drawn behind the (transparent-background) 3D viewport.
public partial class HoloBackdrop : Control
{
    private float _scan; // 0..1 sweep position

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public override void _Process(double delta)
    {
        _scan = (_scan + (float)delta / 4f) % 1.2f; // 4 s sweep + a beat off-screen
        QueueRedraw();
    }

    public override void _Draw()
    {
        // Radial glow, approximated with a few concentric alpha circles.
        var center = new Vector2(Size.X * 0.5f, Size.Y * 0.6f);
        float radius = Mathf.Min(Size.X, Size.Y) * 0.55f;
        for (int i = 3; i >= 1; i--)
            DrawCircle(center, radius * i / 3f, new Color(DesignTokens.TeamAccentBase, 0.022f));

        // Diagonal hatch.
        var hatch = new Color(0.47f, 0.75f, 1f, 0.045f);
        for (float x = -Size.Y; x < Size.X; x += 16f)
            DrawLine(new Vector2(x, 0), new Vector2(x + Size.Y, Size.Y), hatch, 6f);

        // Scanline sweep.
        float y = _scan * Size.Y * 1.2f - Size.Y * 0.1f;
        const float band = 40f;
        for (int i = 0; i < 4; i++)
        {
            float a = 0.05f * (4 - i) / 4f;
            DrawRect(new Rect2(0, y + i * band / 4f, Size.X, band / 4f), new Color(DesignTokens.TeamAccentBase, a), filled: true);
        }
    }
}

// Screen-space hardpoint markers over the 3D preview: a dot + mono tag per weapon mount
// (filled = weapon assigned, hollow = empty, pulsing ring = selected, bright = hovered)
// and inert dim dots for the non-assignable hardpoints. Lives OUTSIDE the SubViewport
// (own-world) and reprojects through the preview camera every frame.
public partial class HardpointMarkerOverlay : Control
{
    private LoadoutPreview _preview = null!;
    private Func<byte, bool> _isFilled = _ => false;

    public void Init(LoadoutPreview preview, Func<byte, bool> isFilled)
    {
        _preview = preview;
        _isFilled = isFilled;
    }

    public override void _Ready() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        // Legend + rotate hint (static chrome, drawn with the markers to stay one pass).
        DrawString(UiFonts.Mono, new Vector2(14, 20), "● WEAPON MOUNT", HorizontalAlignment.Left, -1, 10, DesignTokens.TeamAccent);
        DrawString(UiFonts.Mono, new Vector2(14, 34), "· SYSTEM", HorizontalAlignment.Left, -1, 10, DesignTokens.TextDim);
        DrawString(UiFonts.Mono, new Vector2(14, Size.Y - 12), "ROTATE ◄ ► · SCROLL ZOOM · CLICK MOUNT", HorizontalAlignment.Left, -1, 10, DesignTokens.Text2);

        if (_preview == null)
            return;
        foreach (LoadoutPreview.Mount m in _preview.Mounts)
        {
            if (_preview.MountScreenPos(m) is not Vector2 sp)
                continue;
            // Overlay and preview share the same rect, so preview coords are ours.
            if (!m.Assignable)
            {
                DrawCircle(sp, 2f, new Color(DesignTokens.TextDim, 0.5f));
                continue;
            }

            bool selected = _preview.SelectedIndex == m.Hp.Index;
            bool hovered = _preview.HoverIndex == m.Hp.Index;
            bool filled = _isFilled(m.Hp.Index);
            Color c = DesignTokens.TeamAccent;
            float r = selected ? 9f : hovered ? 8f : 6.5f;
            if (selected)
            {
                // Pulsing halo, the design's saMarker glow.
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.GetTicksMsec() / 220f);
                DrawCircle(sp, r + 4f + pulse * 3f, new Color(c, 0.12f + 0.10f * pulse));
            }
            DrawCircle(sp, r, filled ? c : new Color(DesignTokens.Void, 0.75f));
            DrawArc(sp, r, 0, Mathf.Tau, 24, c, 2f, true);

            string tag = $"P{m.Hp.Index + 1}";
            Vector2 sz = UiFonts.Mono.GetStringSize(tag, HorizontalAlignment.Left, -1, 10);
            var tagPos = sp + new Vector2(-sz.X * 0.5f, r + 14f);
            DrawRect(new Rect2(tagPos + new Vector2(-3, -10), sz + new Vector2(6, 4)), new Color(DesignTokens.Void, 0.7f), filled: true);
            DrawString(UiFonts.Mono, tagPos, tag, HorizontalAlignment.Left, -1, 10, selected || hovered ? c : DesignTokens.Text2);
        }
    }
}
