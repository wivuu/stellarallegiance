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
    public static bool ResolveStaticSphere(
        ref ShipState s,
        float shipRadius,
        Vec3 center,
        float radius,
        float restitution,
        out float vn
    )
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
        public readonly DockFace[]? DockFaces; // own-base rectangular docking doors (base-local); null otherwise

        // Station-class launch/dock restriction inputs (2026-07-21): the base's StationClassId
        // byte (DockRules.UnknownStationClass when not a catalogued base) and its precomputed
        // largest-door index (-1 = none). Non-base bodies keep the defaults; a mask-0 ship reads
        // neither.
        public readonly byte StationClass;
        public readonly int LargestDockFace;

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
            DockFace[]? dockFaces,
            ConvexHull[]? subHulls,
            byte stationClass = DockRules.UnknownStationClass,
            int largestDockFace = -1
        )
        {
            Hull = hull;
            Center = center;
            Rot = rot;
            Scale = scale;
            SphereRadius = sphereRadius;
            BaseTeam = baseTeam;
            DockFaces = dockFaces;
            SubHulls = subHulls;
            StationClass = stationClass;
            LargestDockFace = largestDockFace;
        }

        public static StaticBody AsteroidHull(ConvexHull hull, Vec3 center, Quat rot, float scale) =>
            new(hull, center, rot, scale, 0f, -1, null, null);

        public static StaticBody AsteroidSphere(Vec3 center, float radius) =>
            new(null, center, Quat.Identity, 1f, radius, -1, null, null);

        // A base hull (already world-scaled, so identity rot + scale 1, local frame == world).
        public static StaticBody BaseHull(
            ConvexHull hull,
            Vec3 center,
            int team,
            DockFace[] faces,
            byte stationClass,
            int largestDockFace
        ) => new(hull, center, Quat.Identity, 1f, 0f, team, faces, null, stationClass, largestDockFace);

        // A COMPOUND base: `merged` is the shrink-wrap hull (broadphase/metrics parity with the
        // single-hull form); `subHulls` are the authored convex parts a ship actually bounces off,
        // in the same already-world-scaled local frame. The dock faces still gate on the merged
        // envelope. B3 migrates the base-insert callers onto this overload; the 6-arg one stays.
        public static StaticBody BaseHull(
            ConvexHull merged,
            ConvexHull[] subHulls,
            Vec3 center,
            int team,
            DockFace[] faces,
            byte stationClass,
            int largestDockFace
        ) => new(merged, center, Quat.Identity, 1f, 0f, team, faces, subHulls, stationClass, largestDockFace);

        public static StaticBody BaseSphere(Vec3 center, float radius, int team) =>
            new(null, center, Quat.Identity, 1f, radius, team, null, null);

        // A deployed recon probe: a plain solid sphere, team-agnostic (no ownership carve-out — you
        // bounce off your own probes too), matching the server's ResolveProbeCollisions footprint.
        public static StaticBody ProbeSphere(Vec3 center, float radius) =>
            new(null, center, Quat.Identity, 1f, radius, -1, null, null);

        // A constructor's growing base-construction shell: a team-agnostic solid sphere (any ship bounces
        // off it) matching the server's ResolveBuildSphereCollisions barrier, so the local ship predicts
        // the same bounce the server enforces instead of sinking into the shell and snapping back.
        public static StaticBody BuildSphere(Vec3 center, float radius) =>
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
            if (
                SphereVsHull(pos, radius, b.SubHulls[i], b.Center, b.Rot, b.Scale, out Vec3 n, out float pen)
                && (!any || pen > penetration)
            )
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
    // `launchClassMask` is the ship hull's ShipClassDef.LaunchClassMask (0 = unrestricted): a
    // restricted hull gets NO dock carve-out at own bases of a disallowed station class, and at an
    // allowed base only its largest door opens (DockRules) — matching the server's dock pass.
    public static bool ResolveStatics(
        ref ShipState s,
        float shipRadius,
        System.Collections.Generic.IReadOnlyList<StaticBody> bodies,
        int localTeam,
        ushort launchClassMask,
        float restitution,
        float dockFaceDepth,
        out Vec3 hitPos
    )
    {
        bool hit = false;
        hitPos = default;
        for (int i = 0; i < bodies.Count; i++)
        {
            StaticBody b = bodies[i];
            bool ownBase = b.BaseTeam >= 0 && b.BaseTeam == localTeam;
            if (
                ownBase
                && b.DockFaces != null
                && DockRules.ClassAllowed(launchClassMask, b.StationClass)
                && IntersectsDockFace(
                    s.Pos - b.Center,
                    s.Vel,
                    b.DockFaces,
                    dockFaceDepth,
                    shipRadius,
                    DockRules.AllowedFace(launchClassMask, b.LargestDockFace)
                )
            )
                continue; // closing on your dock face inside the approach cone — let the ship through (server docks it)
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
    // base's dock faces)? Used for the collision THUD — same geometry as ResolveStatics, no bounce.
    // `launchClassMask` mirrors ResolveStatics: a restricted hull thuds where it would bounce. `vel`
    // feeds the same angle-of-attack gate the resolver uses; for remote ships the caller's velocity
    // is interpolation-smoothed, so a near-threshold thud may briefly disagree with the server's
    // bounce — cosmetic only (the SFX debounce swallows it).
    public static bool Touches(
        Vec3 pos,
        Vec3 vel,
        float shipRadius,
        System.Collections.Generic.IReadOnlyList<StaticBody> bodies,
        int localTeam,
        ushort launchClassMask,
        float dockFaceDepth
    )
    {
        for (int i = 0; i < bodies.Count; i++)
        {
            StaticBody b = bodies[i];
            bool ownBase = b.BaseTeam >= 0 && b.BaseTeam == localTeam;
            if (
                ownBase
                && b.DockFaces != null
                && DockRules.ClassAllowed(launchClassMask, b.StationClass)
                && IntersectsDockFace(
                    pos - b.Center,
                    vel,
                    b.DockFaces,
                    dockFaceDepth,
                    shipRadius,
                    DockRules.AllowedFace(launchClassMask, b.LargestDockFace)
                )
            )
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

    // ---- Ship-vs-ship (shared with client prediction) --------------------

    // Ship-vs-ship contact (any pair). With hulls loaded the contact is each ship's center, as a
    // shipRadius sphere, against the OTHER ship's convex hull — the deeper of the two contacts wins;
    // with neither hull it falls back to the legacy equal-radius sphere overlap. `n` is oriented
    // b → a so the caller's impulse pushes them apart. hullA/hullB are pre-scaled to world (the
    // authored ModelLength); boundA/boundB are their bounding radii (shipRadius when hull-less).
    // ONE kernel for the server's Pass C AND the client's local-ship prediction, so the predicted
    // bounce matches the authoritative one.
    public static bool ShipShipContact(
        Vec3 posA,
        Quat rotA,
        ConvexHull? hullA,
        float boundA,
        Vec3 posB,
        Quat rotB,
        ConvexHull? hullB,
        float boundB,
        float shipRadius,
        out Vec3 n,
        out float pen
    )
    {
        n = default;
        pen = 0f;

        if (hullA is null && hullB is null)
        {
            // Legacy equal-radius sphere overlap.
            Vec3 d = posA - posB;
            float dist2 = d.LengthSquared();
            float minD = 2f * shipRadius;
            if (dist2 >= minD * minD)
                return false;
            float dist = (float)System.Math.Sqrt(dist2);
            n = dist > 1e-4f ? d * (1f / dist) : new Vec3(0f, 1f, 0f);
            pen = minD - dist;
            return true;
        }

        // Broad-phase: the two world bounding spheres.
        float bound = boundA + boundB;
        if ((posA - posB).LengthSquared() >= bound * bound)
            return false;

        // a's center vs b's hull → normal already points out of b toward a (= b → a).
        if (hullB is ConvexHull hb && SphereVsHull(posA, shipRadius, hb, posB, rotB, 1f, out Vec3 nB, out float pB))
        {
            n = nB;
            pen = pB;
        }
        // b's center vs a's hull → normal points out of a toward b (a → b); negate to b → a.
        if (
            hullA is ConvexHull ha
            && SphereVsHull(posB, shipRadius, ha, posA, rotA, 1f, out Vec3 nA, out float pA)
            && pA > pen
        )
        {
            n = nA * -1f;
            pen = pA;
        }
        return pen > 0f;
    }

    // A ship the LOCAL predicted ship can bump into: a remote ship's interpolated pose + smoothed
    // authoritative velocity, its mass off the Ship row, and its class hull (null = sphere fallback).
    public readonly struct MovingShip
    {
        public readonly Vec3 Pos;
        public readonly Quat Rot;
        public readonly Vec3 Vel;
        public readonly float Mass;
        public readonly ConvexHull? Hull; // pre-scaled to world; null = shipRadius sphere
        public readonly float BoundingRadius; // hull bound (shipRadius when Hull is null)

        public MovingShip(Vec3 pos, Quat rot, Vec3 vel, float mass, ConvexHull? hull, float boundingRadius)
        {
            Pos = pos;
            Rot = rot;
            Vel = vel;
            Mass = mass;
            Hull = hull;
            BoundingRadius = boundingRadius;
        }
    }

    // Resolve the LOCAL predicted ship against the other ships it can see, applying ONLY the local
    // ship's mass-weighted share of the server's Pass C response (restitution impulse + push-out
    // split by inverse mass). The other ship is authority-owned — the client can't move it; its
    // share of the separation arrives with the next authoritative snapshot. KINEMATIC only (the
    // server owns collision damage). Returns true on any contact and the last contact position
    // (for the collision thud).
    public static bool ResolveShipsLocal(
        ref ShipState s,
        float shipRadius,
        ConvexHull? localHull,
        float localBound,
        System.Collections.Generic.IReadOnlyList<MovingShip> ships,
        float restitution,
        out Vec3 hitPos
    )
    {
        bool hit = false;
        hitPos = default;
        for (int i = 0; i < ships.Count; i++)
        {
            MovingShip o = ships[i];
            if (
                !ShipShipContact(
                    s.Pos,
                    s.Rot,
                    localHull,
                    localBound,
                    o.Pos,
                    o.Rot,
                    o.Hull,
                    o.BoundingRadius,
                    shipRadius,
                    out Vec3 n,
                    out float pen
                )
            )
                continue;
            // The local half of the server's ResolveShipImpulse (n points other → local).
            float iA = s.Mass > 0f ? 1f / s.Mass : 1f;
            float iB = o.Mass > 0f ? 1f / o.Mass : 1f;
            float invSum = iA + iB;
            float relVn = Dot(s.Vel - o.Vel, n);
            if (relVn < 0f)
                s.Vel += n * (-(1f + restitution) * relVn / invSum * iA);
            s.Pos += n * (pen * (iA / invSum));
            hit = true;
            hitPos = s.Pos;
        }
        return hit;
    }

    // True when a ship sphere intersects one of a base's bounded rectangular docking FACES (doors):
    // the ship center is inside the door rectangle laterally (± the axis half-extents + shipRadius
    // slop) AND within a depth window along the face's inward normal. `d` is the ship position
    // relative to the base center (face fields are in that same base-local, already-world-scaled
    // frame). Iterates EVERY face — a base may author N doors (each a group of 5 markers). The
    // inward-slack depth window [−dockFaceDepth, +shipRadius] keeps a fast ship (worst case Scout
    // ~8 world units/tick at 20 Hz) from tunneling the thin plane between ticks: the window spans
    // dockFaceDepth+shipRadius ≈ 12 ≥ 8 with margin.
    //
    // These position-only overloads answer "is this POINT in a door window" (validators, tests,
    // launch placement). The LIVE dock/skip path is the (d, vel, ...) overload below: since bases
    // bake fully solid (no corridor carve), docking additionally demands an angle of attack —
    // velocity closing on the face within the CollisionConfig.DockApproachMinCosSq cone. A hull
    // base docks ONLY through that gated test — everything else is the solid shell.
    public static bool IntersectsDockFace(Vec3 d, DockFace[] faces, float dockFaceDepth, float shipRadius) =>
        IntersectsDockFace(d, faces, dockFaceDepth, shipRadius, -1);

    // Angle-of-attack-gated overload — the live dock trigger AND the own-base bounce-skip (the two
    // must stay ONE predicate, evaluated bit-identically by server sim and client prediction, or
    // the bay mouth rubber-bands). Per candidate face the ship's velocity DIRECTION must close on
    // the door — speed is irrelevant:
    //   vn = vel·Normal ≥ 0                    (not backing away)
    //   vn² ≥ DockApproachMinCosSq · |vel|²    (within the 45° approach cone, sqrt-free)
    // Below DockDirectionDeadzoneSq the velocity has no meaningful direction and the gate passes
    // outright — a parked or brake-wobbling ship touching the face docks instead of flickering the
    // gate off on tiny negative vn. A gate-failing ship in the window simply collides with the
    // (uncarved) structure — the station is solid unless you move AT a door.
    public static bool IntersectsDockFace(
        Vec3 d,
        Vec3 vel,
        DockFace[] faces,
        float dockFaceDepth,
        float shipRadius,
        int onlyFace
    )
    {
        float v2 = vel.LengthSquared();
        bool directional = v2 > CollisionConfig.DockDirectionDeadzoneSq;
        int start = onlyFace >= 0 ? onlyFace : 0;
        int end = onlyFace >= 0 ? System.Math.Min(onlyFace + 1, faces.Length) : faces.Length;
        for (int i = start; i < end; i++)
        {
            DockFace f = faces[i];
            if (directional)
            {
                float vn = Dot(vel, f.Normal);
                if (vn < 0f)
                    continue;
                if (vn * vn < CollisionConfig.DockApproachMinCosSq * v2)
                    continue;
            }
            if (IntersectsDockFace(d, faces, dockFaceDepth, shipRadius, i))
                return true;
        }
        return false;
    }

    // True when the ship currently satisfies the OWN-BASE dock predicate against any body — the
    // exact condition under which the server consumes it this tick. Client prediction uses this to
    // latch "dock pending": ShipGone is an RTT away, and the ghost ticks predicted past the dock
    // would fly into the now-solid station interior and bounce/thud (the pre-2026-07 carved
    // corridors used to hide this by being empty space).
    public static bool InOwnDockWindow(
        Vec3 pos,
        Vec3 vel,
        System.Collections.Generic.IReadOnlyList<StaticBody> bodies,
        int localTeam,
        ushort launchClassMask,
        float dockFaceDepth,
        float shipRadius
    )
    {
        for (int i = 0; i < bodies.Count; i++)
        {
            StaticBody b = bodies[i];
            if (
                b.BaseTeam >= 0
                && b.BaseTeam == localTeam
                && b.DockFaces != null
                && DockRules.ClassAllowed(launchClassMask, b.StationClass)
                && IntersectsDockFace(
                    pos - b.Center,
                    vel,
                    b.DockFaces,
                    dockFaceDepth,
                    shipRadius,
                    DockRules.AllowedFace(launchClassMask, b.LargestDockFace)
                )
            )
                return true;
        }
        return false;
    }

    // `onlyFace` (2026-07-21 launch-station-classes): -1 tests every door (stock behaviour); >= 0
    // tests ONLY that face index — a restricted hull may enter solely through the base's largest
    // door (DockRules.AllowedFace), so every other door acts as solid shell for it.
    public static bool IntersectsDockFace(Vec3 d, DockFace[] faces, float dockFaceDepth, float shipRadius, int onlyFace)
    {
        int start = onlyFace >= 0 ? onlyFace : 0;
        int end = onlyFace >= 0 ? System.Math.Min(onlyFace + 1, faces.Length) : faces.Length;
        for (int i = start; i < end; i++)
        {
            DockFace f = faces[i];
            Vec3 rel = d - f.Center;
            float along = Dot(rel, f.Normal);
            if (along > shipRadius || along < -dockFaceDepth)
                continue;
            Vec3 lateral = rel - f.Normal * along;
            if (
                System.Math.Abs(Dot(lateral, f.U)) <= f.Eu + shipRadius
                && System.Math.Abs(Dot(lateral, f.V)) <= f.Ev + shipRadius
            )
                return true;
        }
        return false;
    }
}
