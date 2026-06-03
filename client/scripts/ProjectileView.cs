using Godot;
using SpacetimeDB.Types;

// A single projectile (T8). Projectiles are dumb, constant-velocity shots: their
// whole trajectory is determined by the spawn pos+vel, so we FIRE AND FORGET —
// extrapolate that one straight line for the projectile's life and never correct.
// We deliberately ignore the server's per-tick position updates: re-anchoring on
// each 20 Hz sample would multiply network jitter by projectile speed into a visible
// snap, and exact position doesn't matter — only that motion is smooth and that the
// server's authoritative hit (the row delete) does the damage. Freed on row delete.
public partial class ProjectileView : Node3D
{
	private Vector3 _pos;
	private Vector3 _vel;
	private double _t0;   // seconds, when _pos/_vel were sampled (spawn)
	private double _spawnTime;

	// Enemy-shot masking: enemy/remote shots arrive ~one-way latency after they were
	// actually fired, so the spawn sample would otherwise pop in at the (already stale)
	// muzzle. We render this many seconds AHEAD of the spawn line so the bolt appears
	// where it really is now. A one-time spawn offset, not an ongoing correction — it's
	// baked into the single extrapolation, so motion stays smooth. Zero for predicted
	// own shots (already prediction-correct) and on localhost (no measurable latency).
	private float _renderLeadSec;

	public ulong ProjectileId { get; private set; }

	// True until the authoritative Projectile row arrives and adopts this node
	// (client-side muzzle prediction). A ghost renders instantly on fire; if the
	// matching authoritative row never arrives (a mispredicted shot), it expires.
	public bool IsPredicted { get; private set; }

	public void Initialize(Projectile row, float renderLeadSec = 0f)
	{
		ProjectileId = row.ProjectileId;
		_renderLeadSec = renderLeadSec;
		_spawnTime = Time.GetTicksMsec() / 1000.0;
		_pos = new Vector3(row.PosX, row.PosY, row.PosZ);
		_vel = new Vector3(row.VelX, row.VelY, row.VelZ);
		_t0 = _spawnTime;
		Position = _pos + _vel * _renderLeadSec;   // honour the lead immediately (no 1-frame muzzle pop)
	}

	// Spawn an immediate ghost from a predicted muzzle pos/vel (no authoritative
	// row yet). It extrapolates the same straight line the real shot will follow.
	public void InitializePredicted(Vector3 pos, Vector3 vel)
	{
		IsPredicted = true;
		ProjectileId = 0;
		_spawnTime = Time.GetTicksMsec() / 1000.0;
		_pos = pos;
		_vel = vel;
		_t0 = _spawnTime;
		Position = pos;
	}

	// The authoritative row arrived: adopt its id and become a normal projectile. We
	// keep extrapolating the predicted line unchanged (fire-and-forget) — no re-anchor.
	public void AttachAuthoritative(ulong projectileId)
	{
		ProjectileId = projectileId;
		IsPredicted = false;
	}

	public bool GhostExpired(double now, double ttl) => IsPredicted && now - _spawnTime > ttl;

	public override void _Process(double delta)
	{
		float e = (float)(Time.GetTicksMsec() / 1000.0 - _t0);
		Position = _pos + _vel * (e + _renderLeadSec);
	}
}
