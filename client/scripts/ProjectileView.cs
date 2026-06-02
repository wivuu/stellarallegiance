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

	public ulong ProjectileId { get; private set; }

	public void Initialize(Projectile row)
	{
		ProjectileId = row.ProjectileId;
		Sample(row);
	}

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
