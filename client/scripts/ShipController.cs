using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Reads local input, runs the fixed-rate (20 Hz) input/prediction loop, and
// calls the ApplyInput reducer. Also handles the (temporary) spawn key for T4.
// Input is sampled every render frame but only applied + sent + predicted on a
// simple fixed 20 Hz accumulator, so the prediction cadence is regular.
public partial class ShipController : Node
{
	private const int MaxStepsPerFrame = 5;   // spiral-of-death guard
	private const int DefaultTargetLead = 3;  // ticks the prediction runs ahead of authority
	private const float SlewGain = 0.08f;     // how hard the local clock tracks the server
	private const float MaxSlew = 0.30f;      // cap the clock rate adjustment (±30%)

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
	// Phase-1b: when GameNetClient is active (SIM_URI set), spawn + input go over the
	// native sim socket instead of STDB reducers; everything else (prediction, defs,
	// rendering) is unchanged. Null/inactive = pure STDB path.
	private GameNetClient? _net;
	private bool Native => _net is { Active: true };

	private double _acc;
	private uint _predTick;             // prediction tick, in SERVER-tick space
	private bool _hadShip;
	private int _stepsSinceSpawn;
	private ShipInputState _input;

	// On-change input sending: ApplyInput goes out only when the stick state differs from
	// the last SENT input, or the keepalive window lapses. The server replays the last
	// received input for the silent ticks (held input) — exactly what our own prediction
	// does with an unchanged stick — so auth == prediction still holds while idle/cruise
	// ticks cost no reducer transaction at all (~10x fewer under keyboard flight; mouse
	// easing changes the stick every tick, so active maneuvering still sends at full rate).
	private const uint InputKeepaliveTicks = 20;   // ~1 s at 20 Hz; also paces PingMs samples
	private ShipInputState _lastSentInput;
	private uint _lastSentTick;
	private ShipClass? _spawnRequest;   // class chosen via HUD menu / 1-2 keys; cleared once flying
	private bool _spawnPending;
	private double _spawnRetry;
	private bool _perturbHeld;          // edge-detect the P debug key

	// Mouse-look aiming (Allegiance style). The M0 flight model integrates yaw/pitch as
	// commanded turn RATES that slew in under a torque limit, so it needs a HELD stick
	// deflection — a raw per-frame pixel delta is a one-tick transient the rate-limited
	// slew can't act on (small moves vanish, large moves saturate -> jerky, all-or-nothing
	// aim). So the mouse drives a self-CENTERING virtual stick: captured-cursor motion is
	// accumulated (in _Input) into a persistent deflection (_stickYaw/_stickPitch) that
	// eases back toward center each frame when the mouse stops. Push to turn, release to
	// straighten. This is purely an input-sampling change; the flight dynamics are untouched.
	// The cursor is captured while flying (Esc releases, click recaptures); arrow keys still
	// work as a fallback and sum with the stick. STDB_MOUSE_SENS tunes feel (px->deflection),
	// STDB_MOUSE_INVERT=1 flips pitch. (Sens + return rate below want a quick in-flight tune.)
	private const float DefaultMouseSens = 0.01f;   // px -> stick deflection per frame
	private const float MouseReturnPerSec = 8f;      // how fast the virtual stick eases back to center
	private float _mouseSens = DefaultMouseSens;
	private bool _mouseInvert;
	private Vector2 _mouseDelta;        // captured-cursor motion accumulated since last sample
	private float _stickYaw, _stickPitch;  // persistent self-centering virtual-stick deflection (-1..1)
	private bool _hasShip;             // mirrors _world.LocalShip != null, set each _Process for _Input's capture gate

	// Headless verification: `--autofly` auto-spawns a Scout and flies a fixed
	// input so the full ApplyInput -> SimTick -> reconcile loop can be checked
	// without a human at the keyboard.
	// Exact field compare (bools + floats sampled from the same key/stick state repeat
	// bit-identically while unchanged, so == is the right test — no epsilon wanted).
	private static bool InputsEqual(in ShipInputState a, in ShipInputState b) =>
		a.Thrust == b.Thrust && a.StrafeX == b.StrafeX && a.StrafeY == b.StrafeY
		&& a.Yaw == b.Yaw && a.Pitch == b.Pitch && a.Roll == b.Roll
		&& a.Firing == b.Firing && a.Boost == b.Boost && a.Coast == b.Coast;

	private bool _autoFly;
	private bool _autoJoined;            // autofly QuickJoins (team + ready) once on connect
	private bool _selfTestDone;         // autofly fires one divergence injection
	private bool _combatTest;           // --combat-test: fly straight + fire (head-on damage check)

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
	private const double PingIntervalSec = 0.25;   // 4 Hz

	public override void _Ready()
	{
		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_world = GetNode<WorldRenderer>("../WorldRenderer");

		if (int.TryParse(OS.GetEnvironment("STDB_LEAD"), out var lead))
		{
			_targetLead = Mathf.Clamp(lead, 1, 15);
			_leadFromEnv = true;   // pin it; skip the adaptive sizing below
		}

		if (float.TryParse(OS.GetEnvironment("STDB_MOUSE_SENS"), out var sens) && sens > 0f)
			_mouseSens = sens;
		_mouseInvert = OS.GetEnvironment("STDB_MOUSE_INVERT") is "1" or "true";

		// Latency for the adaptive lead / HUD readout is sampled in native mode via the
		// Ping/Pong probe (the in-STDB ApplyInput reducer-ack path was removed with the sim).
		_net = GetNodeOrNull<GameNetClient>("../GameNetClient");
		if (_net is not null)
			_net.Pong += OnPong;

		var autoClass = ShipClass.Scout;
		foreach (var a in OS.GetCmdlineArgs())
		{
			if (a == "--autofly") _autoFly = true;
			if (a == "--fighter") autoClass = ShipClass.Fighter;   // autofly picks Fighter (dev verify)
			if (a == "--bomber") autoClass = ShipClass.Bomber;     // autofly picks Bomber (dev verify)
			if (a == "--combat-test") { _autoFly = true; _combatTest = true; }
		}
		// Headless runs are otherwise uncapped: _Process spins as fast as possible,
		// flooding ApplyInput and racing the prediction far ahead of the 20 Hz
		// server, which inflates the prediction lead. Cap to a realistic display
		// rate so the autofly's reconcile behaviour matches a real client.
		if (_autoFly)
		{
			Engine.MaxFps = 60;
			_spawnRequest = autoClass;         // autofly flies Scout, or Fighter with --fighter
		}
	}

	// Called by the HUD spawn menu. Picks the class to spawn; the actual reducer
	// call happens in _Process once the connection is live (with retry).
	public void RequestSpawn(ShipClass cls)
	{
		if (_world.LocalShip == null) _spawnRequest = cls;
	}

	public override void _Process(double delta)
	{
		// Neutral input while the chat box is open or the sector overview map is up, so
		// typing/panning never steers or fires — the ship coasts on held/neutral input.
		_input = _autoFly ? AutoInput() : (Chat.Capturing || SectorOverview.Active ? new ShipInputState() : ReadInput(delta));

		// Spawn handling. The class comes from the HUD spawn menu (RequestSpawn) or
		// the 1/2 keyboard shortcuts (handy alongside the menu). We only call the
		// reducer once the connection is live (LocalIdentity is set on connect),
		// retry after a short delay so an early/lost request recovers, and clear the
		// request once the ship actually exists.
		// One native connection: "connected" means the server's Welcome has landed.
		bool connected = _cm.State == ConnectionManager.ConnState.Connected;
		bool hasShip = _world.LocalShip != null;
		_hasShip = hasShip;   // cached for _Input's capture gate (event-driven, runs between frames)

		// Headless autofly: the server gates spawning behind the lobby ready-up, so ready up once
		// on connect to drive the match to Active before requesting a ship (teams are balanced
		// server-side). Run the server with --autostart for fully unattended benchmarks.
		if (_autoFly && connected && !_autoJoined)
		{
			_net?.SetReady(true);
			_autoJoined = true;
		}

		HandleMouseCapture(hasShip);

		if (!hasShip && !Chat.Capturing)
		{
			if (Input.IsPhysicalKeyPressed(Key.Key1)) _spawnRequest = ShipClass.Scout;
			if (Input.IsPhysicalKeyPressed(Key.Key2)) _spawnRequest = ShipClass.Fighter;
			if (Input.IsPhysicalKeyPressed(Key.Key3)) _spawnRequest = ShipClass.Bomber;
		}

		if (_spawnPending)
		{
			_spawnRetry -= delta;
			if (_spawnRetry <= 0) _spawnPending = false;
		}
		if (hasShip)
		{
			_spawnPending = false;
			_spawnRequest = null;
		}
		else if (connected && !_spawnPending && _spawnRequest is { } cls)
		{
			// Spawn on the authoritative sim server (honored only while the match is Active;
			// the request simply retries until then).
			_net?.RequestSpawn((byte)cls);
			_spawnPending = true;
			_spawnRetry = 1.0;
		}

		// Prediction. The prediction tick lives in SERVER-tick space and is kept a
		// small fixed lead ahead of WorldRenderer.ServerTick by SLEWING the local
		// clock rate (a continuous nudge, never a discrete skip/stall), so it tracks
		// the server's real rate (~18.7 Hz here) without drifting away. Integration
		// is always fixed-dt, so determinism is preserved — only wall-clock pacing
		// is slewed. This makes predicted[N] and auth[N] index the same integration.
		var pc = _world.LocalShip;
		if (pc == null)
		{
			_hadShip = false;
			_acc = 0;
			return;
		}
		if (!_hadShip)
		{
			_predTick = _world.ServerTick;   // anchor to authority; first reconcile aligns the rest
			_acc = 0;
			_stepsSinceSpawn = 0;
			_hadShip = true;
			// Fresh ship: force the first step to send (server starts from default input).
			_lastSentInput = default;
			_lastSentTick = 0;
		}

		// Afterburner (Shift): a real flight input now — extra forward thrust and a
		// raised speed cap while held (see FlightModel Boost). It rides in the networked
		// ShipInput so the server integrates the same boost the client predicted (no
		// reconcile storm), and still drives the engine glow. Autofly pins it on so
		// headless runs exercise the boost + exhaust path.
		bool boost = _autoFly || (!Chat.Capturing && !SectorOverview.Active && Input.IsPhysicalKeyPressed(Key.Shift));
		_input.Boost = boost;
		pc.SetAfterburner(boost ? 1f : 0f);

		// An escape pod is unarmed: drop firing so the player can't shoot and the client
		// doesn't predict muzzle ghosts the server (which also ignores pod fire) won't make.
		if (pc.IsPod)
			_input.Firing = false;

		UpdateAdaptiveLead();
		if (Native)
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
					// RTT is sampled separately via the Ping/Pong probe above.
					_net?.SendInput(_predTick, _input);
					_lastSentInput = _input;
					_lastSentTick = _predTick;
			}
			if (pc.Step(_input, _predTick) is PredictionController.PredictedShot shot)
				_world.SpawnLocalBolt(shot.Pos, shot.Vel, shot.Dir, shot.LifeSec);
		}

		// T5 divergence injection (debug). Press P to force a misprediction and
		// watch reconciliation snap + re-sim back; autofly fires one self-test.
		bool perturb = !Chat.Capturing && Input.IsPhysicalKeyPressed(Key.P);
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
		float budgetMs = PingMs + 2f * JitterMs;                  // RTT + ~2σ jitter
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
		if (_sentAt.Count > 256) _sentAt.Clear();
	}

	// Accumulate raw mouse motion only while the cursor is captured (consumed once per frame
	// in ReadInput; visible-cursor motion is ignored so menu interaction never steers), AND
	// drive the cursor capture/release transitions. Doing the MouseMode change here — in
	// response to the real Esc/click EVENT rather than polling in _Process — keeps the OS
	// cursor's hide/show in lockstep with the mode: on macOS a Captured set from _Process
	// leaves a ghost cursor pinned at screen center until the next motion event.
	//
	// Esc always RELEASES the cursor (one-way, so the OS cursor is reachable mid-flight); a
	// left click in the viewport recaptures it. Skipped under --autofly (headless has no real
	// cursor) and while Chat/SectorOverview own the cursor (they restore it on close).
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
			_mouseDelta += mm.Relative;

		if (_autoFly || !_hasShip || Chat.Capturing || SectorOverview.Active)
			return;

		if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true, Echo: false })
		{
			Input.MouseMode = Input.MouseModeEnum.Visible;
		}
		else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }
			&& Input.MouseMode != Input.MouseModeEnum.Captured)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
			_mouseDelta = Vector2.Zero;   // drop any motion from the recapture gesture
		}
	}

	// Release the cursor for the spawn menu (dead / not yet spawned). The flying-state
	// capture/release lives in _Input; this only handles the no-ship menu case each frame.
	private void HandleMouseCapture(bool flying)
	{
		if (_autoFly || Chat.Capturing || SectorOverview.Active) return;
		if (!flying && Input.MouseMode == Input.MouseModeEnum.Captured)
			Input.MouseMode = Input.MouseModeEnum.Visible;   // free cursor for the menu
	}

	private static float Axis(Key pos, Key neg)
	{
		float v = 0f;
		if (Input.IsPhysicalKeyPressed(pos)) v += 1f;
		if (Input.IsPhysicalKeyPressed(neg)) v -= 1f;
		return v;
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
			_stickYaw = Mathf.Clamp(_stickYaw - m.X * _mouseSens, -1f, 1f);
			_stickPitch = Mathf.Clamp(_stickPitch + (_mouseInvert ? -m.Y : m.Y) * _mouseSens, -1f, 1f);
			float ret = Mathf.Exp(-MouseReturnPerSec * (float)delta);
			_stickYaw *= ret;
			_stickPitch *= ret;
		}
		else
		{
			_stickYaw = 0f;   // cursor freed (menu/Esc): no residual steering
			_stickPitch = 0f;
		}

		return new ShipInputState
		{
			// Thrust is now a THROTTLE: W = full forward throttle (commands MaxSpeed),
			// S = weak reverse. Yaw/Pitch/Roll are commanded turn-RATE fractions.
			Thrust  = Axis(Key.W, Key.S),       // forward throttle / reverse
			StrafeX = Axis(Key.A, Key.D),       // strafe right / left
			StrafeY = Axis(Key.Z, Key.C),       // strafe up / down
			Yaw     = Mathf.Clamp(Axis(Key.Left, Key.Right) + _stickYaw, -1f, 1f),
			Pitch   = Mathf.Clamp(Axis(Key.Up, Key.Down) + _stickPitch, -1f, 1f),
			Roll    = Axis(Key.Q, Key.E),       // roll left / right
			Firing  = Input.IsPhysicalKeyPressed(Key.Space) || (look && Input.IsMouseButtonPressed(MouseButton.Left)),
			Coast   = Input.IsPhysicalKeyPressed(Key.X),   // vector lock: hold velocity
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

		float t = _stepsSinceSpawn * FlightModel.Dt;   // sim seconds
		return new ShipInputState
		{
			Thrust = 1f,
			Yaw = 0.4f * Mathf.Sin(t * 0.6f),          // weave, ~10 s period
			Pitch = 0.2f * Mathf.Sin(t * 0.37f),
			Firing = true,                             // exercise projectile spawn/cull
		};
	}
}
