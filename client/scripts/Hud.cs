using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// Heads-up display. The Lobby overlay (a child created here) owns the pre/post-match
// UI; ship selection is the ShipLoadout hangar overlay, auto-opened whenever you're in
// an active match without a ship AFTER you've deployed once (first spawn, then respawn
// after docking or death — deploy intent is sticky for the match) and closed once the
// ship exists. While flying the Hud shows a speed + reconcile readout; F4 reopens the
// hangar read-only (LAUNCH gated to "IN FLIGHT").
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
    private Label _warning = null!;

    // Edge-detect the secondary-fire keys so an empty-rack click plays its "no rounds" blip once
    // per press (not every held frame), and a short cooldown so mashing F doesn't machine-gun it.
    private bool _firing2Held;
    private double _emptyClickCd;

    // The design-system gallery overlay (F9), instantiated on demand.
    private Control? _showcase;

    // The hangar / ship-loadout overlay (F4 or the HANGAR button), instantiated on demand.
    private ShipLoadout? _hangar;

    // Deploy intent, raised by the Lobby's LAUNCH. The mandatory ship-select hangar only opens
    // once the pilot asks to deploy — until then the Lobby overlay owns the not-flying screen
    // (even mid-match), so a joiner can pick a team and read the roster first. Sticky through the
    // whole active match (NOT consumed on spawn): once you've committed to the fight, losing your
    // ship — by docking or dying — returns you to the hangar to re-launch, not the team picker.
    // Cleared only when the match ends (back to the post-match lobby).
    private bool _deployRequested;

    // Previous-frame visibility, so UI sounds fire once on the transition (the sector
    // warning first appearing) rather than every frame.
    private bool _warnWasVisible;

    public override void _Ready()
    {
        // Boot straight into the design-system gallery for headless screenshot CI:
        //   godot --path client -- --ui-showcase
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ui-showcase") >= 0)
        {
            CallDeferred(nameof(LoadShowcaseScene));
            return;
        }

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
        markers.Init(_world, GetNode<Camera3D>("../Camera3D"), _net, _defs);

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

        // Weapons readout, bottom-right (symmetric to the minimap): the local ship's armament —
        // primary gun cadence + launcher ammo/lock. Added here so the top-left text draws over it.
        var weapons = new WeaponsPanel { Name = "WeaponsPanel" };
        AddChild(weapons);
        weapons.Init(_world, _net, _defs);

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

        // Connecting modal on its own high CanvasLayer so it draws above the server
        // browser (ServerInputLayer, layer 100) — the browser stays visible underneath
        // while a join is in flight, and mid-game reconnects overlay the world.
        var connLayer = new CanvasLayer { Name = "ConnectLayer", Layer = 150 };
        AddChild(connLayer);
        var conn = new ConnectLinkModal { Name = "ConnectLinkModal" };
        connLayer.AddChild(conn);
        conn.Init(_cm, _ship);

        CaptureLiveUiIfRequested();
    }

    private void LoadShowcaseScene() => GetTree().ChangeSceneToFile("res://scenes/UiShowcase.tscn");

    // `--ui-shot=<path>` (without --ui-showcase) screenshots the live game UI after a short
    // settle and quits — used to verify the migrated screens render with the design system.
    private void CaptureLiveUiIfRequested()
    {
        string? outPath = null;
        double delay = 2.0; // default settle; --ui-shot-delay=<sec> waits longer (e.g. for an autofly spawn)
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--ui-shot="))
                outPath = a.Substring("--ui-shot=".Length);
            else if (a.StartsWith("--ui-shot-delay="))
                double.TryParse(a.Substring("--ui-shot-delay=".Length), System.Globalization.CultureInfo.InvariantCulture, out delay);
        }
        if (outPath == null)
            return;
        var t = GetTree().CreateTimer(delay);
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
            if (_hangar.OpenedForSpawn)
                return; // the active ship-select can't be F4-dismissed — launch to leave
            _hangar.QueueFree();
            _hangar = null;
        }
        else
        {
            OpenHangar(forSpawn: false);
        }
    }

    private void OpenHangar(bool forSpawn)
    {
        _hangar = new ShipLoadout { OpenedForSpawn = forSpawn };
        _hangar.Init(_defs, _ship, _world, _net);
        AddChild(_hangar);
    }

    // The Lobby's LAUNCH expresses intent to deploy. While a match is Active this promotes the
    // pilot from the lobby overlay into the mandatory ship-select hangar; set pre-match (on ready)
    // it carries that intent through match-start so readying flows straight into the hangar.
    public void RequestDeploy(bool on = true) => _deployRequested = on;

    public override void _Process(double delta)
    {
        var ship = _world.LocalShip;
        bool flying = ship != null;

        // The Lobby overlay owns the not-flying screen — pre-match, post-match, AND mid-match
        // until the pilot presses LAUNCH — so a joiner can see the teams and pick a side before
        // deploying. The spawn hangar opens only once deploy is requested (Hud.RequestDeploy).
        bool inMatch = _world.Phase == MatchPhase.Active;
        // Deploy intent is sticky for the whole match — cleared only when it ends (back to the
        // post-match lobby). It persists across the lobby→active flip (a pre-match ready flows
        // straight into the ship-select at start) AND across losing a ship, so docking or dying
        // reopens the hangar instead of dumping the pilot on the team picker.
        if (_world.Phase == MatchPhase.Ended)
            _deployRequested = false;
        // The hangar IS the ship-select screen. While in an active match with no ship and deploy
        // requested (first spawn, respawn after dock/death): open it if it isn't up, and promote a
        // hangar the player had open manually — either way it becomes the mandatory select
        // (LAUNCH to leave). The death-cam guard holds the hangar back for the blast beat (dock has
        // no death-cam, so it opens immediately). Once the ship exists — or the match leaves Active
        // — the spawn hangar closes itself and the lobby overlay takes over.
        bool hangarUp = _hangar != null && GodotObject.IsInstanceValid(_hangar);
        if (inMatch && !flying && _deployRequested && !_world.DeathCamActive)
        {
            if (hangarUp)
                _hangar!.OpenedForSpawn = true;
            else if (_defs.BuildableShips().Count > 0)
                OpenHangar(forSpawn: true);
        }
        else if (hangarUp && _hangar!.OpenedForSpawn)
        {
            _hangar.QueueFree();
            _hangar = null;
        }

        _sectorShips.Visible = inMatch;
        if (inMatch)
            _sectorShips.Text = $"Ships in sector: {_world.ShipsInLocalSector()}";

        // Running team balance (server-authoritative; accrues on the paycheck cadence). Same team
        // source as the buy menu so the balance shown matches what gates the buttons.
        _credits.Visible = inMatch;
        if (inMatch)
            _credits.Text = $"Credits: {_world.TeamCredits(_world.LocalTeam ?? _net.MyTeam)}";

        // Missile launcher presence gates the empty-rack blip below. The live ammo/lock readout now
        // lives in the bottom-right WeaponsPanel; MissileMount() returns null for hulls with no rack.
        WeaponDef? missileMount = flying && !ship!.IsPod ? _defs.MissileMount((byte)ship.Class) : null;

        // Empty-rack click: a "no rounds" blip on the pressed edge of the secondary-fire keys when
        // the launcher is dry. Mirrors ShipController's Firing2 gate (F, or RMB while the cursor is
        // captured) and stays silent while chat/menus own the keyboard so typing F never blips.
        if (_emptyClickCd > 0)
            _emptyClickCd -= delta;
        bool inputFree = !Chat.Capturing && !SectorOverview.Active && !ShipLoadout.Active && !EscapeMenu.Active && !SettingsDialog.Active;
        bool firing2 =
            inputFree
            && (
                Input.IsPhysicalKeyPressed(Key.F)
                || (Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Right))
            );
        if (firing2 && !_firing2Held && missileMount != null && _net.LocalMissileAmmo == 0 && _emptyClickCd <= 0)
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileEmpty);
            _emptyClickCd = 0.5;
        }
        _firing2Held = firing2;

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
        // Top-left readout: the live flight stats while flying; nothing otherwise (the
        // hangar owns the pre-spawn screen, the lobby overlay everything outside a match).
        _label.Text = flying
            ? ship!.IsPod
                // Ejected: flying the escape pod. Just the resolve hint — hull/speed now read
                // off the HULL gauge and the velocity marker (the pod is unarmed, just fleeing).
                ? "⚠  EJECTED — reach a friendly base or get rescued"
                // HP + Speed are shown graphically (HULL gauge / velocity marker), so the text
                // line keeps only the network telemetry: ping and reconcile stats.
                : $"Ping: {_ship.PingMs, 3:0} ms (±{_ship.JitterMs:0})   Reconciles: {ship.ReconcileCount} (last err {ship.LastReconcileError:0.0}u)"
            : "";
    }
}
