using Godot;
using SpacetimeDB.Types;

// A single projectile (T8). Projectiles are dumb, straight-line shots, so the
// smoothest accurate render is to EXTRAPOLATE from the last authoritative sample
// along its known velocity (rather than lerping toward stepped 20 Hz positions).
// Each authoritative update re-anchors pos+vel+time; the tiny correction between
// 20 Hz samples is invisible at projectile speed. Freed on row delete.
public partial class ProjectileView : Node3D
{
	private Vector3 _pos;
	private Vector3 _vel;
	private double _t0;   // seconds, when _pos/_vel were sampled
	private double _spawnTime;

	public ulong ProjectileId { get; private set; }

	// True until the authoritative Projectile row arrives and adopts this node
	// (client-side muzzle prediction). A ghost renders instantly on fire; if the
	// matching authoritative row never arrives (a mispredicted shot), it expires.
	public bool IsPredicted { get; private set; }

	public void Initialize(Projectile row)
	{
		ProjectileId = row.ProjectileId;
		_spawnTime = Time.GetTicksMsec() / 1000.0;
		Sample(row);
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

	// The authoritative row arrived: adopt its id and become a normal projectile.
	// We keep extrapolating the current line (no snap); the next OnAuthoritative
	// re-anchors it — and since both follow the same straight line, that's ~zero.
	public void AttachAuthoritative(ulong projectileId)
	{
		ProjectileId = projectileId;
		IsPredicted = false;
	}

	public bool GhostExpired(double now, double ttl) => IsPredicted && now - _spawnTime > ttl;

	public void OnAuthoritative(Projectile row) => Sample(row);

	private void Sample(Projectile row)
	{
		_pos = new Vector3(row.PosX, row.PosY, row.PosZ);
		_vel = new Vector3(row.VelX, row.VelY, row.VelZ);
		_t0 = Time.GetTicksMsec() / 1000.0;
		Position = _pos;
	}

	public override void _Process(double delta)
	{
		float e = (float)(Time.GetTicksMsec() / 1000.0 - _t0);
		Position = _pos + _vel * e;
	}
}
