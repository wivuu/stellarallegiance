using Godot;

// Chase camera. Follows the local ship from behind (ships fly along local +Z,
// so "behind" is local -Z) and looks ahead. When there is no local ship, it
// parks at a wide overview of the sector.
public partial class CameraRig : Camera3D
{
	private static readonly Vector3 ChaseOffset = new Vector3(0f, 2.5f, -10f); // ship-local
	private static readonly Vector3 OverviewPos = new Vector3(600f, 750f, 1600f);

	// A Camera3D looks down its local -Z, but the ship flies along +Z, so rotate
	// the ship's basis 180° about its OWN up to aim the camera along the ship's
	// forward. Inheriting the ship's full orientation (its up included) is the
	// whole point: there is no world "up" in space, so referencing one (as LookAt
	// does) makes the view flip when you pitch toward vertical.
	private static readonly Basis FaceForward = new Basis(Vector3.Up, Mathf.Pi);

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
		// camera moves AND rotates at EXACTLY the ship's rate — no smoothing lag,
		// no world-up reference. CameraRig processes after the ship's node in tree
		// order, so this reads the transform the ship rendered this frame. Sharing
		// the ship's orientation means rolling/pitching to any attitude (including
		// straight up) keeps the controls and view consistent — true 6DOF.
		Transform3D t = ship.GlobalTransform;
		GlobalTransform = new Transform3D(t.Basis * FaceForward, t.Origin + t.Basis * ChaseOffset);
	}
}
