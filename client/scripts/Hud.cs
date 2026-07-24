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
    private Label _fps = null!;
    private double _statLogAccum; // throttles the [render-stats] log line (see StressRender.ShowStats)
    private bool _measureEnabled; // one-shot: turn on the viewport's GPU/CPU render-time meters
    private Rid _viewportRid;

    // [perf-buckets] reporter state (see PerfBuckets): the last snapshot of the monotonic bucket
    // totals + the frame counter at that snapshot, so a report deltas both against the current
    // values. `_procMsAccum` sums proc_ms EVERY frame so `other`/`sum` compare against a WINDOW-
    // averaged proc — the TimeProcess monitor is an instantaneous last-frame value, while the
    // buckets are window averages, so mixing them would skew `other`. Only touched while Enabled.
    private long[]? _bucketPrev;
    private long[]? _bucketNow;
    private ulong _lastBucketFrames;
    private double _procMsAccum;

    // [predict-stats] windowing (see ReportPredictStats): the own-ship reconcile count + the global
    // live-contact tick count at the previous 2s report, so each report prints in-window deltas.
    // Only touched while InterpStats.Enabled.
    private int _lastReconcileCount;
    private int _lastLocalContacts;

    // Edge-detect the secondary-fire keys so an empty-rack click plays its "no rounds" blip once
    // per press (not every held frame), and a short cooldown so mashing F doesn't machine-gun it.
    private bool _firing2Held;
    private bool _chaffHeld;
    private bool _mineHeld;
    private bool _probeHeld;
    private double _emptyClickCd;

    // The design-system gallery overlay (F9), instantiated on demand.
    private Control? _showcase;

    // The hangar / ship-loadout overlay (F4 or the HANGAR button), instantiated on demand.
    private ShipLoadout? _hangar;

    // The floating chat overlay (created in _Ready). Kept as a field so OpenHangar can raise it
    // above the full-screen hangar, which is added as a later Hud child and would otherwise cover it.
    private Chat? _chat;

    // Deploy intent, raised by the Lobby's LAUNCH. The mandatory ship-select hangar only opens
    // once the pilot asks to deploy — until then the Lobby overlay owns the not-flying screen
    // (even mid-match), so a joiner can pick a team and read the roster first. Sticky through the
    // whole active match (NOT consumed on spawn): once you've committed to the fight, losing your
    // ship — by docking or dying — returns you to the hangar to re-launch, not the team picker.
    // Cleared only when the match ends (back to the post-match lobby). Static so the Lobby overlay
    // can read it (Lobby.cs) and yield the not-flying screen to the hangar once you've deployed —
    // matching the other static UI-state flags (ShipLoadout.Active, Chat.Capturing, ...). Hud is a
    // persistent Main.tscn node, so this has the same session lifetime the instance field had.
    public static bool DeployRequested { get; private set; }

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

        // Full-screen "jump" flash that masks the rock-field swap on an aleph warp (see WarpFlash).
        // Its own high CanvasLayer, driven by the local ship's sector change via WorldRenderer.Warped.
        var warpFlash = new WarpFlash { Name = "WarpFlash" };
        AddChild(warpFlash);
        _world.Warped += warpFlash.Play; // raise + hold on warp
        _world.WarpSettled += warpFlash.Release; // clear once the destination sector has loaded

        // Sun lens flare (added first so it sits UNDER every HUD element while still drawing over
        // the 3D viewport — it's a light effect on the sky, not a readout).
        var flare = new LensFlare { Name = "LensFlare" };
        AddChild(flare);
        flare.Init(GetNode<Camera3D>("../Camera3D"), _world);

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
        systemRing.Init(_world, GetNode<Camera3D>("../Camera3D"), _defs);

        // Always-on sector minimap, bottom-left.
        var minimap = new Minimap { Name = "Minimap" };
        AddChild(minimap);
        minimap.Init(_cm, _world);

        // Weapons readout, bottom-right (symmetric to the minimap): the local ship's armament —
        // primary gun cadence + launcher ammo/lock. Added here so the top-left text draws over it.
        var weapons = new WeaponsPanel { Name = "WeaponsPanel" };
        AddChild(weapons);
        weapons.Init(_world, _net, _defs);

        // Telescopic zoom scope (+/−): a circular PiP magnifier that replaces the centre gauges
        // while open. Added after the combat overlays so it draws above them, under the text/menu.
        var zoom = new ZoomView { Name = "ZoomView" };
        AddChild(zoom);
        zoom.Init(_world);

        // Transient first/third-person view-mode readout, flashed on a V toggle or wheel transition.
        var viewMode = new ViewModeIndicator { Name = "ViewModeIndicator" };
        AddChild(viewMode);
        viewMode.Init();

        // Live framerate, pinned to the very top-left corner. Always on (unlike the match-gated
        // readouts below), so it's useful on the menus/lobby too. Mono Data style, muted color.
        _fps = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.TextDim);
        _fps.Position = new Vector2(16, 12);
        AddChild(_fps);

        // Active-ship count for the local sector. Hidden until a match is live (the lobby overlay
        // owns the screen otherwise). Telemetry → mono Data style. Sits under the FPS readout.
        _sectorShips = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _sectorShips.Position = new Vector2(16, 38);
        _sectorShips.Visible = false;
        AddChild(_sectorShips);

        _label = UiKit.MakeLabel("", UiKit.TextStyle.Data);
        _label.Position = new Vector2(16, 64);
        AddChild(_label);

        // Team credits readout (Stage-2 economy), under the flight/controls line. Hidden until a
        // match is live. The Secondary token replaces the old inline gold.
        _credits = UiKit.MakeLabel("", UiKit.TextStyle.Data, DesignTokens.Secondary);
        _credits.Position = new Vector2(16, 90);
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
        _chat = new Chat { Name = "Chat" };
        AddChild(_chat);
        _chat.Init(_cm, _world);

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
                outPath = a["--ui-shot=".Length..];
            else if (a.StartsWith("--ui-shot-delay="))
                double.TryParse(
                    a["--ui-shot-delay=".Length..],
                    System.Globalization.CultureInfo.InvariantCulture,
                    out delay
                );
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
            if (_showcase != null && IsInstanceValid(_showcase))
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
        // The hangar is a later Hud sibling than Chat, so it draws on top by default. Raise the
        // chat overlay back to the front so Enter-to-talk stays visible/usable over the hangar.
        _chat?.MoveToFront();
    }

    // The Lobby's LAUNCH expresses intent to deploy. While a match is Active this promotes the
    // pilot from the lobby overlay into the mandatory ship-select hangar; set pre-match (on ready)
    // it carries that intent through match-start so readying flows straight into the hangar.
    public void RequestDeploy(bool on = true) => DeployRequested = on;

    public override void _Process(double delta)
    {
        // Live framerate, always on (top-left corner). Engine.GetFramesPerSecond is a smoothed
        // per-second reading, so this stays legible without extra averaging. Under --render-stats the
        // draw-call / primitive counters ride alongside it — the FPS says frames dropped, these say
        // WHY (draw calls vs geometry), which is what the render stress A/B is measuring.
        ulong draws = 0,
            prims = 0;
        double gpuMs = 0,
            rcpuMs = 0,
            procMs = 0;
        bool showStats = StressRender.ShowStats;
        if (showStats)
        {
            // Enable the viewport's per-frame GPU + render-thread-CPU meters once. These are Godot's
            // own NATIVE (C++) timings — identical in Debug and Release — so they cut cleanly through
            // the "is it the managed build?" confound: gpu_ms high ⇒ GPU-bound; rcpu_ms high ⇒ native
            // draw-submission/cull bound (where fewer draw calls / a MultiMesh would help); proc_ms
            // high ⇒ main-thread _Process (C# + native node work), the only config-sensitive one.
            if (!_measureEnabled)
            {
                _viewportRid = GetViewport().GetViewportRid();
                RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);
                _measureEnabled = true;
            }
            draws = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
            prims = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
            gpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid);
            rcpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewportRid);
            procMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
            _fps.Text =
                $"FPS {Engine.GetFramesPerSecond()}  DRAW {draws}  GPU {gpuMs:F1}  RCPU {rcpuMs:F1}  PROC {procMs:F1}ms  fx={StressRender.Fx}";
            // Accumulate proc_ms EVERY frame so the [perf-buckets] report can average it over the same
            // window as the buckets (the monitor above is a per-frame instantaneous value).
            if (PerfBuckets.Enabled)
                _procMsAccum += procMs;
        }
        else
        {
            _fps.Text = $"FPS: {Engine.GetFramesPerSecond()}";
        }

        // Per-frame render-hitch tally for the [interp-stats] report (global, not per-tier).
        if (InterpStats.Enabled)
            InterpStats.NoteFrame(delta);

        // The 2s report cadence ticks whenever EITHER measurement family is live; each report line
        // then respects its OWN gate — [render-stats]/[perf-buckets] only under ShowStats/PerfBuckets,
        // [interp-stats]/[predict-stats] only under InterpStats.Enabled.
        if (showStats || InterpStats.Enabled)
        {
            _statLogAccum += delta;
            if (_statLogAccum >= 2.0)
            {
                _statLogAccum = 0.0;
                if (showStats)
                {
                    Log.Print(
                        $"[render-stats] fps={Engine.GetFramesPerSecond()} draws={draws} prims={prims} gpu_ms={gpuMs:F1} rcpu_ms={rcpuMs:F1} proc_ms={procMs:F1} fx={StressRender.Fx}"
                    );
                    ReportPerfBuckets();
                }
                if (InterpStats.Enabled)
                {
                    InterpStats.Report();
                    ReportPredictStats();
                }
            }
        }

        // Time only the non-report remainder into the Hud bucket — the render-stats/report block above
        // (its own RenderingServer queries + logging) isn't part of the per-frame HUD cost we attribute.
        var hudT0 = PerfBuckets.Now();

        var ship = _world.Ships.LocalShip;
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
            DeployRequested = false;
        // …but intent is ALSO per-connection. Any state other than a live Connected session — a
        // voluntary LEAVE MATCH (→ address screen), a drop mid auto-reconnect, or a give-up-and-
        // rejoin — re-homes the pilot to the team lobby, where the server reset their lobby
        // membership to NoTeam (a rejoin is a fresh join, not a ship reclaim). Without clearing it,
        // the stale-true flag drops the rejoiner straight into the MANDATORY spawn hangar with no
        // team, where the server silently refuses MsgSpawn ("Pick a team before launching", a chat
        // line the hangar never observes) and the button hangs on "LAUNCHING…" forever. Clearing it
        // here re-homes them to the team picker to re-earn deploy intent. Harmless mid-match (stays
        // Connected while flying/dying) and on a reclaim reconnect (the reclaimed ship makes this
        // moot — you're flying, hangar and lobby both hidden).
        if (_cm.State != ConnectionManager.ConnState.Connected)
            DeployRequested = false;
        // The hangar IS the ship-select screen. While in an active match with no ship and deploy
        // requested (first spawn, respawn after dock/death): open it if it isn't up, and promote a
        // hangar the player had open manually — either way it becomes the mandatory select
        // (LAUNCH to leave). The death-cam guard holds the hangar back for the blast beat (dock has
        // no death-cam, so it opens immediately). Once the ship exists — or the match leaves Active
        // — the spawn hangar closes itself and the lobby overlay takes over.
        bool hangarUp = _hangar != null && IsInstanceValid(_hangar);
        if (inMatch && !flying && DeployRequested && !_world.Ships.DeathCamActive)
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
            _sectorShips.Text = $"Ships in sector: {_world.Ships.ShipsInLocalSector()}";

        // Running team balance (server-authoritative; accrues on the paycheck cadence). Same team
        // source as the buy menu so the balance shown matches what gates the buttons.
        _credits.Visible = inMatch;
        if (inMatch)
            _credits.Text = $"Credits: {_world.TeamState.Credits(_world.LocalTeam ?? _net.MyTeam)}";

        // Missile launcher presence gates the empty-rack blip below. The live ammo/lock readout now
        // lives in the bottom-right WeaponsPanel; MissileMount() returns null for hulls with no rack
        // — loadout-aware, so a rack emptied in the hangar doesn't blip either.
        WeaponDef? missileMount = flying && !ship!.IsPod ? _defs.MissileMount((byte)ship.Class, ship.LoadoutIds) : null;

        // Empty-rack click: a "no rounds" blip on the pressed edge of the secondary-fire keys when
        // the launcher is dry. Mirrors ShipController's Firing2 gate (F, or RMB while the cursor is
        // captured) and stays silent while chat/menus own the keyboard so typing F never blips.
        if (_emptyClickCd > 0)
            _emptyClickCd -= delta;
        bool inputFree = InputGate.FlightInputFree;
        bool firing2 =
            inputFree
            && (
                Input.IsActionPressed("fire_secondary")
                || (Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsMouseButtonPressed(MouseButton.Right))
            );
        if (firing2 && !_firing2Held && missileMount != null && _net.LocalMissileAmmo == 0 && _emptyClickCd <= 0)
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileEmpty);
            _emptyClickCd = 0.5;
        }
        _firing2Held = firing2;

        // Same "no rounds" blip for the dispenser keys (C chaff / B mine / G probe): an empty (or
        // absent) dispenser otherwise swallows the press silently — the drop itself is
        // server-authoritative, so this client-side edge detect is cosmetic feedback only.
        bool dispensersLive = flying && !ship!.IsPod;
        bool chaffKey = inputFree && Input.IsActionPressed("drop_chaff");
        if (chaffKey && !_chaffHeld && dispensersLive && _net.LocalChaffAmmo == 0 && _emptyClickCd <= 0)
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileEmpty);
            _emptyClickCd = 0.5;
        }
        _chaffHeld = chaffKey;
        bool mineKey = inputFree && Input.IsActionPressed("drop_mine");
        if (mineKey && !_mineHeld && dispensersLive && _net.LocalMineAmmo == 0 && _emptyClickCd <= 0)
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileEmpty);
            _emptyClickCd = 0.5;
        }
        _mineHeld = mineKey;
        bool probeKey = inputFree && Input.IsActionPressed("drop_probe");
        if (probeKey && !_probeHeld && dispensersLive && _net.LocalProbeAmmo == 0 && _emptyClickCd <= 0)
        {
            SfxManager.Instance?.PlayUi(SfxManager.SfxId.MissileEmpty);
            _emptyClickCd = 0.5;
        }
        _probeHeld = probeKey;

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
        // Hidden in the F3 sector map — telemetry is ship-centric combat chrome, not a map aid.
        _label.Visible = !SectorOverview.Active;
        _label.Text = flying
            ? ship!.IsPod
                // Ejected: flying the escape pod. Just the resolve hint — hull/speed now read
                // off the HULL gauge and the velocity marker (the pod is unarmed, just fleeing).
                ? "⚠  EJECTED — reach a friendly base or get rescued"
                // HP + Speed are shown graphically (HULL gauge / velocity marker), so the text
                // line keeps only the network telemetry: ping and reconcile stats.
                : $"Ping: {_ship.PingMs, 3:0} ms (±{_ship.JitterMs:0})   Reconciles: {ship.ReconcileCount} (last err {ship.LastReconcileError:0.0}u)"
            : "";
        PerfBuckets.Add(PerfBuckets.Hud, hudT0);
    }

    // Print one [perf-buckets] line of per-frame bucket averages over the window since the last report
    // (see PerfBuckets). Buckets are monotonic tick totals, so we delta the current snapshot against the
    // previous one and divide by the frames elapsed (Engine.GetProcessFrames). world_other = world − col
    // − bolt (the collision + bolt-impact passes nested inside WorldRenderer._Process); other =
    // window-avg proc − Σ(printed C# buckets), the native/uninstrumented remainder; sum = Σ(printed C#
    // buckets) so a reader can eyeball sum vs proc. Called from the 2s [render-stats] block, Enabled only.
    private void ReportPerfBuckets()
    {
        if (!PerfBuckets.Enabled)
            return;
        int n = PerfBuckets.BucketCount;
        _bucketPrev ??= new long[n];
        _bucketNow ??= new long[n];
        PerfBuckets.Snapshot(_bucketNow);
        ulong nowFrames = Engine.GetProcessFrames();
        ulong dFrames = nowFrames - _lastBucketFrames;
        if (dFrames > 0)
        {
            double freq = PerfBuckets.Frequency;
            double MsPer(int b) => (_bucketNow[b] - _bucketPrev[b]) * 1000.0 / freq / dFrames;
            double mkProc = MsPer(PerfBuckets.MkProc);
            double mkDraw = MsPer(PerfBuckets.MkDraw);
            double rship = MsPer(PerfBuckets.RShip);
            double glow = MsPer(PerfBuckets.Glow);
            double trail = MsPer(PerfBuckets.Trail);
            double col = MsPer(PerfBuckets.Col);
            double colStat = MsPer(PerfBuckets.ColStatic);
            double colPair = MsPer(PerfBuckets.ColPair);
            double bolt = MsPer(PerfBuckets.Bolt);
            double beacon = MsPer(PerfBuckets.Beacon);
            double worldOther = MsPer(PerfBuckets.World) - col - bolt;
            double hud = MsPer(PerfBuckets.Hud);
            double sum = mkProc + mkDraw + rship + glow + trail + col + bolt + beacon + worldOther + hud;
            double procAvg = _procMsAccum / dFrames; // window-averaged, matching the buckets' window
            double other = procAvg - sum;
            Log.Print(
                $"[perf-buckets] frames={dFrames} ships={_world.Ships.Count} "
                    + $"mk_proc={mkProc:F1} mk_draw={mkDraw:F1} rship={rship:F1} glow={glow:F1} trail={trail:F1} "
                    + $"col={col:F1} col_stat={colStat:F1} col_pair={colPair:F1} bolt={bolt:F1} beacon={beacon:F1} world_other={worldOther:F1} hud={hud:F1} "
                    + $"other={other:F1} sum={sum:F1}"
            );
        }
        (_bucketPrev, _bucketNow) = (_bucketNow, _bucketPrev); // this snapshot becomes next window's baseline
        _lastBucketFrames = nowFrames;
        _procMsAccum = 0.0;
    }

    // Print the own-ship ram/prediction line on the same 2s cadence as [interp-stats] (see
    // InterpStats). reconciles = in-window server corrections (delta of the PredictionController's
    // monotonic ReconcileCount); rec_err_max = the largest position error any of those corrections
    // spanned this window (peak captured at the reconcile site, then zeroed); local_hits = in-window
    // count of LIVE prediction ticks that resolved a genuine ship-vs-ship contact (the ram-recipe
    // frequency signal). All counters reset across a respawn, so negative deltas clamp to 0.
    private void ReportPredictStats()
    {
        if (!InterpStats.Enabled)
            return;

        var ship = _world.Ships.LocalShip;
        int recNow = ship?.ReconcileCount ?? 0;
        int reconciles = recNow - _lastReconcileCount;
        if (reconciles < 0)
            reconciles = 0; // a respawn built a fresh PredictionController — counter restarted at 0
        _lastReconcileCount = recNow;

        int hitsNow = PredictionController.LocalContactTicks;
        int localHits = hitsNow - _lastLocalContacts;
        if (localHits < 0)
            localHits = 0;
        _lastLocalContacts = hitsNow;

        float recErrMax = PredictionController.WindowMaxReconcileErr;
        PredictionController.WindowMaxReconcileErr = 0f;

        // sep_at_hit (rendered separation at each in-window local_hit): p50/p95 over the window, feeding
        // the Phase-5 forward-rendering design note. sep_n = how many hits contributed.
        var (sepN, sepP50, sepP95) = InterpStats.DrainSepAtHit();

        Log.Print(
            $"[predict-stats] reconciles={reconciles} rec_err_max={recErrMax:F1} local_hits={localHits} "
                + $"sep_n={sepN} sep_at_hit_p50={sepP50:F1} sep_at_hit_p95={sepP95:F1}"
        );
    }
}
