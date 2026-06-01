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
	private const int TargetLead = 2;         // ticks to stay ahead of authority
	private const float SlewGain = 0.08f;     // how hard the local clock tracks the server
	private const float MaxSlew = 0.30f;      // cap the clock rate adjustment (±30%)

	private ConnectionManager _cm = null!;
	private WorldRenderer _world = null!;

	private double _acc;
	private uint _predTick;             // prediction tick, in SERVER-tick space
	private bool _hadShip;
	private int _stepsSinceSpawn;
	private ShipInputState _input;
	private bool _spawnPending;
	private double _spawnRetry;
	private bool _perturbHeld;          // edge-detect the P debug key

	// Headless verification: `--autofly` auto-spawns a Scout and flies a fixed
	// input so the full ApplyInput -> SimTick -> reconcile loop can be checked
	// without a human at the keyboard.
	private bool _autoFly;
	private bool _selfTestDone;         // autofly fires one divergence injection

	public override void _Ready()
	{
		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_world = GetNode<WorldRenderer>("../WorldRenderer");
		foreach (var a in OS.GetCmdlineArgs())
			if (a == "--autofly") _autoFly = true;
		// Headless runs are otherwise uncapped: _Process spins as fast as possible,
		// flooding ApplyInput and racing the prediction far ahead of the 20 Hz
		// server, which inflates the prediction lead. Cap to a realistic display
		// rate so the autofly's reconcile behaviour matches a real client.
		if (_autoFly) Engine.MaxFps = 60;
	}

	public override void _Process(double delta)
	{
		_input = _autoFly ? AutoInput() : ReadInput();

		// Spawn handling. Only attempt once the connection is live (LocalIdentity
		// is set on connect); retry after a short delay so an early/lost request
		// recovers, and clear the pending flag once the ship actually exists.
		bool connected = _cm.LocalIdentity is not null && _cm.Conn is not null;
		bool hasShip = _world.LocalShip != null;
		if (_spawnPending)
		{
			_spawnRetry -= delta;
			if (_spawnRetry <= 0) _spawnPending = false;
		}
		if (hasShip)
		{
			_spawnPending = false;
		}
		else if (connected && !_spawnPending && (_autoFly || Input.IsPhysicalKeyPressed(Key.Key1)))
		{
			_cm.Conn!.Reducers.SpawnShip(ShipClass.Scout);
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
		float slew = Mathf.Clamp((TargetLead - lead) * SlewGain, -MaxSlew, MaxSlew);
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

		if (_autoFly && !_selfTestDone && _stepsSinceSpawn >= 100)
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
		Thrust  = Axis(Key.W, Key.S),
		StrafeX = Axis(Key.D, Key.A),
		StrafeY = Axis(Key.Space, Key.Shift),
		Yaw     = Axis(Key.Left, Key.Right),
		Pitch   = Axis(Key.Up, Key.Down),
		Roll    = Axis(Key.Q, Key.E),
		Firing  = false, // weapons arrive in T8
	};

	// Deterministic scripted flight for headless verification — representative of
	// NORMAL play: long straight runs with occasional gentle turns and coasting,
	// rather than a pinned max-rate turn (which is an adversarial worst case for
	// the prediction lead). Driven by steps-since-spawn so it's reproducible.
	private ShipInputState AutoInput()
	{
		// TEMP(determinism check): sustained hard turn — worst case for rotation drift.
		return new ShipInputState { Thrust = 1f, Yaw = 0.6f, Pitch = 0.15f };
		uint phase = ((uint)_stepsSinceSpawn / 80) % 6;  // ~4 s per phase
		return phase switch
		{
			0 => new ShipInputState { Thrust = 1f },                 // straight accel
			1 => new ShipInputState { Thrust = 1f, Yaw = 0.25f },    // gentle turn
			2 => new ShipInputState { Thrust = 0f },                 // coast
			3 => new ShipInputState { Thrust = 1f, Yaw = -0.25f },   // gentle turn back
			4 => new ShipInputState { Thrust = 1f, Pitch = 0.2f },   // climb
			_ => new ShipInputState { Thrust = 1f },                 // straight
		};
	}
}
