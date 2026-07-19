using Godot;

// Shared client-side model/hardpoint geometry math. ShipModelLoader and BaseModelLoader stay
// deliberately independent parallel files for their own (placeholder geometry, FX) logic — see
// GlbLoader's header — but this one bit of orientation math is identical everywhere a local +Z
// "forward" needs to become a full orthonormal basis, so it lives here instead of three times.
public static class ModelGeom
{
    // Orthonormal basis whose local +Z points along `forward` (game-forward). Falls back to
    // identity for a near-zero direction, and swaps the up reference when forward is nearly
    // parallel to world up so the cross product stays well-conditioned.
    public static Basis BasisFacingZ(Vector3 forward)
    {
        if (forward.LengthSquared() < 1e-8f)
            return Basis.Identity;
        Vector3 z = forward.Normalized();
        Vector3 upRef = Mathf.Abs(z.Dot(Vector3.Up)) > 0.999f ? Vector3.Right : Vector3.Up;
        Vector3 x = upRef.Cross(z).Normalized();
        Vector3 y = z.Cross(x);
        return new Basis(x, y, z);
    }
}
