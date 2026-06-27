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
    public const float DockDiscRadius = 9f; // docking cone base-disc radius (own base carve-out)
    public const float AsteroidCollisionScale = 0.95f; // fraction of a rock's visual radius that's solid
    // ponytail: a FIXED fraction means the inward slop scales with the rock — 0.82 left a ~13-unit-deep
    // soft shell on the biggest rocks (R~70), enough to fly a whole ship inside before bouncing. 0.95
    // keeps a small inward margin (hull is the convex envelope, ≥ the mesh, so this avoids bouncing on
    // air over concavities). If big-vs-small still feels uneven, switch to an absolute margin
    // (solidRadius = R - k) instead of a fraction.
    public const float CollisionRestitution = 0.3f; // bounce restitution on a closing contact
}
