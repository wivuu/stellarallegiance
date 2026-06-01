using Godot;

// Chase camera. Follows the local ship from behind (ships fly along local +Z,
// so "behind" is local -Z) and looks ahead. When there is no local ship, it
// parks at a wide overview of the sector.
public partial class CameraRig : Camera3D
{
	private static readonly Vector3 ChaseOffset = new Vector3(0f, 2.5f, -10f); // ship-local
	private static readonly Vector3 OverviewPos = new Vector3(600f, 750f, 1600f);

	private WorldRenderer _world = null!;

	public override void _Ready()
	{
		_world = GetNode<WorldRenderer>("../WorldRenderer");
		Far = 6000f;
	}

	public override void _Process(double delta)
	{
		var ship = _world.LocalShip;
		if (ship == null)
		{
			GlobalPosition = OverviewPos;
			LookAt(Vector3.Zero, Vector3.Up);
			return;
		}

		// Rigidly attach to the ship's (smoothly interpolated) transform so the
		// camera moves at EXACTLY the ship's rate — no smoothing lag or beat.
		// CameraRig processes after the ship's node in tree order, so this reads
		// the transform the ship rendered this frame.
		Transform3D t = ship.GlobalTransform;
		GlobalPosition = t.Origin + t.Basis * ChaseOffset;
		Vector3 forward = t.Basis * new Vector3(0f, 0f, 1f); // ship's nose (+Z)
		LookAt(t.Origin + forward * 12f, Vector3.Up);
	}
}
