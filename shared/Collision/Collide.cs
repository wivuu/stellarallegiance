namespace StellarAllegiance.Shared;

// =====================================================================
//  Collide.cs — shared sphere-vs-static-geometry collision PRIMITIVES.
//
//  The server sim and the client prediction both resolve a ship (a ShipRadius sphere) against the
//  same asteroid/base hulls with these exact functions, so the client predicts the same push-out
//  the server applies and the ship never penetrates while waiting for reconciliation.
//
//  KINEMATIC ONLY. Bounce/ResolveStaticSphere mutate ShipState (velocity + position) and report
//  the inbound normal speed `vn` (<0 ⇒ closing). The SERVER turns that into collision damage;
//  the client ignores it (health is server-authoritative). This is the kinematic/damage split.
// =====================================================================
public static class Collide
{
    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    // Sphere(center=spherePos, radius) vs a convex hull placed at (center, rot, uniform scale). On
    // contact returns the WORLD outward normal (out of the hull toward the sphere) and the world
    // penetration depth. The kernel behind every hull bounce — asteroids and bases.
    public static bool SphereVsHull(
        Vec3 spherePos,
        float radius,
        ConvexHull hull,
        Vec3 center,
        Quat rot,
        float scale,
        out Vec3 worldNormal,
        out float worldPenetration
    )
    {
        worldNormal = default;
        worldPenetration = 0f;
        if (scale <= 1e-6f)
            return false;
        float inv = 1f / scale;
        Quat rotInv = rot.Conjugate();
        Vec3 localP = rotInv.Rotate(spherePos - center) * inv;
        float localR = radius * inv;
        if (!hull.ResolveSphere(localP, localR, out Vec3 localN, out float pen))
            return false;

        Vec3 n = rot.Rotate(localN); // rotation preserves length; uniform scale doesn't tilt normals
        float nl = n.Length();
        worldNormal = nl > 1e-6f ? n * (1f / nl) : new Vec3(0f, 1f, 0f);
        worldPenetration = pen * scale; // penetration is in local units → ×scale to world
        return true;
    }

    // Damp + reflect inbound velocity along a world contact normal and push out of penetration.
    // Reports `vn` (inbound normal speed) so the server can apply collision damage. Kinematic.
    public static void Bounce(ref ShipState s, Vec3 worldNormal, float worldPenetration, float restitution, out float vn)
    {
        vn = Dot(s.Vel, worldNormal);
        if (vn < 0f)
            s.Vel -= worldNormal * ((1f + restitution) * vn);
        s.Pos += worldNormal * worldPenetration;
    }

    // Sphere-vs-sphere static bounce (a rock without a hull, or a base fallback). Snaps the ship to
    // the contact surface and reflects inbound velocity. Reports `vn` for server-side damage.
    // Returns true on contact.
    public static bool ResolveStaticSphere(ref ShipState s, float shipRadius, Vec3 center, float radius, float restitution, out float vn)
    {
        vn = 0f;
        Vec3 d = s.Pos - center;
        float dist2 = d.LengthSquared();
        float minD = radius + shipRadius;
        if (dist2 >= minD * minD)
            return false;

        float dist = (float)System.Math.Sqrt(dist2);
        Vec3 n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
        vn = Dot(s.Vel, n);
        if (vn < 0f)
            s.Vel -= n * ((1f + restitution) * vn);
        s.Pos = center + n * minD;
        return true;
    }

    // Per-rock world rotation from the authored (RotX,RotY,RotZ). Godot Node3D.Rotation Euler is
    // YXZ order; the client applies it that way, so collision builds q = qY·qX·qZ to collide each
    // rock as it visually renders. Shared so the server and client produce identical rock poses.
    public static Quat RockRotation(float rx, float ry, float rz)
    {
        Quat qx = Quat.FromRotationVector(new Vec3(rx, 0f, 0f));
        Quat qy = Quat.FromRotationVector(new Vec3(0f, ry, 0f));
        Quat qz = Quat.FromRotationVector(new Vec3(0f, 0f, rz));
        return (qy * qx * qz).Normalized();
    }

    // Deterministic per-rock tumble (unit axis, speed rad/s) from the rock id. ONE shared source so
    // the rendered rock, the client's predicted hull, and the server's authoritative hull all spin as
    // one — integer splitmix64 hash + a uniform point on the sphere, so every peer agrees bit-for-bit.
    public static (Vec3 Axis, float Speed) RockSpin(ulong id)
    {
        ulong h = id * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        float u1 = (h & 0x1FFFFF) / (float)0x200000;
        float u2 = ((h >> 21) & 0x1FFFFF) / (float)0x200000;
        float u3 = ((h >> 42) & 0xFFFF) / (float)0x10000;
        float z = u1 * 2f - 1f;
        float phi = u2 * (float)(System.Math.PI * 2.0);
        float r = (float)System.Math.Sqrt(System.Math.Max(0f, 1f - z * z));
        var axis = new Vec3(r * (float)System.Math.Cos(phi), r * (float)System.Math.Sin(phi), z);
        float len = axis.Length();
        if (len < 1e-6f)
            return (new Vec3(0f, 1f, 0f), 0.03f + u3 * 0.12f);
        return (axis * (1f / len), 0.03f + u3 * 0.12f);
    }

    // World rotation of a tumbling rock at sim time t (seconds): spawn pose, then the tumble about its
    // fixed world axis. t = tick * FlightModel.Dt on every peer, so the phase stays shared.
    public static Quat RockRotationAt(Quat baseRot, Vec3 spinAxis, float spinSpeed, float t) =>
        spinSpeed <= 0f ? baseRot : Quat.FromRotationVector(spinAxis * (spinSpeed * t)) * baseRot;

    // A static collision body in world space: a transformed convex hull, or a sphere fallback when
    // Hull is null. The client builds a per-sector list of these (asteroids + bases) and resolves the
    // local ship against them each predicted tick, exactly as the server resolves its sim ships.
    public readonly struct StaticBody
    {
        public readonly ConvexHull? Hull;
        public readonly Vec3 Center;
        public readonly Quat Rot;
        public readonly float Scale;
        public readonly float SphereRadius; // used when Hull is null
        public readonly int BaseTeam; // -1 for an asteroid; the team id for a base (dock carve-out + ownership)
        public readonly (Vec3 Pos, Vec3 Normal)[]? DockDiscs; // own-base docking discs (base-local); null otherwise

        // Authored compound collision parts (one per baked COL_ node), in this body's SAME local frame
        // as `Hull` (bases: already world-scaled ⇒ identity rot / scale 1). null ⇒ single-hull
        // semantics via `Hull` — every asteroid/ship/un-baked base keeps exactly today's behaviour.
        // `Hull` remains the merged shrink-wrap (used for nothing kinematic once SubHulls is set, but
        // kept non-null so the existing `b.Hull != null` broadphase guards read the same).
        public readonly ConvexHull[]? SubHulls;

        private StaticBody(
            ConvexHull? hull,
            Vec3 center,
            Quat rot,
            float scale,
            float sphereRadius,
            int baseTeam,
            (Vec3, Vec3)[]? discs,
            ConvexHull[]? subHulls
        )
        {
            Hull = hull;
            Center = center;
            Rot = rot;
            Scale = scale;
            SphereRadius = sphereRadius;
            BaseTeam = baseTeam;
            DockDiscs = discs;
            SubHulls = subHulls;
        }

        public static StaticBody AsteroidHull(ConvexHull hull, Vec3 center, Quat rot, float scale) =>
            new(hull, center, rot, scale, 0f, -1, null, null);

        public static StaticBody AsteroidSphere(Vec3 center, float radius) =>
            new(null, center, Quat.Identity, 1f, radius, -1, null, null);

        // A base hull (already world-scaled, so identity rot + scale 1, local frame == world).
        public static StaticBody BaseHull(ConvexHull hull, Vec3 center, int team, (Vec3, Vec3)[] discs) =>
            new(hull, center, Quat.Identity, 1f, 0f, team, discs, null);

        // A COMPOUND base: `merged` is the shrink-wrap hull (broadphase/metrics parity with the
        // single-hull form); `subHulls` are the authored convex parts a ship actually bounces off,
        // in the same already-world-scaled local frame. The dock discs still gate on the merged
        // envelope. B3 migrates the base-insert callers onto this overload; the 4-arg one stays.
        public static StaticBody BaseHull(ConvexHull merged, ConvexHull[] subHulls, Vec3 center, int team, (Vec3, Vec3)[] entrances) =>
            new(merged, center, Quat.Identity, 1f, 0f, team, entrances, subHulls);

        public static StaticBody BaseSphere(Vec3 center, float radius, int team) =>
            new(null, center, Quat.Identity, 1f, radius, team, null, null);

        // A deployed recon probe: a plain solid sphere, team-agnostic (no ownership carve-out — you
        // bounce off your own probes too), matching the server's ResolveProbeCollisions footprint.
        public static StaticBody ProbeSphere(Vec3 center, float radius) =>
            new(null, center, Quat.Identity, 1f, radius, -1, null, null);
    }

    // Sphere vs a static body's SOLID: the single merged hull when SubHulls is null (byte-identical
    // to calling SphereVsHull directly — the regression-safe path every asteroid/ship/un-baked base
    // takes), else the DEEPEST-penetration contact across the authored sub-hulls. One deepest contact
    // = one bounce this tick; any residual overlap with a shallower part resolves next tick, exactly
    // as the single-hull resolver has always relied on. Reuses SphereVsHull per sub-hull so the
    // local-frame mapping (center/rot/scale) is identical for bases (identity/1) and would carry over
    // to any rotated compound body too — no duplicated sphere-vs-hull math.
    public static bool SphereVsBody(Vec3 pos, float radius, in StaticBody b, out Vec3 normal, out float penetration)
    {
        if (b.SubHulls is null)
            return SphereVsHull(pos, radius, b.Hull!, b.Center, b.Rot, b.Scale, out normal, out penetration);

        normal = default;
        penetration = 0f;

        // Broad-phase for the compound scan: the merged Hull's BoundingRadius encloses every
        // sub-hull (parts are clamped strictly inside it), so a sphere beyond that reach can't
        // touch any part — skip the (hundreds-of-planes) sub-hull loop entirely. The 4× radius
        // slack covers ResolveSphere's approximate corner contacts (it can report a graze up to
        // ~a radius outside the true surface near a corner), so gated and ungated results agree.
        // Lives in the SHARED kernel so server and client prediction gate identically (parity).
        Vec3 reachD = pos - b.Center;
        float reach = b.Hull!.BoundingRadius * b.Scale + radius * 4f;
        if (reachD.LengthSquared() > reach * reach)
            return false;

        bool any = false;
        for (int i = 0; i < b.SubHulls.Length; i++)
        {
            if (SphereVsHull(pos, radius, b.SubHulls[i], b.Center, b.Rot, b.Scale, out Vec3 n, out float pen) && (!any || pen > penetration))
            {
                normal = n;
                penetration = pen;
                any = true;
            }
        }
        return any;
    }

    // Resolve a ship (a shipRadius sphere) against a sector's static bodies, mutating its state the
    // same way the server does: bounce off every asteroid/base hull, EXCEPT skip the bounce at the
    // ship's OWN base docking discs (so prediction doesn't fight the server's docking). Returns true
    // if any contact occurred and reports the last contact position (for the collision thud).
    public static bool ResolveStatics(
        ref ShipState s,
        float shipRadius,
        System.Collections.Generic.IReadOnlyList<StaticBody> bodies,
        int localTeam,
        float restitution,
        float dockDiscRadius,
        out Vec3 hitPos
    )
    {
        bool hit = false;
        hitPos = default;
        for (int i = 0; i < bodies.Count; i++)
        {
            StaticBody b = bodies[i];
            bool ownBase = b.BaseTeam >= 0 && b.BaseTeam == localTeam;
            if (ownBase && b.DockDiscs != null && IntersectsDockDisc(s.Pos - b.Center, b.DockDiscs, dockDiscRadius, shipRadius))
                continue; // your dock opening — let the ship through (server handles docking)
            if (b.Hull != null)
            {
                if (SphereVsBody(s.Pos, shipRadius, b, out Vec3 n, out float pen))
                {
                    Bounce(ref s, n, pen, restitution, out _);
                    hit = true;
                    hitPos = s.Pos;
                }
            }
            else if (ResolveStaticSphere(ref s, shipRadius, b.Center, b.SphereRadius, restitution, out _))
            {
                hit = true;
                hitPos = s.Pos;
            }
        }
        return hit;
    }

    // Non-mutating overlap test: does a ship sphere at `pos` touch any static body (skipping its own
    // base's dock discs)? Used for the collision THUD — same geometry as ResolveStatics, no bounce.
    public static bool Touches(
        Vec3 pos,
        float shipRadius,
        System.Collections.Generic.IReadOnlyList<StaticBody> bodies,
        int localTeam,
        float dockDiscRadius
    )
    {
        for (int i = 0; i < bodies.Count; i++)
        {
            StaticBody b = bodies[i];
            bool ownBase = b.BaseTeam >= 0 && b.BaseTeam == localTeam;
            if (ownBase && b.DockDiscs != null && IntersectsDockDisc(pos - b.Center, b.DockDiscs, dockDiscRadius, shipRadius))
                continue;
            if (b.Hull != null)
            {
                if (SphereVsBody(pos, shipRadius, b, out _, out _))
                    return true;
            }
            else
            {
                float m = b.SphereRadius + shipRadius;
                if ((pos - b.Center).LengthSquared() < m * m)
                    return true;
            }
        }
        return false;
    }

    // True when a ship sphere intersects one of a base's docking-cone base discs: the ship center is
    // at/just inside the disc plane and within the disc radius laterally. `d` is the ship position
    // relative to the base center (disc Pos/Normal are in that same base-local frame). The inward
    // slack (−discRadius) keeps a fast ship from tunneling the thin disc plane in one tick; lateral
    // uses discRadius+shipRadius so the ship's hull (not just its center) must reach the disc. This
    // is the ONLY way to dock at a hull base — everything else is the solid shell.
    public static bool IntersectsDockDisc(Vec3 d, (Vec3 Pos, Vec3 Normal)[] discs, float discRadius, float shipRadius)
    {
        float r = discRadius + shipRadius;
        for (int i = 0; i < discs.Length; i++)
        {
            Vec3 rel = d - discs[i].Pos;
            float along = Dot(rel, discs[i].Normal);
            if (along > shipRadius || along < -discRadius)
                continue;
            Vec3 lateral = rel - discs[i].Normal * along;
            if (lateral.LengthSquared() <= r * r)
                return true;
        }
        return false;
    }
}
