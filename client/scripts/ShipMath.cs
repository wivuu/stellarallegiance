using Godot;
using SpacetimeDB.Types;
using StellarAllegiance.Shared;

// Marshaling between the authoritative Ship row, the shared deterministic
// FlightModel types, and Godot's render types. Kept out of the synced
// FlightModel.cs so that file stays engine-independent.
public static class ShipMath
{
	public static ShipState StateFromRow(Ship r) => new ShipState
	{
		Pos = new Vec3(r.PosX, r.PosY, r.PosZ),
		Vel = new Vec3(r.VelX, r.VelY, r.VelZ),
		Rot = new Quat(r.RotX, r.RotY, r.RotZ, r.RotW),
		AngVel = new Vec3(r.AngVelX, r.AngVelY, r.AngVelZ),
		Mass = r.Mass,
		AbPower = r.AbPower,
	};

	public static Vector3 ToGodot(Vec3 v) => new Vector3(v.X, v.Y, v.Z);

	public static Quaternion ToGodot(Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);

	public static float Distance(Vec3 a, Vec3 b)
	{
		float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
		return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	// Angular difference between two unit quaternions, in radians.
	public static float AngleBetween(Quat a, Quat b)
	{
		float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
		dot = Mathf.Clamp(Mathf.Abs(dot), 0f, 1f);
		return 2f * Mathf.Acos(dot);
	}
}
