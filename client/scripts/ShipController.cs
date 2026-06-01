using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

// Reads local input, runs the fixed-rate (20 Hz) input/prediction loop, and
// calls the ApplyInput reducer. Also handles the (temporary) spawn key for T4.
// Render frames are decoupled: input is sampled each frame but only applied +
// sent + predicted on the fixed sim cadence so it matches the server clock.
public partial class ShipController : Node
{
	private ConnectionManager _cm = null!;
	private WorldRenderer _world = null!;

	private double _acc;
	private uint _clientTick;
	private ShipInputState _input;
	private bool _spawnPending;
	private double _spawnRetry;

	// Headless verification: `--autofly` auto-spawns a Scout and flies a fixed
	// input so the full ApplyInput -> SimTick -> reconcile loop can be checked
	// without a human at the keyboard.
	private bool _autoFly;

	public override void _Ready()
	{
		_cm = GetNode<ConnectionManager>("../ConnectionManager");
		_world = GetNode<WorldRenderer>("../WorldRenderer");
		foreach (var a in OS.GetCmdlineArgs())
			if (a == "--autofly") _autoFly = true;
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

		// Fixed-rate input + prediction.
		_acc += delta;
		while (_acc >= FlightModel.Dt)
		{
			_acc -= FlightModel.Dt;
			var pc = _world.LocalShip;
			if (pc == null) continue;

			_clientTick++;
			_cm.Conn?.Reducers.ApplyInput(
				_input.Thrust, _input.StrafeX, _input.StrafeY,
				_input.Yaw, _input.Pitch, _input.Roll,
				_input.Firing, _clientTick);
			pc.Step(_input, _clientTick);
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

	// Deterministic-ish scripted flight for headless verification: full forward
	// thrust with a gentle climbing turn so position clearly changes.
	private ShipInputState AutoInput() => new ShipInputState
	{
		Thrust = 1f,
		Yaw = 0.3f,
		Pitch = 0.05f,
		Firing = false,
	};
}
