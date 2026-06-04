using Godot;
using SpacetimeDB.Types;
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

	private double _acc;
	private uint _predTick;             // prediction tick, in SERVER-tick space
	private bool _hadShip;
	private int _stepsSinceSpawn;
	private ShipInputState _input;
	private ShipClass? _spawnRequest;   // class chosen via HUD menu / 1-2 keys; cleared once flying
	private bool _spawnPending;
	private double _spawnRetry;
	private bool _perturbHeld;          // edge-detect the P debug key

	// Mouse-look aiming (Allegiance style). The flight model already integrates yaw/
	// pitch as -1..1 rate inputs, so mouse-look is purely an input-sampling change: we
	// accumulate captured-cursor motion in _Input, then in ReadInput scale the per-frame
	// pixel delta by sensitivity and clamp to -1..1, feeding the existing axis path. The
	// cursor is captured while flying (Esc releases, click recaptures); arrow keys still
	// work as a fallback and sum with the mouse. STDB_MOUSE_SENS tunes feel (px→rate),
	// STDB_MOUSE_INVERT=1 flips pitch.
	private const float DefaultMouseSens = 0.08f;
	private float _mouseSens = DefaultMouseSens;
	private bool _mouseInvert;
	private Vector2 _mouseDelta;        // captured-cursor motion accumulated since last sample
	private bool _escHeld;              // edge-detect Escape (capture toggle)
	private bool _clickHeld;            // edge-detect left click (recapture)

	// Headless verification: `--autofly` auto-spawns a Scout and flies a fixed
	// input so the full ApplyInput -> SimTick -> reconcile loop can be checked
	// without a human at the keyboard.
	private bool _autoFly;
	private bool _autoJoined;            // autofly QuickJoins (team + ready) once on connect
	private bool _selfTestDone;         // autofly fires one divergence injection
	private bool _combatTest;           // --combat-test: fly straight + fire (head-on damage check)

	// Round-trip latency, measured by timing each ApplyInput against its own reducer
	// callback (clientTick is echoed back). This is the true client→server→client
	// round trip (network + reducer run), independent of the prediction clock. The
	// HUD reads PingMs/JitterMs. Only sampled while flying (that's when we send input).
	private readonly System.Collections.Generic.Dictionary<uint, double> _sentAt = new();
	public float PingMs { get; private set; }
	public float JitterMs { get; private set; }

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

		// Time ApplyInput round-trips for the latency readout. The reducer callback
		// fires on the caller when the server commits our call, echoing clientTick.
		_cm.Connected += conn => conn.Reducers.OnApplyInput +=
			(_, _, _, _, _, _, _, _, clientTick) => OnInputAck(clientTick);

		var autoClass = ShipClass.Scout;
		foreach (var a in OS.GetCmdlineArgs())
		{
			if (a == "--autofly") _autoFly = true;
			if (a == "--fighter") autoClass = ShipClass.Fighter;   // autofly picks Fighter (dev verify)
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
		_input = _autoFly ? AutoInput() : ReadInput();

		// Spawn handling. The class comes from the HUD spawn menu (RequestSpawn) or
		// the 1/2 keyboard shortcuts (handy alongside the menu). We only call the
		// reducer once the connection is live (LocalIdentity is set on connect),
		// retry after a short delay so an early/lost request recovers, and clear the
		// request once the ship actually exists.
		bool connected = _cm.LocalIdentity is not null && _cm.Conn is not null;
		bool hasShip = _world.LocalShip != null;

		// Headless autofly: the lobby now gates spawning, so QuickJoin once on connect
		// (smallest side + ready) to drive the match to Active before requesting a ship.
		// Two --combat-test clients split onto opposing sides server-side.
		if (_autoFly && connected && !_autoJoined)
		{
			_cm.Conn!.Reducers.QuickJoin();
			_autoJoined = true;
		}

		HandleMouseCapture(hasShip);

		if (!hasShip)
		{
			if (Input.IsPhysicalKeyPressed(Key.Key1)) _spawnRequest = ShipClass.Scout;
			if (Input.IsPhysicalKeyPressed(Key.Key2)) _spawnRequest = ShipClass.Fighter;
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
			_cm.Conn!.Reducers.SpawnShip(cls);
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
		}

		UpdateAdaptiveLead();
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
			_cm.Conn?.Reducers.ApplyInput(
				_input.Thrust, _input.StrafeX, _input.StrafeY,
				_input.Yaw, _input.Pitch, _input.Roll,
				_input.Firing, _predTick);
			_sentAt[_predTick] = Time.GetTicksMsec();
			if (pc.Step(_input, _predTick) is PredictionController.PredictedShot shot)
				_world.SpawnPredictedProjectile(pc.Team, shot.Pos, shot.Vel);
		}

		// T5 divergence injection (debug). Press P to force a misprediction and
		// watch reconciliation snap + re-sim back; autofly fires one self-test.
		bool perturb = Input.IsPhysicalKeyPressed(Key.P);
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

	// An ApplyInput we sent has been committed by the server: the elapsed wall time
	// is the round-trip latency. Smooth it (EWMA) and track jitter for the HUD.
	private void OnInputAck(uint clientTick)
	{
		if (!_sentAt.Remove(clientTick, out var sent))
			return;
		float rtt = (float)(Time.GetTicksMsec() - sent);
		float dev = Mathf.Abs(rtt - PingMs);
		PingMs = PingMs <= 0f ? rtt : PingMs * 0.9f + rtt * 0.1f;
		JitterMs = JitterMs <= 0f ? dev : JitterMs * 0.9f + dev * 0.1f;
		// Drop stale unacked sends so the map can't grow unbounded.
		if (_sentAt.Count > 256) _sentAt.Clear();
	}

	// Accumulate raw mouse motion only while the cursor is captured; consumed (and
	// reset) once per frame in ReadInput. Visible-cursor motion is ignored so menu
	// interaction never steers the ship.
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
			_mouseDelta += mm.Relative;
	}

	// Capture the cursor for mouse-look while flying; release it for the spawn menu.
	// Esc toggles release (so the OS cursor is reachable mid-flight); a click (or Esc)
	// recaptures. Edge-detected so a held key/button doesn't thrash the mode. Skipped
	// under --autofly (headless has no real cursor and must not grab focus).
	private void HandleMouseCapture(bool flying)
	{
		if (_autoFly) return;

		bool esc = Input.IsPhysicalKeyPressed(Key.Escape);
		bool escPressed = esc && !_escHeld;
		_escHeld = esc;

		bool click = Input.IsMouseButtonPressed(MouseButton.Left);
		bool clickPressed = click && !_clickHeld;
		_clickHeld = click;

		bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
		if (!flying)
		{
			if (captured) Input.MouseMode = Input.MouseModeEnum.Visible;   // free cursor for the menu
		}
		else if (captured)
		{
			if (escPressed) Input.MouseMode = Input.MouseModeEnum.Visible;
		}
		else if (clickPressed || escPressed)
		{
			Input.MouseMode = Input.MouseModeEnum.Captured;
			_mouseDelta = Vector2.Zero;   // drop any motion from the recapture gesture
		}
	}

	private static float Axis(Key pos, Key neg)
	{
		float v = 0f;
		if (Input.IsPhysicalKeyPressed(pos)) v += 1f;
		if (Input.IsPhysicalKeyPressed(neg)) v -= 1f;
		return v;
	}

	private ShipInputState ReadInput()
	{
		// Consume the frame's captured-cursor motion. Mouse-right turns right (matches
		// the Right arrow → -Yaw convention); mouse-up pitches like the Up arrow unless
		// inverted. Pixel delta × sensitivity, clamped to the -1..1 rate axis, then
		// summed with the arrow keys (which still work as a fallback).
		bool look = Input.MouseMode == Input.MouseModeEnum.Captured;
		Vector2 m = _mouseDelta;
		_mouseDelta = Vector2.Zero;
		float mouseYaw = look ? -m.X * _mouseSens : 0f;
		float mousePitch = look ? (_mouseInvert ? m.Y : -m.Y) * _mouseSens : 0f;

		return new ShipInputState
		{
			Thrust  = Axis(Key.W, Key.S),       // forward / reverse
			StrafeX = Axis(Key.A, Key.D),       // strafe right / left
			StrafeY = Axis(Key.E, Key.C),       // strafe up / down
			Yaw     = Mathf.Clamp(Axis(Key.Left, Key.Right) + mouseYaw, -1f, 1f),
			Pitch   = Mathf.Clamp(Axis(Key.Up, Key.Down) + mousePitch, -1f, 1f),
			Roll    = Axis(Key.Q, Key.Z),       // roll left / right (moved off E)
			Firing  = Input.IsPhysicalKeyPressed(Key.Space) || (look && Input.IsMouseButtonPressed(MouseButton.Left)),
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
