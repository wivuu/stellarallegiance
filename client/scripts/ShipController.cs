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

	// Headless verification: `--autofly` auto-spawns a Scout and flies a fixed
	// input so the full ApplyInput -> SimTick -> reconcile loop can be checked
	// without a human at the keyboard.
	private bool _autoFly;
	private bool _selfTestDone;         // autofly fires one divergence injection
	private bool _combatTest;           // --combat-test: fly straight + fire (head-on damage check)

	public override void _Ready()
	{
		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_world = GetNode<WorldRenderer>("../WorldRenderer");

		if (int.TryParse(OS.GetEnvironment("STDB_LEAD"), out var lead))
			_targetLead = Mathf.Clamp(lead, 1, 15);

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
			pc.Step(_input, _predTick);
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

	private static float Axis(Key pos, Key neg)
	{
		float v = 0f;
		if (Input.IsPhysicalKeyPressed(pos)) v += 1f;
		if (Input.IsPhysicalKeyPressed(neg)) v -= 1f;
		return v;
	}

	private static ShipInputState ReadInput() => new ShipInputState
	{
		Thrust  = Axis(Key.W, Key.S),       // forward / reverse
		StrafeX = Axis(Key.A, Key.D),       // strafe right / left
		StrafeY = Axis(Key.E, Key.C),       // strafe up / down
		Yaw     = Axis(Key.Left, Key.Right),
		Pitch   = Axis(Key.Down, Key.Up),
		Roll    = Axis(Key.Q, Key.Z),       // roll left / right (moved off E)
		Firing  = Input.IsPhysicalKeyPressed(Key.Space) || Input.IsMouseButtonPressed(MouseButton.Left),
	};

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
