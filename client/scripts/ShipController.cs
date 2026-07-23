using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;
using StellarAllegiance.Ui;

// Reads local input, runs the fixed-rate (20 Hz) input/prediction loop, and
// calls the ApplyInput reducer. Also handles the (temporary) spawn key for T4.
// Input is sampled every render frame but only applied + sent + predicted on a
// simple fixed 20 Hz accumulator, so the prediction cadence is regular.
public partial class ShipController : Node
{
    private const int MaxStepsPerFrame = 5; // spiral-of-death guard
    private const int DefaultTargetLead = 3; // ticks the prediction runs ahead of authority
    private const float SlewGain = 0.08f; // how hard the local clock tracks the server
    private const float MaxSlew = 0.30f; // cap the clock rate adjustment (±30%)

    // How far ahead of authority the prediction clock runs. This is the input-timing
    // budget: each ApplyInput is stamped with `_predTick`, so it must reach the
    // server BEFORE that tick is simulated, or the server falls back to a stale
    // input and diverges — and every miss costs a reconcile that fights your
    // steering. The lead must cover round-trip + network jitter. localhost (RTT≈0)
    // is fine at 1; over the internet, jitter around the ~50 ms tick boundary makes
    // 1 too tight, so the default is 3 (~150 ms margin). A larger lead does NOT add
    // felt latency — the local ship is predicted/rendered instantly; more lead just
    // means fewer corrections. Override per-connection with STDB_LEAD (clamped 1..15).
    private int _targetLead = DefaultTargetLead;

    // When STDB_LEAD is NOT set, the lead is derived from the live latency readout
    // (UpdateAdaptiveLead): an input stamped for tick P must reach the server before
    // it simulates P, so the budget the lead has to cover is one full round trip plus
    // a few standard deviations of jitter. At 120 ms RTT the fixed default of 3
    // (~150 ms) left only ~30 ms of slack, so ordinary jitter pushed inputs past their
    // tick and forced a reconcile ~every second. Sizing the lead to measured RTT+jitter
    // makes on-time inputs the norm and drives the reconcile rate down. (No felt cost:
    // the local ship is still predicted instantly.)
    private bool _leadFromEnv;

    private ConnectionManager _cm = null!;
    private WorldRenderer _world = null!;
    private DefRegistry? _defs; // sibling; hardpoint layouts for the MsgSpawn weapon-override tail

    // Phase-1b: when GameNetClient is active (SIM_URI set), spawn + input go over the
    // native sim socket instead of STDB reducers; everything else (prediction, defs,
    // rendering) is unchanged. Null/inactive = pure STDB path.
    private GameNetClient? _net;
    private bool Native => _net is { Active: true };

    private double _acc;
    private uint _predTick; // prediction tick, in SERVER-tick space
    private bool _hadShip;
    private int _stepsSinceSpawn;
    private ShipInputState _input;

    // On-change input sending: ApplyInput goes out only when the stick state differs from
    // the last SENT input, or the keepalive window lapses. The server replays the last
    // received input for the silent ticks (held input) — exactly what our own prediction
    // does with an unchanged stick — so auth == prediction still holds while idle/cruise
    // ticks cost no reducer transaction at all (~10x fewer under keyboard flight; mouse
    // easing changes the stick every tick, so active maneuvering still sends at full rate).
    private const uint InputKeepaliveTicks = 20; // ~1 s at 20 Hz; also paces PingMs samples
    private ShipInputState _lastSentInput;
    private uint _lastSentTick;
    private ShipClass? _spawnRequest; // class chosen via HUD menu / 1-2 keys; cleared once flying
    private bool _spawnPending;
    private double _spawnRetry;

    // Why the last buy was suppressed by the client pre-check (locked / can't afford), or null when
    // the buy went through / none is pending. Read by Hud to surface a one-line hint near the menu.
    public string? SpawnHint { get; private set; }
    private bool _perturbHeld; // edge-detect the P debug key
    private bool _apHeld; // edge-detect the autopilot toggle (T)

    // Optimistic local autopilot-engaged flag: set the moment we send an engage frame, cleared on a
    // disengage send (T toggle, manual-input handback, death). The server independently drives the
    // true state via the ShipFlagAutopilot snapshot bit (WP4 will reconcile the follow-authority
    // prediction mode from it); this local flag only gates the T toggle + handback here and stays
    // deliberately simple/self-contained. Static so SectorOverview's F3 right-click engage can read
    // and set it through the same seam.
    public static bool ApEngagedLocal { get; private set; }

    // Sync the engaged flag from the SERVER's authoritative ShipFlagAutopilot bit (driven by
    // WorldRenderer on the snapshot edge). Server-side disengagement — arrival, target loss, override
    // detection — clears it here so the HUD banner/toggle track truth even when the client didn't ask.
    public static void SyncApEngaged(bool on) => ApEngagedLocal = on;

    // Flight-HUD contact-marker cap override (--marker-cap=N). -1 = unset (TargetMarkers uses its
    // authored MaxEnemyMarkers / MaxFriendlyMarkers defaults), 0 = uncapped (the A/B knob), N>0 = N
    // enemy markers with N*3/4 friendly. Read by TargetMarkers.DrawShipsPass; parsed in _Ready below.
    public static int MarkerCap = -1;

    private bool _apActivePrev; // edge-detect follow-authority exit to re-anchor the prediction clock

    // Mouse-look aiming (Allegiance style). The M0 flight model integrates yaw/pitch as
    // commanded turn RATES that slew in under a torque limit, so it needs a HELD stick
    // deflection — a raw per-frame pixel delta is a one-tick transient the rate-limited
    // slew can't act on (small moves vanish, large moves saturate -> jerky, all-or-nothing
    // aim). So the mouse drives a self-CENTERING virtual stick: captured-cursor motion is
    // accumulated (in _Input) into a persistent deflection (_stickYaw/_stickPitch) that
    // eases back toward center each frame when the mouse stops. Push to turn, release to
    // straighten. This is purely an input-sampling change; the flight dynamics are untouched.
    // The cursor is captured while flying (first Esc releases, click recaptures; a second Esc
    // with the cursor already free opens the escape menu); arrow keys still work as a fallback
    // and sum with the stick. Feel comes from the settings (UserPrefs sensitivity multiplier +
    // invert-Y, live via RefreshMousePrefs); the STDB_MOUSE_SENS / STDB_MOUSE_INVERT env vars
    // pin it for a run (testing override that wins over the saved prefs).
    private const float DefaultMouseSens = 0.01f; // px -> stick deflection per frame
    private const float MouseReturnPerSec = 8f; // how fast the virtual stick eases back to center
    private float _mouseSens = DefaultMouseSens;
    private bool _mouseInvert;
    private bool _sensFromEnv,
        _invertFromEnv; // STDB_MOUSE_* env override present — pin the value, ignore prefs
    private Vector2 _mouseDelta; // captured-cursor motion accumulated since last sample
    private float _stickYaw,
        _stickPitch; // persistent self-centering virtual-stick deflection (-1..1)
    private bool _hasShip; // mirrors _world.Ships.LocalShip != null, set each _Process for _Input's capture gate

    // Headless verification: `--autofly` auto-spawns a Scout and flies a fixed
    // input so the full ApplyInput -> SimTick -> reconcile loop can be checked
    // without a human at the keyboard.
    // Exact field compare (bools + floats sampled from the same key/stick state repeat
    // bit-identically while unchanged, so == is the right test — no epsilon wanted).
    private static bool InputsEqual(in ShipInputState a, in ShipInputState b) =>
        a.Thrust == b.Thrust
        && a.StrafeX == b.StrafeX
        && a.StrafeY == b.StrafeY
        && a.Yaw == b.Yaw
        && a.Pitch == b.Pitch
        && a.Roll == b.Roll
        && a.Firing == b.Firing
        && a.Boost == b.Boost
        && a.Firing2 == b.Firing2
        && a.DropChaff == b.DropChaff
        && a.DropMine == b.DropMine
        && a.DropProbe == b.DropProbe
        && a.LockTargetId == b.LockTargetId;

    private bool _autoFly;
    private bool _autoJoined; // autofly QuickJoins (team + ready) once on connect
    private bool _hangarDemo; // --hangar-demo: QuickJoin only; the hangar harness drives spawning
    private double _hangarDemoElapsed; // failsafe clock — quit if the demo never completes
    private bool _selfTestDone; // autofly fires one divergence injection
    private bool _combatTest; // --combat-test: fly straight + fire (head-on damage check)
    private bool _warpTest; // --warp-test: mine-drop run, then manual-steer into the sector's aleph (warp smoke)
    private bool _ramTest; // --ram-test: autofly chases + rams the nearest remote ship (ram-prediction measurement harness)
    private ulong _ramTargetId; // committed ram target (survives frame-to-frame so the rammer doesn't orbit a cluster)

    // Round-trip latency. STDB mode times each ApplyInput against its own reducer callback
    // (clientTick echoed back); native mode times an explicit Ping/Pong nonce (no reducer to
    // echo). Either way it's the true client→server→client round trip, independent of the
    // prediction clock. The HUD reads PingMs/JitterMs; both feed UpdateAdaptiveLead.
    private readonly System.Collections.Generic.Dictionary<uint, double> _sentAt = new();
    public float PingMs { get; private set; }
    public float JitterMs { get; private set; }

    // Native-mode ping probe: a small nonce sent on a fixed wall-clock cadence (the on-change
    // input stream is too bursty to estimate jitter from). _sentAt holds only ping nonces in
    // native mode and only reducer ticks in STDB mode — the two modes never coexist.
    private uint _pingNonce;
    private double _pingAcc;
    private const double PingIntervalSec = 0.25; // 4 Hz

    public override void _Ready()
    {
        _cm = GetNode<ConnectionManager>("../ConnectionManager");
        _world = GetNode<WorldRenderer>("../WorldRenderer");
        _defs = GetNodeOrNull<DefRegistry>("../DefRegistry");

        if (int.TryParse(OS.GetEnvironment("STDB_LEAD"), out var lead))
        {
            _targetLead = Mathf.Clamp(lead, 1, 15);
            _leadFromEnv = true; // pin it; skip the adaptive sizing below
        }

        if (float.TryParse(OS.GetEnvironment("STDB_MOUSE_SENS"), out var sens) && sens > 0f)
        {
            _mouseSens = sens;
            _sensFromEnv = true; // testing override — wins over the saved pref
        }
        string invertEnv = OS.GetEnvironment("STDB_MOUSE_INVERT");
        if (!string.IsNullOrEmpty(invertEnv))
        {
            _mouseInvert = invertEnv is "1" or "true";
            _invertFromEnv = true;
        }
        RefreshMousePrefs();
        UserPrefs.Changed += RefreshMousePrefs; // settings dialog writes through UserPrefs

        // Latency for the adaptive lead / HUD readout is sampled in native mode via the
        // Ping/Pong probe (the in-STDB ApplyInput reducer-ack path was removed with the sim).
        _net = GetNodeOrNull<GameNetClient>("../GameNetClient");
        if (_net is not null)
            _net.Pong += OnPong;

        var autoClass = ShipClass.Scout;
        foreach (var a in OS.GetCmdlineArgs())
        {
            if (a == "--autofly")
                _autoFly = true;
            if (a == "--fighter")
                autoClass = ShipClass.Fighter; // autofly picks Fighter (dev verify)
            if (a == "--bomber")
                autoClass = ShipClass.Bomber; // autofly picks Bomber (dev verify)
            if (a == "--combat-test")
            {
                _autoFly = true;
                _combatTest = true;
            }
            if (a == "--warp-test")
            {
                _autoFly = true;
                _warpTest = true;
            }
            // Ram-prediction measurement harness: fly autofly, but chase + ram the nearest visible remote
            // ship so the local predictor repeatedly resolves genuine ship-vs-ship contacts (two same-team
            // autofly clients on the default weave never touch). Implies the autofly join/spawn flow.
            if (a == "--ram-test")
            {
                _autoFly = true;
                _ramTest = true;
            }
            // Render stress-test knobs (see StressRender / the --stress-fighters server harness).
            // --render-stats alone just shows the counters; --stress-fx=<mode> also strips ship fx
            // in stages and, for the dressed modes, lights the parked fleet's plume so the A/B
            // reflects a MOVING fleet rather than the throttle-gated-dark inert default.
            if (a == "--render-stats")
            {
                StressRender.ShowStats = true;
                PerfBuckets.Enabled = true; // gate the [perf-buckets] frame-time attribution alongside the counters
            }
            // Remote-ship motion-fidelity instrumentation (InterpStats). Independent of the render-stats
            // knobs above, so it runs on a plain --autofly session with no stress-fx; gates the
            // [interp-stats]/[predict-stats] lines the Hud prints on its 2s cadence.
            if (a == "--interp-stats")
                InterpStats.Enabled = true;
            if (a.StartsWith("--stress-fx="))
            {
                StressRender.Fx = a["--stress-fx=".Length..] switch
                {
                    "none" or "nofx" => StressRender.FxMode.NoFx,
                    "nolights" => StressRender.FxMode.NoLights,
                    _ => StressRender.FxMode.Full,
                };
                StressRender.ForceGlow = StressRender.Fx != StressRender.FxMode.NoFx;
                StressRender.ShowStats = true;
                PerfBuckets.Enabled = true;
            }
            // Flight-HUD marker-cap override (A/B knob for the distance-based contact cap). N = enemy
            // cap (friendly = N*3/4); 0 = uncapped. Left at -1 (unset) TargetMarkers uses its defaults.
            if (a.StartsWith("--marker-cap="))
            {
                if (int.TryParse(a["--marker-cap=".Length..], out int mc) && mc >= 0)
                    MarkerCap = mc;
            }
        }
        // Self-report the managed build config so perf runs can't silently execute unoptimized C#
        // (Godot run from `--path` normally loads the Debug assembly regardless of `dotnet -c Release`).
#if DEBUG
        Log.Print("[build-config] managed=DEBUG (unoptimized C#)");
#else
        Log.Print("[build-config] managed=RELEASE (optimized C#)");
#endif
        // --hangar-demo drives the docked screen itself; it only needs the QuickJoin
        // (team + ready) so the match starts and the mandatory spawn hangar opens. It is a
        // UI-harness flag (after `--`, GetCmdlineUserArgs) like --ui-shot — see ShipLoadout.
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--hangar-demo="))
                _hangarDemo = true;
        // Headless runs are otherwise uncapped: _Process spins as fast as possible,
        // flooding ApplyInput and racing the prediction far ahead of the 20 Hz
        // server, which inflates the prediction lead. Cap to a realistic display
        // rate so the autofly's reconcile behaviour matches a real client.
        if (_autoFly)
        {
            Engine.MaxFps = 60;
            _spawnRequest = autoClass; // autofly flies Scout, or Fighter with --fighter
        }
        if (_hangarDemo)
            Engine.MaxFps = 60;
    }

    public override void _ExitTree()
    {
        UserPrefs.Changed -= RefreshMousePrefs; // static event — would leak this node otherwise
    }

    // Mouse feel follows the saved settings unless an STDB_MOUSE_* env var pinned it for this
    // run. Re-run on every UserPrefs.Changed so slider/toggle changes apply mid-flight.
    private void RefreshMousePrefs()
    {
        if (!_sensFromEnv)
            _mouseSens = DefaultMouseSens * UserPrefs.MouseSensMultiplier;
        if (!_invertFromEnv)
            _mouseInvert = UserPrefs.MouseInvertY;
    }

    // Called by the HUD spawn menu. Picks the class to spawn; the actual reducer
    // call happens in _Process once the connection is live (with retry).
    public void RequestSpawn(ShipClass cls)
    {
        if (_world.Ships.LocalShip == null)
            _spawnRequest = cls;
    }

    // Resolve the current autopilot destination, in priority order: the Tab focus (a ship / base /
    // asteroid, decoded from its FocusedId encoding), else a dropped F3 waypoint. Returns false when
    // there's nothing to engage toward. kind: 0 ship, 1 base, 2 rock, 3 waypoint; id is UNENCODED
    // (flags stripped); sector/pos carry the waypoint (zeros for entity kinds).
    private static bool ResolveEngageTarget(out byte kind, out ulong id, out uint sector, out Vector3 pos)
    {
        kind = 0;
        id = 0;
        sector = 0;
        pos = Vector3.Zero;

        ulong focus = TargetMarkers.FocusedId;
        if (focus != 0)
        {
            if (GameContent.IsBaseLock(focus))
            {
                kind = 1;
                id = GameContent.BaseIdOf(focus);
            }
            else if (GameContent.IsAsteroidFocus(focus))
            {
                kind = 2;
                id = GameContent.AsteroidIdOf(focus);
            }
            else
            {
                kind = 0;
                id = focus;
            }
            return true;
        }

        var wp = TargetMarkers.Waypoint;
        if (wp.Has)
        {
            kind = 3;
            sector = wp.Sector;
            pos = wp.Pos;
            return true;
        }
        return false;
    }

    // Engage autopilot toward the current focus/waypoint (send mode=1). No-op (returns false) unless
    // the player is launched and a destination resolves. Shared by the T toggle and SectorOverview's
    // F3 right-click engage. Sets the optimistic local flag.
    public bool EngageAutopilot()
    {
        if (_world.Ships.LocalShip == null)
            return false; // not launched — nothing to steer
        if (!ResolveEngageTarget(out byte kind, out ulong id, out uint sector, out Vector3 pos))
            return false;
        _net?.SetAutopilot(1, kind, id, sector, pos);
        ApEngagedLocal = true;
        return true;
    }

    // Disengage autopilot (send mode=0) and clear the optimistic local flag. Called by the T toggle
    // and the manual-input handback below.
    public void DisengageAutopilot()
    {
        _net?.SetAutopilot(0, 0, 0, 0, Vector3.Zero);
        ApEngagedLocal = false;
        // Hand control back to local prediction IMMEDIATELY (don't wait ~1 RTT for the server flag to
        // clear) so a manual takeover feels instant; the server flag's later falling edge is then a
        // no-op. RebaseTo inside SetAutopilot keeps the handoff visually continuous.
        _world.Ships.LocalShip?.SetAutopilot(false);
    }

    // Autopilot toggle (T): edge-triggered like Tab. Swallowed while chatting (T would type). Toggles
    // between engage (toward the current focus/waypoint) and disengage.
    private void HandleAutopilotToggle()
    {
        if (Chat.Capturing)
        {
            _apHeld = true;
            return;
        }
        bool t = Input.IsActionPressed("engage_autopilot");
        bool pressed = t && !_apHeld;
        _apHeld = t;
        if (!pressed || _world.Ships.LocalShip == null)
            return;
        if (ApEngagedLocal)
            DisengageAutopilot();
        else
            EngageAutopilot();
    }

    // Whether the sampled input represents deliberate manual flight — any flight axis or thrust past
    // a quarter deflection, or afterburner. Cruise-control handback: this instant-disengages a
    // local autopilot (the server detects the same override independently). Firing does NOT count.
    private const float ManualOverrideDeadzone = 0.25f;

    private static bool ManualOverride(in ShipInputState i) =>
        Mathf.Abs(i.Thrust) > ManualOverrideDeadzone
        || Mathf.Abs(i.StrafeX) > ManualOverrideDeadzone
        || Mathf.Abs(i.StrafeY) > ManualOverrideDeadzone
        || Mathf.Abs(i.Yaw) > ManualOverrideDeadzone
        || Mathf.Abs(i.Pitch) > ManualOverrideDeadzone
        || Mathf.Abs(i.Roll) > ManualOverrideDeadzone
        || i.Boost;

    public override void _Process(double delta)
    {
        _input = SampleInput(delta);

        // Spawn handling. The class comes from the HUD spawn menu (RequestSpawn) or
        // the 1/2 keyboard shortcuts (handy alongside the menu). We only call the
        // reducer once the connection is live (LocalIdentity is set on connect),
        // retry after a short delay so an early/lost request recovers, and clear the
        // request once the ship actually exists.
        // One native connection: "connected" means the server's Welcome has landed.
        bool connected = _cm.State == ConnectionManager.ConnState.Connected;
        bool hasShip = _world.Ships.LocalShip != null;
        _hasShip = hasShip; // cached for _Input's capture gate (event-driven, runs between frames)

        TickAutoFlyBootstrap(connected, hasShip, delta);

        HandleMouseCapture(hasShip);

        TickSpawn(connected, hasShip, delta);

        // Prediction. The prediction tick lives in SERVER-tick space and is kept a
        // small fixed lead ahead of WorldRenderer.ServerTick by SLEWING the local
        // clock rate (a continuous nudge, never a discrete skip/stall), so it tracks
        // the server's real rate (~18.7 Hz here) without drifting away. Integration
        // is always fixed-dt, so determinism is preserved — only wall-clock pacing
        // is slewed. This makes predicted[N] and auth[N] index the same integration.
        var pc = _world.Ships.LocalShip;
        if (pc == null)
        {
            _hadShip = false;
            _acc = 0;
            ApEngagedLocal = false; // no ship (death / pre-spawn) → autopilot can't be engaged
            return;
        }
        if (!_hadShip)
            AnchorFreshShip();

        TickAutopilotAndBoost(pc);

        UpdateAdaptiveLead();
        if (Native)
            TickPing(delta);

        StepPrediction(pc, delta);

        TickDivergenceDebug(pc);
    }

    // Neutral input while the chat box is open, the sector overview map is up, or the
    // hangar screen is open, so typing/panning/clicking never steers or fires — the
    // ship coasts on held/neutral input.
    private ShipInputState SampleInput(double delta) =>
        _autoFly
            ? AutoInput()
            : (Chat.Capturing || SectorOverview.Active || ShipLoadout.Active ? new ShipInputState() : ReadInput(delta));

    private void TickAutoFlyBootstrap(bool connected, bool hasShip, double delta)
    {
        // Headless autofly: the server gates spawning behind BOTH a team pick ("Pick a team before
        // launching") and the lobby ready-up, so QuickJoin once on connect — take a side (BLUE) then
        // ready up to drive the match to Active before requesting a ship. Run the server with
        // --autostart for a perpetual match (this readies straight through it).
        if ((_autoFly || _hangarDemo) && connected && !_autoJoined)
        {
            // AUTOFLY_TEAM overrides the default BLUE pick so a second harness client can take the
            // other side (e.g. to spawn in that team's home sector for entrance/perf smokes).
            byte team = byte.TryParse(OS.GetEnvironment("AUTOFLY_TEAM"), out byte tv) && tv <= 1 ? tv : (byte)0;
            _net?.SetTeam(team);
            _net?.SetReady(true);
            _autoJoined = true;
        }

        // --hangar-demo: the Lobby's auto-deploy edge (Lobby→Active) can lose the race against
        // our own team-set round-trip, so raise deploy intent ourselves once the match is live —
        // the Hud then opens the mandatory spawn hangar and the harness drives it. A hard
        // failsafe quits the process if the demo somehow never completes (never hang a window).
        if (_hangarDemo)
        {
            if (connected && !hasShip && _world.Phase == MatchPhase.Active && !Hud.DeployRequested)
            {
                GD.Print("[hangar-demo] match active — requesting deploy");
                GetNodeOrNull<Hud>("../Hud")?.RequestDeploy();
            }
            _hangarDemoElapsed += delta;
            if (_hangarDemoElapsed > 90)
            {
                GD.Print("HANGAR_DEMO_TIMEOUT: quitting (demo never completed)");
                GetTree().Quit();
            }
        }
    }

    private void TickSpawn(bool connected, bool hasShip, double delta)
    {
        if (!hasShip && !Chat.Capturing && !ShipLoadout.Active)
        {
            if (Input.IsPhysicalKeyPressed(Key.Key1))
                _spawnRequest = ShipClass.Scout;
            if (Input.IsPhysicalKeyPressed(Key.Key2))
                _spawnRequest = ShipClass.Fighter;
            if (Input.IsPhysicalKeyPressed(Key.Key3))
                _spawnRequest = ShipClass.Bomber;
        }

        if (_spawnPending)
        {
            _spawnRetry -= delta;
            if (_spawnRetry <= 0)
                _spawnPending = false;
        }
        if (hasShip)
        {
            _spawnPending = false;
            _spawnRequest = null;
            SpawnHint = null;
            // Clear the NAV waypoint on arrival — but only once autopilot has DISENGAGED (the server
            // drops ShipFlagAutopilot when the ship reaches its mark). Gating on !ApEngagedLocal keeps
            // the line up for the whole trip no matter how near the waypoint was dropped; a bare
            // distance check would wipe any waypoint set within the arrive band before the line ever
            // drew. (A manual takeover far from the mark leaves the waypoint so T can re-engage it.)
            if (!ApEngagedLocal)
                TargetMarkers.DismissWaypointIfReached(_world.LocalSector, _world.Ships.LocalShip!.GlobalPosition);
        }
        else if (connected && !_spawnPending && _spawnRequest is { } cls)
        {
            // Stage-2 buy pre-check: don't spam a request the latest snapshot says will fail (locked
            // hull / can't afford / a launch base that can't serve the hull). The server stays
            // authoritative; this only suppresses the doomed send and surfaces a reason. The request
            // stays queued so it auto-fires once affordable (e.g. when the paycheck lands) or once
            // the pilot picks a legal base. Team pre-spawn comes from the Welcome assignment.
            byte team = _world.LocalTeam ?? _net?.MyTeam ?? 0;
            // Launch-base pre-checks (2026-07-21 launch-station-classes): resolve the sidebar pick
            // to its base TYPE; 0 = "server default-resolves" (skip — the server scans for a legal
            // base itself). Mirrors TryResolveLaunchSite's class + launch-bay gates.
            bool wrongBase = false,
                noBay = false;
            ulong selBase = LoadoutState.Shared.SelectedBaseId;
            if (_defs != null && selBase != 0)
                foreach (var (id, _, bteam, _, typeId) in _world.Bases.Known())
                    if (id == selBase && bteam == team)
                    {
                        wrongBase = !_defs.HullMayLaunchFrom((byte)cls, typeId);
                        noBay = !_defs.BaseLaunchCapable(typeId);
                        break;
                    }
            var gate = _world.TeamState.CheckSpawnGate(team, (byte)cls, wrongBase);
            if (gate == TeamStateStore.SpawnGate.Allow && !noBay)
            {
                // Spawn on the authoritative sim server (honored only while the match is Active;
                // the request simply retries until then), carrying the hangar's chosen consumable
                // hold for this class (server validates + falls back to the hull default if invalid).
                // Autofly ships a fixed test hold (3 mines + 1 decoy + 1 recon-probe pack — mass
                // 3+1+2 = 6, exactly the scout's free payload alongside its cannon) so headless runs
                // exercise the full MsgSpawn cargo path AND actually carry probes for the pinned
                // DropProbe in AutoInput to deploy (cargo-ids: 2 mine, 3 decoy, 4 recon-probe).
                var hold = _autoFly
                    ? new (uint cargoId, byte count)[] { (2u, (byte)3), (3u, (byte)1), (4u, (byte)1) }
                    : LoadoutState.Shared.CargoFor((byte)cls);
                // v36: carry the hangar sidebar's launch-base pick (0 = server default; the
                // server validates friendly+alive and silently falls back). The weapon-slot
                // override tail rides alongside (autofly sends none — authored loadout keeps the
                // headless smoke deterministic); the server echoes the accepted loadout back on
                // MsgShipLoadout.
                var mounts =
                    !_autoFly && _defs?.GetHardpoints((byte)cls) is { } hps
                        ? LoadoutState.Shared.WeaponOverridesFor((byte)cls, hps)
                        : null;
                _net?.RequestSpawn((byte)cls, hold, LoadoutState.Shared.SelectedBaseId, mounts);
                _spawnPending = true;
                _spawnRetry = 1.0;
                SpawnHint = null;
            }
            else
            {
                SpawnHint =
                    gate == TeamStateStore.SpawnGate.Locked ? $"{cls} is locked"
                    : gate == TeamStateStore.SpawnGate.WrongBase ? $"{cls} can't launch from the selected base"
                    : noBay ? "Selected base has no launch bay"
                    : $"Not enough credits for {cls}";
            }
        }
    }

    private void AnchorFreshShip()
    {
        _predTick = _world.ServerTick; // anchor to authority; first reconcile aligns the rest
        _acc = 0;
        _stepsSinceSpawn = 0;
        _hadShip = true;
        // Fresh ship: force the first step to send (server starts from default input).
        _lastSentInput = default;
        _lastSentTick = 0;
        ApEngagedLocal = false; // a fresh launch starts hands-on
        // Launch locks the cursor to flight immediately — steering is captured relative mouse
        // motion (see _Input / ReadInput), so the pilot flies straight out of the hangar without
        // a click to capture first. ShipLoadout is deliberately NOT in the guard: the mandatory
        // spawn hangar is the launch source and is still in the tree this frame (it closes once
        // the ship exists, Hud._Process), and its _ExitTree doesn't touch MouseMode, so this
        // capture sticks. Skipped in headless autofly (no cursor) and while a real modal owns the
        // cursor, so we never yank it out from under a menu/map/chat.
        if (!_autoFly && !EscapeMenu.Active && !SettingsDialog.Active && !SectorOverview.Active && !Chat.Capturing)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            _mouseDelta = Vector2.Zero;
        }
    }

    private void TickAutopilotAndBoost(PredictionController pc)
    {
        // Afterburner (Shift): a real flight input now — extra forward thrust and a
        // raised speed cap while held (see FlightModel Boost). It rides in the networked
        // ShipInput so the server integrates the same boost the client predicted (no
        // reconcile storm), and still drives the engine glow. Autofly pins it on so
        // headless runs exercise the boost + exhaust path.
        // Gate on a captured cursor too: freeing the mouse (Esc → command mode, or the escape
        // menu) means the pilot isn't flying, so a held Shift shouldn't keep the burner lit.
        bool boost =
            _autoFly
            || (
                Input.MouseMode == Input.MouseModeEnum.Captured
                && !Chat.Capturing
                && !SectorOverview.Active
                && !ShipLoadout.Active
                && Input.IsActionPressed("afterburner")
            );
        _input.Boost = boost;
        // While the server flies us the boost decision is the server's; PredictionController drives the
        // plume from the authoritative AbPower instead (feeding the local key here would fight it).
        if (!pc.AutopilotActive)
            pc.SetAfterburner(boost ? 1f : 0f);

        // Autopilot: hand back to manual the instant the pilot makes a real flight input (cruise-
        // control style; firing doesn't count), then process the T engage/disengage toggle. Skipped
        // under headless autofly. Neutral input while a menu/map/chat owns the sticks won't trip the
        // handback (the input was zeroed above).
        if (!_autoFly)
        {
            if (ApEngagedLocal && ManualOverride(_input))
                DisengageAutopilot();
            HandleAutopilotToggle();
        }

        // An escape pod is unarmed: drop BOTH fire channels so the player can't shoot and the
        // client doesn't predict muzzle ghosts the server (which also ignores pod fire) won't make.
        if (pc.IsPod)
        {
            _input.Firing = false;
            _input.Firing2 = false;
            _input.DropChaff = false;
            _input.DropMine = false;
            _input.DropProbe = false;
        }
    }

    private void TickPing(double delta)
    {
        // Probe RTT on a steady cadence so the adaptive lead has live latency to size
        // against (native mode has no reducer ack to piggyback on).
        _pingAcc += delta;
        if (_pingAcc >= PingIntervalSec)
        {
            _pingAcc = 0;
            uint nonce = ++_pingNonce;
            _sentAt[nonce] = Time.GetTicksMsec();
            _net!.SendPing(nonce);
        }
    }

    private void StepPrediction(PredictionController pc, double delta)
    {
        // Follow-authority (autopilot): the server is steering, so the client suspends its own-ship
        // prediction (Step) and renders from eased authoritative snapshots (PredictionController). We
        // KEEP sampling + sending real stick input below so the server can detect a manual override.
        // On the falling edge (handback), re-anchor the prediction clock to authority so predicted[N]
        // realigns with auth[N]; SetAutopilot already cleared the stale buffer + RebaseTo'd.
        bool apActive = pc.AutopilotActive;
        if (_apActivePrev && !apActive)
        {
            _predTick = _world.ServerTick; // re-anchor; slew rebuilds the lead
            _lastSentInput = default; // force the first hands-on input to send
            _lastSentTick = 0;
        }
        _apActivePrev = apActive;

        int lead = (int)_predTick - (int)_world.ServerTick;
        float slew = Mathf.Clamp((_targetLead - lead) * SlewGain, -MaxSlew, MaxSlew);
        _acc += delta * (1f + slew);

        int budget = MaxStepsPerFrame;
        while (_acc >= FlightModel.Dt && budget > 0)
        {
            _acc -= FlightModel.Dt;
            budget--;

            _predTick++;
            _stepsSinceSpawn++;
            if (!InputsEqual(_input, _lastSentInput) || _predTick - _lastSentTick >= InputKeepaliveTicks)
            {
                // Gameplay is native-only now (a local ship only ever exists in native mode).
                // The sim server's tick-stamped input ring replays this exactly at _predTick;
                // RTT is sampled separately via the Ping/Pong probe above. Sent even while engaged so
                // the server sees our neutral (or override) stick for handback detection.
                _net?.SendInput(_predTick, _input);
                _lastSentInput = _input;
                _lastSentTick = _predTick;
            }
            // Skip local prediction while the server flies us; snapshots drive the render instead.
            if (!apActive)
                foreach (var shot in pc.Step(_input, _predTick))
                    _world.Bolts.SpawnLocalBolt(
                        shot.Pos,
                        shot.Vel,
                        shot.Dir,
                        shot.LifeSec,
                        shot.BoltRadius,
                        shot.BoltLength,
                        shot.IsHeal
                    );
        }
    }

    private void TickDivergenceDebug(PredictionController pc)
    {
        // T5 divergence injection (debug). Press P to force a misprediction and
        // watch reconciliation snap + re-sim back; autofly fires one self-test.
        // Debug-build only so release exports never expose the key.
        bool perturb = OS.IsDebugBuild() && !Chat.Capturing && Input.IsPhysicalKeyPressed(Key.P);
        if (perturb && !_perturbHeld)
            pc.InjectDivergence(new Vector3(25f, 0f, 0f));
        _perturbHeld = perturb;

        if (_autoFly && !_combatTest && !_selfTestDone && _stepsSinceSpawn >= 100)
        {
            pc.InjectDivergence(new Vector3(25f, 0f, 0f));
            _selfTestDone = true;
        }
    }

    // Size the prediction lead to the live latency: cover a full round trip plus a
    // jitter margin so an ApplyInput reliably arrives before its tick is simulated.
    // Uses the smoothed PingMs/JitterMs, so it tracks the link without thrashing; the
    // clock slew (in _Process) eases any change in gently. No-op when STDB_LEAD pins it.
    private void UpdateAdaptiveLead()
    {
        if (_leadFromEnv || PingMs <= 0f)
            return;
        float budgetMs = PingMs + 2f * JitterMs; // RTT + ~2σ jitter
        int desired = Mathf.CeilToInt(budgetMs / (FlightModel.Dt * 1000f)) + 1;
        _targetLead = Mathf.Clamp(desired, DefaultTargetLead, 15);
    }

    // The server echoed our ping nonce (native mode): same RTT measurement, different trigger.
    private void OnPong(uint nonce)
    {
        if (_sentAt.Remove(nonce, out var sent))
            RecordRtt(sent);
    }

    // Smooth a round-trip sample (EWMA) and track its jitter for the HUD + adaptive lead.
    private void RecordRtt(double sentMsec)
    {
        float rtt = (float)(Time.GetTicksMsec() - sentMsec);
        float dev = Mathf.Abs(rtt - PingMs);
        PingMs = PingMs <= 0f ? rtt : PingMs * 0.9f + rtt * 0.1f;
        JitterMs = JitterMs <= 0f ? dev : JitterMs * 0.9f + dev * 0.1f;
        // Drop stale unacked sends so the map can't grow unbounded.
        if (_sentAt.Count > 256)
            _sentAt.Clear();
    }

    // Accumulate raw mouse motion only while the cursor is captured (consumed once per frame
    // in ReadInput; visible-cursor motion is ignored so menu interaction never steers), AND
    // drive the cursor capture/release transitions. Doing the MouseMode change here — in
    // response to the real Esc/click EVENT rather than polling in _Process — keeps the OS
    // cursor's hide/show in lockstep with the mode: on macOS a Captured set from _Process
    // leaves a ghost cursor pinned at screen center until the next motion event.
    //
    // Esc is two-step: the first press RELEASES the cursor (so the OS cursor is reachable
    // mid-flight); with the cursor already free a second press opens the escape menu. A left
    // click in the viewport recaptures. Skipped under --autofly (headless has no real cursor),
    // while Chat/SectorOverview own the cursor (they restore it on close), and while the
    // escape menu / settings dialog are up (so clicking their buttons never recaptures).
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            _mouseDelta += mm.Relative;

        if (
            _autoFly
            || !_hasShip
            || Chat.Capturing
            || SectorOverview.Active
            || ShipLoadout.Active
            || EscapeMenu.Active
            || SettingsDialog.Active
        )
            return;

        if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
        {
            if (ZoomView.Active)
                return; // the scope owns Esc while open (its own handler closes it) — don't release/menu
            if (Input.MouseMode == Input.MouseModeEnum.Captured)
            {
                Input.MouseMode = Input.MouseModeEnum.Visible; // first Esc frees the cursor (command mode)
            }
            else if (TargetMarkers.FocusedId != 0 || SectorOverview.SelectionCount > 0)
            {
                // Cursor already free with a target / command group: Esc backs out one level — clear the
                // combat target and the group first; only the next press (with nothing selected) opens
                // the escape menu.
                TargetMarkers.SetFocus(0);
                SectorOverview.ClearFlightSelection();
            }
            else
            {
                EscapeMenu.Open(this, EscapeMenu.Context.Flight);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (
            @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
            && Input.MouseMode != Input.MouseModeEnum.Captured
            // In cursor-free flight command mode SectorOverview owns the left button (box-select and
            // the plain-click re-lock, which calls RecaptureCursor below), so don't recapture on press.
            && !SectorOverview.FlightCommandActive
        )
        {
            RecaptureCursor();
        }
    }

    // Re-lock the cursor for mouse-look and drop any stale accumulated motion so recapture doesn't
    // snap the view. Public so SectorOverview's cursor-free plain-click re-lock routes through the
    // same seam (it owns the left button while the flight command gestures are live).
    public void RecaptureCursor()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _mouseDelta = Vector2.Zero; // drop any motion from the recapture gesture
    }

    // Release the cursor for the spawn menu (dead / not yet spawned). The flying-state
    // capture/release lives in _Input; this only handles the no-ship menu case each frame.
    private void HandleMouseCapture(bool flying)
    {
        if (
            _autoFly
            || Chat.Capturing
            || SectorOverview.Active
            || ShipLoadout.Active
            || EscapeMenu.Active
            || SettingsDialog.Active
        )
            return;
        if (!flying && Input.MouseMode == Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Visible; // free cursor for the menu
    }

    private ShipInputState ReadInput(double delta)
    {
        // Fold this frame's captured-cursor motion into the self-centering virtual stick.
        // Mouse-right turns right (matches the Right arrow → -Yaw convention); mouse-up
        // pitches like the Up arrow unless inverted. The deflection PERSISTS and eases back
        // toward center each frame (frame-rate-independent exp decay) so the rate-limited
        // flight model gets a held command — releasing the mouse straightens the ship.
        bool look = Input.MouseMode == Input.MouseModeEnum.Captured;
        Vector2 m = _mouseDelta;
        _mouseDelta = Vector2.Zero;
        if (look)
        {
            // Fine aiming while scoped: the telescopic zoom divides the effective mouse gain by
            // the magnification (1 when closed), so a 20x scope turns 20x slower per pixel.
            Vector2 md = m / ZoomView.Magnification;
            _stickYaw = Mathf.Clamp(_stickYaw - md.X * _mouseSens, -1f, 1f);
            _stickPitch = Mathf.Clamp(_stickPitch + (_mouseInvert ? -md.Y : md.Y) * _mouseSens, -1f, 1f);
            float ret = Mathf.Exp(-MouseReturnPerSec * (float)delta);
            _stickYaw *= ret;
            _stickPitch *= ret;
        }
        else
        {
            _stickYaw = 0f; // cursor freed (menu/Esc): no residual steering
            _stickPitch = 0f;
        }

        return new ShipInputState
        {
            // Thrust is now a THROTTLE: W = full forward throttle (commands MaxSpeed),
            // S = weak reverse. Yaw/Pitch/Roll are commanded turn-RATE fractions.
            // Rebindable via the InputMap (InputBindings). GetAxis(negative, positive) reproduces
            // the old Axis(pos, neg) signs; analog gamepad axes feed through for free.
            Thrust = Input.GetAxis("thrust_back", "thrust_forward"), // forward throttle / reverse
            StrafeX = Input.GetAxis("strafe_left", "strafe_right"), // strafe right / left
            StrafeY = Input.GetAxis("strafe_down", "strafe_up"), // strafe up / down
            Yaw = Mathf.Clamp(Input.GetAxis("yaw_right", "yaw_left") + _stickYaw, -1f, 1f),
            Pitch = Mathf.Clamp(Input.GetAxis("pitch_down", "pitch_up") + _stickPitch, -1f, 1f),
            Roll = Input.GetAxis("roll_left", "roll_right"), // roll right / left
            Firing = Input.IsActionPressed("fire_primary") || (look && Input.IsMouseButtonPressed(MouseButton.Left)),
            // Secondary (missile) fire: the fire_secondary action, or RMB while mouse-look owns the
            // cursor — the same capture gate the LMB primary uses so a right-click on a menu never
            // launches. The lock target is whatever TargetMarkers has focused (0 = none).
            Firing2 = Input.IsActionPressed("fire_secondary") || (look && Input.IsMouseButtonPressed(MouseButton.Right)),
            // Dispensers: chaff / mine field / recon probe. Held-input replay re-fires the flag, so
            // the SERVER's cadence gate is the debounce — we do NOT client-edge-detect (that would
            // desync from the authoritative cadence).
            DropChaff = Input.IsActionPressed("drop_chaff"),
            DropMine = Input.IsActionPressed("drop_mine"),
            DropProbe = Input.IsActionPressed("drop_probe"),
            LockTargetId = TargetMarkers.WireLockId,
        };
    }

    // Deterministic scripted flight for headless verification — representative of
    // NORMAL play: continuous gentle weaving (smooth, like a human steering),
    // rather than instant input reversals (an unrealistic worst case) or a pinned
    // max-rate turn. Driven by steps-since-spawn so it's reproducible.
    private ShipInputState AutoInput()
    {
        // Combat test: spawn facing the sector center (server-side), so flying
        // straight ahead + firing sends two opposing clients head-on for a
        // deterministic hit/damage/death check.
        if (_combatTest)
            return new ShipInputState { Thrust = 1f, Firing = true };

        // Ram test harness (--ram-test): chase a MOVING remote ship and ride onto it so the local
        // predictor repeatedly resolves a real ship-vs-ship contact — the ram-time-alignment work needs
        // genuine per-tick contacts against MOVING obstacles to measure (a stationary obstacle makes the
        // time-alignment a no-op, and two same-team autofly clients on the default weave never touch).
        //   • Target the nearest ship whose speed exceeds RamMinTargetSpeed (skip parked/idle ships — a
        //     stationary target just makes the fast Scout orbit it fruitlessly and wouldn't exercise the
        //     fix). COMMIT to it (re-picking "nearest" every frame orbits a cluster without committing)
        //     until it despawns / drifts past RamReacquire, then re-acquire.
        //   • Lead-pursuit steering (aim where the target WILL be when we arrive: target.Pos +
        //     target.Vel × leadT), proportional yaw/pitch from the local-frame error, replicating
        //     AutoSteer.SteerToPoint's sign convention INLINE (local +Z forward; yaw = +local.X, pitch =
        //     −local.Y; bang-bang while behind) so shared/AutoSteer stays untouched.
        //   • THROTTLE-DOWN when close (dist ≤ RamCloseDist): a Scout at full speed (~173) has a turn
        //     radius far larger than the ram distance, so it orbits a target ~15u out and rarely touches;
        //     easing the throttle shrinks the turn radius so it RIDES right onto the target's pose. This
        //     is the measurement's engine: it parks the predicted ship on the remote ship's collision
        //     silhouette every tick, so the predictor resolves a contact every tick — a stress version of
        //     the plan's exact bug (per-tick collision against a remote ship's pose). Zero effect w/o flag.
        if (_ramTest && _world.Ships.LocalShip is { } rammer)
        {
            const float ramReacquire = 1200f; // drop a target that has drifted this far, re-pick nearest mover
            const float ramMinTargetSpeed = 20f; // ignore ~parked ships — the fix only matters for movers
            const float ramCloseDist = 120f; // within this, throttle down to ride onto the target
            Vector3 gp = rammer.GlobalPosition;
            RemoteShip? target = null;

            // Keep the committed target while it's still a valid, reasonably-near, still-MOVING obstacle
            // (drop it if it stopped — otherwise the rammer gets stuck fruitlessly orbiting a parked ship).
            if (
                _ramTargetId != 0
                && _world.Ships.Nodes.TryGetValue(_ramTargetId, out var held)
                && held is RemoteShip heldRs
                && heldRs.Visible
                && SectorView.InSector(heldRs, _world.LocalSector)
                && heldRs.Velocity.Length() >= ramMinTargetSpeed
                && gp.DistanceSquaredTo(heldRs.GlobalPosition) <= ramReacquire * ramReacquire
            )
                target = heldRs;

            if (target is null) // (re)acquire the nearest visible MOVING remote ship in the local sector
            {
                float bestD2 = float.MaxValue;
                foreach (var node in _world.Ships.Nodes.Values)
                {
                    if (node is not RemoteShip rs || !rs.Visible)
                        continue;
                    if (!SectorView.InSector(rs, _world.LocalSector))
                        continue;
                    if (rs.Velocity.Length() < ramMinTargetSpeed)
                        continue;
                    float d2 = gp.DistanceSquaredTo(rs.GlobalPosition);
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        target = rs;
                    }
                }
                _ramTargetId = target?.ShipId ?? 0;
            }

            if (target is not null)
            {
                float dist = (target.GlobalPosition - gp).Length();
                float ownSpeed = Mathf.Max(rammer.Speed, 50f); // Scout cruise ~173; floor avoids /0 at spawn
                float leadT = Mathf.Clamp(dist / ownSpeed, 0f, 1.0f);
                Vector3 aim = target.GlobalPosition + target.Velocity * leadT;
                var q = rammer.GlobalBasis.GetRotationQuaternion();
                float yaw = 0f,
                    pitch = 0f;
                Vector3 toT = aim - gp;
                if (toT.LengthSquared() > 1e-6f)
                {
                    Vector3 local = (q.Inverse() * toT.Normalized()); // ship frame, +Z forward
                    const float turnGain = 4f;
                    yaw = local.Z < 0f ? (local.X >= 0f ? 1f : -1f) : Mathf.Clamp(local.X * turnGain, -1f, 1f);
                    pitch = local.Z < 0f ? (local.Y >= 0f ? -1f : 1f) : Mathf.Clamp(-local.Y * turnGain, -1f, 1f);
                }
                // Ease the throttle for the final approach so the turn radius shrinks enough to ride onto
                // the target instead of orbiting it; full thrust (+boost) to close the gap from range.
                bool close = dist <= ramCloseDist;
                return new ShipInputState
                {
                    Thrust = close ? 0.45f : 1f,
                    Yaw = yaw,
                    Pitch = pitch,
                    Boost = !close,
                };
            }
        }

        // Warp test: drop a few mines on a short straight run, then steer MANUALLY straight into the
        // sector's gate to fire the real warp path (cover → swap → reveal + minefield reconcile) this
        // mode exists to smoke. Manual AutoSteer with a NO-OP avoid delegate (unlike the server
        // autopilot, whose obstacle-avoidance treats the solid aleph as a barrier and orbits its mouth
        // without ever entering the warp-trigger radius) plows the ship straight through the mouth.
        if (_warpTest && _world.Ships.LocalShip is { } wpShip)
        {
            var gates = _world.Alephs.Visible();
            bool dropping = _stepsSinceSpawn * FlightModel.Dt < 6f;
            if ((_stepsSinceSpawn % 40) == 0) // ~2s cadence at 20Hz sim
                Log.Print(
                    $"[warp-test] sector {_world.ViewSector}, gates {gates.Count}, t {_stepsSinceSpawn * FlightModel.Dt:0.0}s"
                );
            if (dropping || gates.Count == 0)
                return new ShipInputState { Thrust = 1f, DropMine = dropping };

            // Head for the nearest visible gate. Coords are identical between Godot and sim space
            // (ShipMath.ToGodot is the identity), so the node transform feeds AutoSteer directly.
            Vector3 gp = wpShip.GlobalPosition;
            Vector3 best = gates[0].Pos;
            foreach (var g in gates)
                if (gp.DistanceSquaredTo(g.Pos) < gp.DistanceSquaredTo(best))
                    best = g.Pos;
            var q = wpShip.GlobalBasis.GetRotationQuaternion();
            var steer = AutoSteer.SteerToPoint(
                new Vec3(gp.X, gp.Y, gp.Z),
                new Quat(q.X, q.Y, q.Z, q.W),
                new Vec3(best.X, best.Y, best.Z),
                turnGain: 3f,
                thrustWhenFacing: 1f,
                avoid: (_, dir) => dir
            );
            return steer;
        }

        float t = _stepsSinceSpawn * FlightModel.Dt; // sim seconds
        return new ShipInputState
        {
            Thrust = 1f,
            Yaw = 0.4f * Mathf.Sin(t * 0.6f), // weave, ~10 s period
            Pitch = 0.2f * Mathf.Sin(t * 0.37f),
            Firing = true, // exercise projectile spawn/cull
            // Pin the dispensers on like boost/Firing: the server cadence gate turns the held
            // flags into periodic drops, so headless runs exercise the full chaff/mine/probe wire
            // (input flag -> sim -> MsgChaff/MsgMinefields/MsgProbes -> client FX) with whatever the
            // spawned hold carried.
            DropChaff = true,
            DropMine = true,
            DropProbe = true,
        };
    }
}
