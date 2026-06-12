using Godot;

// A single visual projectile bolt. Bolts are dumb, constant-velocity shots: their whole
// trajectory is determined by the spawn pos+vel, so we FIRE AND FORGET — extrapolate that
// one straight line for the bolt's life and never correct.
//
// There is no authoritative Projectile row anymore: the server resolves hits analytically
// at fire time, and every client SYNTHESIZES the bolt locally — the local ship from its
// own fire prediction (PredictionController.Step), remote ships from their row's
// LastFireTick advancing (WorldRenderer.SpawnBoltFor, which mirrors the server's muzzle +
// deterministic-spread math). A shot costs zero replication.
//
// Lifetime: the bolt self-expires after its TTL — the weapon's flight life, clipped at
// spawn to the first static obstruction (asteroid/base) along its line so it visually
// stops at rocks the way the old server row-delete used to read. WorldRenderer's
// hit-spark pass may consume it earlier when it visually meets a (moving) ship.
public partial class ProjectileView : Node3D
{
	private Vector3 _pos;
	private Vector3 _vel;
	private double _t0;       // seconds, when _pos/_vel were sampled (spawn)
	private float _ttlSec;    // flight life (already obstruction-clipped by the spawner)

	// Enemy-shot masking: remote shots are observed ~one-way latency after they were
	// actually fired, so the spawn sample would otherwise pop in at the (already stale)
	// muzzle. We render this many seconds AHEAD of the spawn line so the bolt appears
	// where it really is now. A one-time spawn offset, not an ongoing correction — it's
	// baked into the single extrapolation, so motion stays smooth. Zero for the local
	// ship's own predicted shots and on localhost (no measurable latency).
	private float _renderLeadSec;

	// Constant flight velocity (world u/s) and spawn point, read by WorldRenderer's
	// client-side hit-spark pass. It sweeps the bolt across each frame (so a fast shot
	// can't tunnel through a ship) and ignores ships until the bolt has cleared its
	// muzzle, so a shot never sparks on the ship that fired it. Team is deliberately not
	// tracked — friendly fire sparks like any other.
	public Vector3 Velocity => _vel;
	public Vector3 SpawnPos { get; private set; }

	public void Initialize(Vector3 pos, Vector3 vel, float ttlSec, float renderLeadSec = 0f)
	{
		_pos = pos;
		_vel = vel;
		_ttlSec = ttlSec;
		_renderLeadSec = renderLeadSec;
		SpawnPos = pos;
		_t0 = Time.GetTicksMsec() / 1000.0;
		OrientAlongVelocity();
		Position = pos + vel * renderLeadSec;   // honour the lead immediately (no 1-frame muzzle pop)
	}

	// The render lead counts against the TTL: a remote bolt drawn `lead` ahead was fired
	// that much earlier, so it also expires that much sooner on our screen.
	public bool Expired =>
		Time.GetTicksMsec() / 1000.0 - _t0 + _renderLeadSec >= _ttlSec;

	// Aim the bolt's local +Z down its velocity so the cylinder tracer points where it
	// flies. Velocity is constant (fire-and-forget), so this is set once, never per frame.
	private void OrientAlongVelocity()
	{
		if (_vel.LengthSquared() > 1e-6f)
			Quaternion = new Quaternion(Vector3.Back, _vel.Normalized());
	}

	public override void _Process(double delta)
	{
		float e = (float)(Time.GetTicksMsec() / 1000.0 - _t0);
		Position = _pos + _vel * (e + _renderLeadSec);
	}
}
