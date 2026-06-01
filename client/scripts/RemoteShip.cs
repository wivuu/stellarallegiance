using Godot;
using SpacetimeDB.Types;

// Other players' ships. For T4 (single player) this just snaps to the latest
// authoritative transform; smooth ~100 ms interpolation between buffered
// samples is added in T6 (RemoteShipInterpolator).
public partial class RemoteShip : Node3D
{
	public ulong ShipId { get; private set; }

	public void Initialize(Ship row)
	{
		ShipId = row.ShipId;
		Apply(row);
	}

	public void OnAuthoritative(Ship row) => Apply(row);

	private void Apply(Ship row)
	{
		Position = new Vector3(row.PosX, row.PosY, row.PosZ);
		Quaternion = new Quaternion(row.RotX, row.RotY, row.RotZ, row.RotW);
	}
}
