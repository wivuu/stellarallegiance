namespace StellarAllegiance.Shared;

// Kinematic collision tuning shared by the server sim and the client prediction so both resolve a
// ship against the same world geometry identically (the whole point of sharing the collision code:
// the client must predict the exact push-out the server applies, or the ship penetrates until the
// server reconciles it out). Server World.cs aliases these; the client reads them directly.
//
// ONLY the kinematic constants live here. Collision *damage* (CollisionDamageScale,
// MaxCollisionDamage, ShipShipDamageScale) stays server-side — health is server-authoritative and
// the client must never predict it.
public static class CollisionConfig
{
    public const float ShipRadius = 3f; // every ship is this sphere vs static geometry (matches server)
    public const float BaseRadius = 90f; // base sphere-collision fallback radius (no hull)

    // Base meshes are authored with a +90° pitch error about their local X axis. Correct it uniformly
    // at EVERY consumer of the base GLB — the client's rendered hull (BaseModelLoader), the client's
    // prediction hull + the server sim's hull/docking (the shared GlbReader, via SimModel) — so the
    // visible superstructure, its convex collision shape, and its docking hardpoints all rotate as one
    // rigid body and stay aligned. This single angle is the source of truth: the Godot visual pitches
    // its node by it (Node3D.RotateX), and the collision reader seeds its GLB walk with the matching
    // quaternion below (rotating every vertex + hardpoint about the mesh origin).
    public const float BaseModelPitchRadians = 1.5707963268f; // +90° about +X

    // The pitch correction as a shared-math quaternion for the collision reader. Deterministic
    // (MathDet sin/cos) so the client-prediction and server-sim hulls stay bit-identical.
    public static Quat BaseModelRotation => Quat.FromRotationVector(new Vec3(BaseModelPitchRadians, 0f, 0f));
    // Docking-door depth window: the inward slack (along the face normal) of the bounded rectangular
    // docking FACE test (Collide.IntersectsDockFace). The lateral extent is now authored in the GLB
    // (the 4 boundary markers per door), so only this depth constant lives here. Window along the
    // face normal = [−DockFaceDepth, +ShipRadius] ⇒ depth+ShipRadius = 12 world units, ≥ the worst-
    // case single-tick travel (Scout 160 u/s at 20 Hz = 8) so a fast ship can't tunnel the thin face.
    public const float DockFaceDepth = 9f;
    public const float AsteroidCollisionScale = 0.95f; // fraction of a rock's visual radius that's solid
    // ponytail: a FIXED fraction means the inward slop scales with the rock — 0.82 left a ~13-unit-deep
    // soft shell on the biggest rocks (R~70), enough to fly a whole ship inside before bouncing. 0.95
    // keeps a small inward margin (hull is the convex envelope, ≥ the mesh, so this avoids bouncing on
    // air over concavities). If big-vs-small still feels uneven, switch to an absolute margin
    // (solidRadius = R - k) instead of a fraction.
    public const float CollisionRestitution = 0.6f; // bounce restitution on a closing contact
}
