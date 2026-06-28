using Godot;
using StellarAllegiance.Net;

// Heads-up display. The Lobby overlay (a child created here) owns the pre/post-match
// UI; the Hud's own spawn menu only appears once you're teamed in an active match and
// not currently flying, and while flying it shows a speed + reconcile readout.
// The 1/2 keyboard shortcuts in ShipController do the same thing as the spawn buttons.
public partial class Hud : CanvasLayer
{
    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;
    private ShipController _ship = null!;
    private Label _label = null!;
    private Label _sectorShips = null!;
    private Label _credits = null!;
    private Control _menu = null!;
    private Label _warning = null!;

    // Previous-frame visibility, so UI sounds fire once on the transition (the spawn
    // menu opening/closing, the sector warning first appearing) rather than every frame.
    private bool _menuWasVisible;
    private bool _warnWasVisible;

    public override void _Ready()
    {
        _cm = GetNode<ConnectionManager>("../ConnectionManager");
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _ship = GetNode<ShipController>("../ShipController");

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

        // Always-on sector minimap, bottom-left.
        var minimap = new Minimap { Name = "Minimap" };
        AddChild(minimap);
        minimap.Init(_cm, _world);

        // Active-ship count for the local sector, pinned to the very top-left. Hidden until a
        // match is live (the lobby overlay owns the screen otherwise).
        _sectorShips = new Label { Position = new Vector2(16, 12), Visible = false };
        _sectorShips.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_sectorShips);

        _label = new Label { Position = new Vector2(16, 38) };
        _label.AddThemeFontSizeOverride("font_size", 18);
        AddChild(_label);

        // Team credits readout (Stage-2 economy), under the flight/controls line. Hidden until a
        // match is live. Phase 6 adds per-ship cost to the spawn buttons; this is the running balance.
        _credits = new Label { Position = new Vector2(16, 64), Visible = false };
        _credits.AddThemeFontSizeOverride("font_size", 18);
        _credits.AddThemeColorOverride("font_color", new Color(1f, 0.86f, 0.4f));
        AddChild(_credits);

        // Spawn menu: one button per class, shown only when the player has no ship.
        _menu = new VBoxContainer { Position = new Vector2(16, 90) };
        AddChild(_menu);
        _menu.AddChild(SpawnButton("Spawn Scout  [1]  — fast & agile", ShipClass.Scout));
        _menu.AddChild(SpawnButton("Spawn Fighter  [2]  — slower & heavier", ShipClass.Fighter));
        _menu.AddChild(SpawnButton("Spawn Bomber  [3]  — heavy & ponderous", ShipClass.Bomber));

        // Out-of-bounds warning (sector boundary): centered in the upper third, hidden
        // until the local ship strays past its sector radius and starts taking damage.
        _warning = new Label
        {
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorRight = 1f,
            OffsetTop = 90f,
        };
        _warning.AddThemeFontSizeOverride("font_size", 30);
        _warning.AddThemeColorOverride("font_color", new Color(1f, 0.35f, 0.3f));
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
    }

    private Button SpawnButton(string text, ShipClass cls)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(280, 36) };
        b.Pressed += () => SfxManager.Instance?.PlayUi(SfxManager.SfxId.UiClick);
        b.Pressed += () => _ship.RequestSpawn(cls);
        return b;
    }

    public override void _Process(double delta)
    {
        var ship = _world.LocalShip;
        bool flying = ship != null;

        // The Lobby overlay owns everything outside a live match. The spawn menu appears once a
        // match is Active and you're not currently flying — keying off "not flying" means it also
        // reopens when a pod is destroyed/docked and you're awaiting your next ship.
        bool inMatch = _world.Phase == MatchPhase.Active;
        bool teamedInMatch = inMatch;
        _menu.Visible = teamedInMatch && !flying;
        if (_menu.Visible != _menuWasVisible)
        {
            SfxManager.Instance?.PlayUi(_menu.Visible ? SfxManager.SfxId.MenuOpen : SfxManager.SfxId.MenuClose);
            _menuWasVisible = _menu.Visible;
        }
        _sectorShips.Visible = inMatch;
        if (inMatch)
            _sectorShips.Text = $"Ships in sector: {_world.ShipsInLocalSector()}";

        // Running team balance (server-authoritative; accrues on the paycheck cadence).
        _credits.Visible = inMatch;
        if (inMatch)
            _credits.Text = $"Credits: {_world.TeamCredits(_world.LocalTeam ?? 0)}";

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
                // Ejected: flying the escape pod. Show the resolve hint + pod hull instead of
                // the combat flight stats (the pod is unarmed and just trying to get home).
                ? $"⚠  EJECTED — reach a friendly base or get rescued   Pod HP: {ship.Health, 3:0} / {ship.MaxHealth, 3:0}   Speed: {ship.Speed, 4:0.0} u/s"
                : $"HP: {ship.Health, 4:0} / {ship.MaxHealth, 3:0}   Speed: {ship.Speed, 5:0.0} u/s   Ping: {_ship.PingMs, 3:0} ms (±{_ship.JitterMs:0})   Reconciles: {ship.ReconcileCount} (last err {ship.LastReconcileError:0.0}u)"
            : teamedInMatch
                ? "Choose your ship:\nW/S throttle · Shift afterburner · A/D strafe · E/C up·down · mouse aim (Esc frees cursor) · Q/Z roll · click/Space fire · Tab focus target"
                : "";
    }
}
