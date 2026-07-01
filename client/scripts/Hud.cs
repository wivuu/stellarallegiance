using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Ui;

// Heads-up display. The Lobby overlay (a child created here) owns the pre/post-match
// UI; the Hud's own spawn menu only appears once you're teamed in an active match and
// not currently flying, and while flying it shows a speed + reconcile readout.
// The 1/2 keyboard shortcuts in ShipController do the same thing as the spawn buttons.
public partial class Hud : CanvasLayer
{
    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;
    private ShipController _ship = null!;
    private GameNetClient _net = null!;
    private DefRegistry _defs = null!;
    private Label _label = null!;
    private Label _sectorShips = null!;
    private Label _credits = null!;
    private VBoxContainer _menu = null!;
    private Label _spawnHint = null!;
    private Label _warning = null!;

    // The design-system gallery overlay (F9), instantiated on demand.
    private Control? _showcase;

    // The hangar / ship-loadout overlay (F4 or the HANGAR button), instantiated on demand.
    private ShipLoadout? _hangar;

    // The buy-menu buttons, rebuilt from the streamed ship defs once they arrive (BuildSpawnMenu).
    // Each carries its ClassId so _Process can refresh price/affordability/lock state per frame. The
    // count of buttons currently built, so the menu only rebuilds when the buildable set changes.
    private readonly System.Collections.Generic.List<(byte classId, ChamferButton button)> _spawnButtons = new();
    private int _builtShipCount = -1;

    // Previous-frame visibility, so UI sounds fire once on the transition (the spawn
    // menu opening/closing, the sector warning first appearing) rather than every frame.
    private bool _menuWasVisible;
    private bool _warnWasVisible;

    // `--hangar`: auto-open the loadout screen once the ship defs land — the hangar
    // counterpart of --ui-showcase for screenshot verification (pairs with --ui-shot).
    private bool _autoHangar;

    public override void _Ready()
    {
        // Boot straight into the design-system gallery for headless screenshot CI:
        //   godot --path client -- --ui-showcase
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ui-showcase") >= 0)
        {
            CallDeferred(nameof(LoadShowcaseScene));
            return;
        }

        _autoHangar = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--hangar") >= 0;

        _cm = GetNode<ConnectionManager>("../ConnectionManager");
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _ship = GetNode<ShipController>("../ShipController");
        _net = GetNode<GameNetClient>("../GameNetClient");
        _defs = GetNode<DefRegistry>("../DefRegistry");

        // Sun lens flare (added first so it sits UNDER every HUD element while still drawing over
        // the 3D viewport — it's a light effect on the sky, not a readout).
        var flare = new LensFlare { Name = "LensFlare" };
        AddChild(flare);
        flare.Init(GetNode<Camera3D>("../Camera3D"));

        // Enemy target markers (added first so the HUD text/menu draw on top of it).
        var markers = new TargetMarkers { Name = "TargetMarkers" };
        AddChild(markers);
        markers.Init(_world, GetNode<Camera3D>("../Camera3D"));

        // Prograde velocity marker (direction of travel, not aim). Drawn under the text/menu.
        var velo = new VelocityIndicator { Name = "VelocityIndicator" };
        AddChild(velo);
        velo.Init(_world, GetNode<Camera3D>("../Camera3D"));

        // HULL + BOOST system ring: concentric arc gauges framing the aim reticle. Added here
        // so the top-left text/menu still draw over it. Reads the local ship's hull + boost ramp.
        var systemRing = new SystemRing { Name = "SystemRing" };
        AddChild(systemRing);
        systemRing.Init(_world, GetNode<Camera3D>("../Camera3D"));

        // Always-on sector minimap, bottom-left.
        var minimap = new Minimap { Name = "Minimap" };
        AddChild(minimap);
        minimap.Init(_cm, _world);

        // Active-ship count for the local sector, pinned to the very top-left. Hidden until a
        // match is live (the lobby overlay owns the screen otherwise). Telemetry → mono Data style.
        _sectorShips = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _sectorShips.Position = new Vector2(16, 12);
        _sectorShips.Visible = false;
        AddChild(_sectorShips);

        _label = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _label.Position = new Vector2(16, 38);
        AddChild(_label);

        // Team credits readout (Stage-2 economy), under the flight/controls line. Hidden until a
        // match is live. The Secondary token replaces the old inline gold.
        _credits = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Secondary);
        _credits.Position = new Vector2(16, 64);
        _credits.Visible = false;
        AddChild(_credits);

        // Buy menu: one button per buildable ship class, built lazily from the streamed defs once they
        // arrive (BuildSpawnMenu, driven from _Process) and refreshed each frame for price/balance/lock
        // state. Shown only when the player has no ship. The 1/2/3 keys still spawn the first three.
        _menu = new VBoxContainer { Position = new Vector2(16, 90) };
        AddChild(_menu);

        // One-line feedback when a buy is suppressed by the client pre-check (locked / can't afford).
        // The buttons gray out the unaffordable/locked options; this names the reason for a queued buy.
        _spawnHint = UiKit.MakeLabel("", UiKit.TextStyle.Body, DesignTokens.Warn);
        _spawnHint.Position = new Vector2(16, 200);
        _spawnHint.Visible = false;
        AddChild(_spawnHint);

        // Out-of-bounds warning (sector boundary): centered in the upper third, hidden
        // until the local ship strays past its sector radius and starts taking damage.
        _warning = UiKit.MakeLabel("", UiKit.TextStyle.Display, DesignTokens.Danger);
        _warning.Visible = false;
        _warning.HorizontalAlignment = HorizontalAlignment.Center;
        _warning.AnchorRight = 1f;
        _warning.OffsetTop = 90f;
        AddChild(_warning);

        // Lobby / pre-match / post-match overlay. Owns the team picker, ready-up, and
        // end screen. Only shows once actually connected (see Lobby._Process).
        var lobby = new Lobby { Name = "Lobby" };
        AddChild(lobby);
        lobby.Init(_cm, _world);

        // Chat overlay (added after the lobby so its log/input draw above the lobby
        // backdrop). Owns Enter-to-type, the team/all channel, and dev commands.
        var chat = new Chat { Name = "Chat" };
        AddChild(chat);
        chat.Init(_cm, _world);

        // Connection-status overlay (added last so it draws on top of everything,
        // including the lobby). Shows "Server offline" / "Connecting…" until we're live.
        var conn = new ConnectionOverlay { Name = "ConnectionOverlay" };
        AddChild(conn);
        conn.Init(_cm);

        CaptureLiveUiIfRequested();
    }

    private void LoadShowcaseScene() => GetTree().ChangeSceneToFile("res://scenes/UiShowcase.tscn");

    // `--ui-shot=<path>` (without --ui-showcase) screenshots the live game UI after a short
    // settle and quits — used to verify the migrated screens render with the design system.
    private void CaptureLiveUiIfRequested()
    {
        string? outPath = null;
        foreach (string a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--ui-shot="))
                outPath = a.Substring("--ui-shot=".Length);
        if (outPath == null)
            return;
        var t = GetTree().CreateTimer(2.0);
        t.Timeout += () =>
        {
            GetViewport().GetTexture().GetImage().SavePng(outPath);
            GD.Print("UI_SHOT_SAVED:" + ProjectSettings.GlobalizePath(outPath));
            GetTree().Quit();
        };
    }

    // F9 toggles the design-system gallery as a live overlay, for eyeballing the shared
    // components against the real screens. The showcase self-themes, so it can hang
    // straight off this CanvasLayer.
    public override void _ShortcutInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F9 })
        {
            if (_showcase != null && GodotObject.IsInstanceValid(_showcase))
            {
                _showcase.QueueFree();
                _showcase = null;
            }
            else
            {
                _showcase = new UiShowcase();
                AddChild(_showcase);
            }
            GetViewport().SetInputAsHandled();
        }

        // F4 toggles the hangar / loadout screen (same lifecycle as the showcase). Guarded
        // on _defs: in --ui-showcase boot the game nodes were never resolved.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F4 } && _defs != null)
        {
            ToggleHangar();
            GetViewport().SetInputAsHandled();
        }
    }

    private void ToggleHangar()
    {
        if (_hangar != null && GodotObject.IsInstanceValid(_hangar))
        {
            _hangar.QueueFree();
            _hangar = null;
        }
        else
        {
            _hangar = new ShipLoadout();
            _hangar.Init(_defs, _ship, _world, _net);
            AddChild(_hangar);
        }
    }

    // Rebuild the buy menu from the current ship defs — one button per buildable hull, in ClassId order.
    // Called once the defs arrive (and again if the buildable set changes); per-frame price/lock state
    // is applied in RefreshSpawnButtons. Reuses the Lobby.MakeButton click+sfx pattern.
    private void BuildSpawnMenu(System.Collections.Generic.IReadOnlyList<StellarAllegiance.Shared.ShipClassDef> ships)
    {
        foreach (var child in _menu.GetChildren())
            child.QueueFree();
        _spawnButtons.Clear();
        foreach (var def in ships)
        {
            byte classId = def.ClassId;
            // ChamferButton (Primary) bakes in the UI click sound, so we only wire the spawn.
            var b = new ChamferButton { Variant = ButtonVariant.Primary, CustomMinimumSize = new Vector2(300, 36) };
            b.Pressed += () => _ship.RequestSpawn((ShipClass)classId);
            _menu.AddChild(b);
            _spawnButtons.Add((classId, b));
        }

        // The hangar entry point: same overlay as F4, opened from the buy menu where hull
        // choice already happens. Secondary variant so the spawn buttons stay the headline.
        var hangar = new ChamferButton
        {
            Variant = ButtonVariant.Secondary,
            Text = "Hangar — loadout  [F4]",
            CustomMinimumSize = new Vector2(300, 36),
        };
        hangar.Pressed += ToggleHangar;
        _menu.AddChild(hangar);
    }

    // Per-frame buy-menu state: each button shows "Spawn <name>  [hotkey] — <cost> credits" and grays
    // out (Disabled) when the team can't afford it or the hull is locked — mirroring the server's spawn
    // gate via WorldRenderer.CheckSpawnGate. Team pre-spawn = the lobby/Welcome assignment (LocalTeam is
    // null until a ship exists), matching ShipController's gate path so the menu agrees with the server.
    private void RefreshSpawnButtons()
    {
        byte team = _world.LocalTeam ?? _net.MyTeam;
        foreach (var (classId, button) in _spawnButtons)
        {
            if (!_defs.TryGetShipDef(classId, out var def))
                continue;
            var gate = _world.CheckSpawnGate(team, classId);
            button.Disabled = gate != WorldRenderer.SpawnGate.Allow;
            string hotkey = classId < 3 ? $"  [{classId + 1}]" : "";
            string suffix = gate == WorldRenderer.SpawnGate.Locked ? "   (locked)" : "";
            button.Text = $"Spawn {def.Name}{hotkey} — {def.Cost} credits{suffix}";
        }
    }

    public override void _Process(double delta)
    {
        var ship = _world.LocalShip;
        bool flying = ship != null;

        if (_autoHangar && _defs.BuildableShips().Count > 0)
        {
            _autoHangar = false;
            ToggleHangar();
        }

        // The Lobby overlay owns everything outside a live match. The spawn menu appears once a
        // match is Active and you're not currently flying — keying off "not flying" means it also
        // reopens when a pod is destroyed/docked and you're awaiting your next ship.
        bool inMatch = _world.Phase == MatchPhase.Active;
        bool teamedInMatch = inMatch;
        // The hangar overlay covers the screen; hide the buy menu under it so there's a
        // single spawn path while it's open (its LAUNCH button).
        _menu.Visible = teamedInMatch && !flying && !ShipLoadout.Active;
        if (_menu.Visible != _menuWasVisible)
        {
            SfxManager.Instance?.PlayUi(_menu.Visible ? SfxManager.SfxId.MenuOpen : SfxManager.SfxId.MenuClose);
            _menuWasVisible = _menu.Visible;
        }

        // Buy menu, built from the streamed defs (they arrive once, after Welcome). Rebuild when the
        // buildable set changes; refresh price/balance/lock state each frame the menu is up.
        if (_menu.Visible)
        {
            var ships = _defs.BuildableShips();
            if (ships.Count != _builtShipCount)
            {
                BuildSpawnMenu(ships);
                _builtShipCount = ships.Count;
            }
            RefreshSpawnButtons();
        }

        _sectorShips.Visible = inMatch;
        if (inMatch)
            _sectorShips.Text = $"Ships in sector: {_world.ShipsInLocalSector()}";

        // Running team balance (server-authoritative; accrues on the paycheck cadence). Same team
        // source as the buy menu so the balance shown matches what gates the buttons.
        _credits.Visible = inMatch;
        if (inMatch)
            _credits.Text = $"Credits: {_world.TeamCredits(_world.LocalTeam ?? _net.MyTeam)}";

        // Buy feedback: only while the spawn menu is up, and only when the pre-check flagged a reason.
        string? hint = _menu.Visible ? _ship.SpawnHint : null;
        _spawnHint.Visible = hint != null;
        if (hint != null)
            _spawnHint.Text = hint;

        // Sector boundary: warn (and pulse) once the ship is past the radius, where the
        // server is eroding the hull. Distance is measured from the local sector center.
        float radius = _world.LocalSectorRadius;
        if (flying && radius > 0f)
        {
            float dist = (ship!.Position - _world.LocalSectorCenter).Length();
            if (dist > radius)
            {
                float over = dist - radius;
                _warning.Text = $"⚠  LEAVING SECTOR — HULL FAILING  ⚠\nreturn to bounds ({over:0} u out)";
                _warning.Visible = true;
            }
            else
            {
                _warning.Visible = false;
            }
        }
        else
        {
            _warning.Visible = false;
        }
        if (_warning.Visible && !_warnWasVisible)
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiNotify);
        _warnWasVisible = _warning.Visible;
        // Top-left readout: the controls hint while choosing a ship (teamed, pre-spawn),
        // the live flight stats while flying, and nothing while the lobby overlay is up.
        _label.Text = flying
            ? ship!.IsPod
                // Ejected: flying the escape pod. Just the resolve hint — hull/speed now read
                // off the HULL gauge and the velocity marker (the pod is unarmed, just fleeing).
                ? "⚠  EJECTED — reach a friendly base or get rescued"
                // HP + Speed are shown graphically (HULL gauge / velocity marker), so the text
                // line keeps only the network telemetry: ping and reconcile stats.
                : $"Ping: {_ship.PingMs, 3:0} ms (±{_ship.JitterMs:0})   Reconciles: {ship.ReconcileCount} (last err {ship.LastReconcileError:0.0}u)"
            : teamedInMatch
                ? "Choose your ship:\nW/S throttle · Shift afterburner · A/D strafe · E/C up·down · mouse aim (Esc frees cursor) · Q/Z roll · click/Space fire · Tab focus target"
                : "";
    }
}
