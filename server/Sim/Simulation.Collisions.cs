using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;
using static StellarAllegiance.Shared.Vec3;

namespace SimServer.Sim;

// Collision resolution (module Pass C, mass-weighted): ship-vs-ship contact + impulse, ship-vs-
// asteroid / base / deployable bounces, and the kernels behind them (BounceShip, ResolveHullCollision,
// ResolveStaticCollision, CollisionDamage). Also home to the ray kernels HullRayEntry /
// BaseHullsRayEntry, which the firing + missile sweeps call.
// Split out of Simulation.cs on 2026-09-09 — a pure move: no behaviour, order, or signature change.

public sealed partial class Simulation
{
    // ---- Collisions (module Pass C, mass-weighted) ------------------------

    // Ship-vs-ship contact (any pair, friend or foe). With both ships' GLB hulls loaded the contact is resolved as a
    // ShipRadius sphere against the OTHER ship's convex hull (the same kernel asteroids/bases use),
    // so a long bomber or a wide fighter collides on its real silhouette; without hulls it falls
    // back to the legacy equal-radius sphere overlap. The contact math lives in the SHARED
    // Collide.ShipShipContact so the client's local-ship prediction resolves the identical bounce.
    // The resolution is the module's mass-weighted impulse + inverse-mass-split push-out along the
    // contact normal n (b → a).
    private void CollideShips(ShipSim a, ShipSim b)
    {
        var ha = World.ShipHull(a.Class, a.IsPod);
        var hb = World.ShipHull(b.Class, b.IsPod);

        if (
            !Collide.ShipShipContact(
                a.State.Pos,
                a.State.Rot,
                ha?.Hull,
                ha?.BoundingRadius ?? World.ShipRadius,
                b.State.Pos,
                b.State.Rot,
                hb?.Hull,
                hb?.BoundingRadius ?? World.ShipRadius,
                World.ShipRadius,
                out Vec3 n,
                out float pen
            )
        )
            return;

        ResolveShipImpulse(a, b, n, pen);
    }

    // Module-identical mass-weighted bounce: restitution impulse + collision damage when closing,
    // and an inverse-mass-split positional correction along n (which points b → a).
    private void ResolveShipImpulse(ShipSim a, ShipSim b, Vec3 n, float pen)
    {
        a.LastCollisionTick = b.LastCollisionTick = _tick; // any resolved contact, closing or not
        float iA = a.State.Mass > 0f ? 1f / a.State.Mass : 1f;
        float iB = b.State.Mass > 0f ? 1f / b.State.Mass : 1f;
        float invSum = iA + iB;

        float relVn = Dot(a.State.Vel - b.State.Vel, n);
        if (relVn < 0f)
        {
            float jimp = -(1f + World.CollisionRestitution) * relVn / invSum;
            a.State.Vel += n * (jimp * iA);
            b.State.Vel -= n * (jimp * iB);
            float dmg = CollisionDamage(-relVn, (1f / invSum) * _combat.ShipShipDamageScale);
            ApplyDamage(a, dmg, _tick);
            ApplyDamage(b, dmg, _tick);
            // Ground-truth ram line (logging only, zero sim effect): correlates the client's local_hits
            // windows against the server's actual same-tick contacts to catch client-only phantom hits.
            _log.LogInformation("[ram] a={A} b={B} tick={Tick} closing={Closing:F1}", a.ShipId, b.ShipId, _tick, -relVn);
        }
        a.State.Pos += n * (pen * (iA / invSum));
        b.State.Pos -= n * (pen * (iB / invSum));
    }

    private void ResolveAsteroidCollisions(ShipSim s)
    {
        // A constructor deliberately sinks into and embeds in its target rock during the build phases;
        // resolving that penetration would shove it back out every tick (the visible flicker). Skip
        // collision with just that one rock while it's aligning/sinking/building on it.
        ulong ignoreRock = s.Kind == ShipKind.Constructor ? ConstructorEmbeddedRock(s) : 0;

        var grid = World.RockGrid(s.SectorId);
        int cx = World.CellOf(s.State.Pos.X),
            cy = World.CellOf(s.State.Pos.Y),
            cz = World.CellOf(s.State.Pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var a in cell)
            {
                if (a.Id == ignoreRock)
                    continue;
                // Cheap bounding-sphere reject at the SPAWN radius (conservative — rocks only shrink,
                // so this never skips a real contact), then the convex hull if this rock has one — else
                // the legacy sphere sized to the rock's CURRENT (mined-down) surface.
                Vec3 dd = s.State.Pos - a.Pos;
                float bound = a.Radius + World.ShipRadius;
                if (dd.LengthSquared() >= bound * bound)
                    continue;
                if (World.RockBodies.TryGetValue(a.Id, out var body))
                {
                    // Live tumble: compose the spawn pose with the spin at the current tick so the
                    // authoritative hull matches the rendered rock (Collide.RockRotationAt, shared).
                    // body.Scale tracks the live radius (SetOreRemaining re-scales it as ore is mined).
                    Quat rot = Collide.RockRotationAt(body.Rot, body.SpinAxis, body.SpinSpeed, _tick * FlightModel.Dt);
                    ResolveHullCollision(s, body.Hull, a.Pos, rot, body.Scale);
                }
                else
                    ResolveStaticCollision(s, a.Pos, World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale);
            }
        }
    }

    // Bounce a ship off a base: the loaded compound world hull if present, else the legacy radius
    // sphere. Runs through the shared Collide.SphereVsBody kernel over the authored sub-hulls (deepest
    // contact = one BounceShip), so an enemy base bounces exactly as the client predicts it.
    private void ResolveBaseCollision(ShipSim s, Vec3 center, byte baseTypeId)
    {
        if (World.BaseHullOf(baseTypeId) is not null)
        {
            if (Collide.SphereVsBody(s.State.Pos, World.ShipRadius, BaseBody(center, baseTypeId), out Vec3 n, out float pen))
                BounceShip(s, n, pen);
        }
        else
            ResolveStaticCollision(s, center, World.BaseRadiusOf(baseTypeId));
    }

    // The base as a shared compound StaticBody at `center`: `BaseHull` is the merged shrink-wrap (kept
    // non-null for the struct + broadphase parity), `BaseSubHulls` are the authored parts the kinematic
    // kernel actually resolves against. Team/discs are irrelevant to SphereVsBody (it never gates on
    // them — dock carve-out is handled by the caller), so a fixed team/the discs are passed for shape.
    // Callers guard World.BaseHull is not null first.
    private Collide.StaticBody BaseBody(Vec3 center, byte baseTypeId) =>
        Collide.StaticBody.BaseHull(
            World.BaseHullOf(baseTypeId)!,
            World.BaseSubHullsOf(baseTypeId),
            center,
            0,
            World.BaseDockFacesOf(baseTypeId),
            StationClassOfBaseType(baseTypeId),
            World.BaseLargestDockFaceOf(baseTypeId)
        );

    // Min-entry-t of a ray (mp + mv·t) across the base's authored sub-hulls (world-scaled, identity
    // frame), the ray analogue of SphereVsBody: the CLOSEST sub-hull surface stops the bolt/missile, so
    // a shot threading a gap between parts passes through exactly as the client renders it. Reuses
    // HullRayEntry per part; the caller's `bestT` plumbing still picks the closest target overall.
    private bool BaseHullsRayEntry(Vec3 center, byte baseTypeId, Vec3 mp, Vec3 mv, float margin, float maxT, out float t)
    {
        t = maxT;
        bool hit = false;
        var subs = World.BaseSubHullsOf(baseTypeId);
        for (int i = 0; i < subs.Length; i++)
            if (HullRayEntry(subs[i], center, Quat.Identity, 1f, mp, mv, margin, t, out float th) && th < t)
            {
                t = th;
                hit = true;
            }
        return hit;
    }

    // Resolve a ship against every DEPLOYABLE solid body in its sector. A deployable is a small,
    // stationary, low-HP object a ship can bounce off AND wreck by ramming — unlike a base (which uses
    // the base-health system, and is only DESTROYED via that system), a deployable dies from the same
    // collision/weapon damage anything else does. Recon probes are the only deployable today; a future
    // drop-turret / sensor buoy / decoy drone slots in here the SAME way: iterate its live list,
    // resolve each element through the ResolveDeployableSphere kernel, and feed the returned impact
    // damage into that deployable's own HP sink. Keep the generic bounce in the kernel; keep only the
    // per-type "which list / which radius / which damage sink" here. O(ships × deployables); few of each.
    private void ResolveDeployableCollisions(ShipSim s, uint tick)
    {
        // Recon probes (Simulation.Probes.cs): solid sphere = the combat hit radius, so "what you
        // shoot is what you bump"; a hard ram spends the impact on the probe's low HP → gone reason 2.
        for (int i = 0; i < _probes.Count; i++)
        {
            var p = _probes[i];
            if (p.SectorId != s.SectorId)
                continue;
            if (!WeaponDefs.TryGetValue(p.WeaponId, out var w) || w.ProbeHitRadius <= 0f)
                continue;
            float dmg = ResolveDeployableSphere(s, p.Pos, w.ProbeHitRadius, tick);
            if (dmg > 0f && p.Health > 0f) // Health 0 = authored-invulnerable: solid but undamageable
            {
                DamageProbe(p, dmg, tick);
                if (p.Health <= 0f)
                    i--; // DamageProbe removed p from _probes; don't skip the next entry
            }
        }
    }

    // Bounce a ship off ONE solid deployable sphere (the shared kernel behind ResolveDeployableCollisions).
    // Pushes the ship out of penetration (solid) and, on a closing contact, applies collision damage to
    // the SHIP (exactly like a base) and RETURNS that same damage so the caller can also spend it on the
    // deployable's own HP. Returns 0 on no contact or a below-min-speed kiss. Symmetric across teams —
    // you bounce off (and can wreck) your own deployables too.
    private float ResolveDeployableSphere(ShipSim s, Vec3 center, float radius, uint tick)
    {
        if (radius <= 0f)
            return 0f;
        if (
            Collide.ResolveStaticSphere(
                ref s.State,
                World.ShipRadius,
                center,
                radius,
                World.CollisionRestitution,
                out float vn
            )
            && vn < 0f
        )
        {
            float dmg = CollisionDamage(-vn, _combat.CollisionDamageScale);
            ApplyDamage(s, dmg, tick);
            return dmg;
        }
        return 0f;
    }

    // Sphere-vs-convex-hull bounce (the convex analogue of ResolveStaticCollision). The hull is
    // in its own authored frame at (center, rot, uniform scale); SphereVsHull maps the ship sphere
    // into that frame, resolves against the nearest face, and maps the contact back to world.
    private void ResolveHullCollision(ShipSim s, ConvexHull hull, Vec3 center, Quat rot, float scale)
    {
        if (Collide.SphereVsHull(s.State.Pos, World.ShipRadius, hull, center, rot, scale, out Vec3 n, out float pen))
            BounceShip(s, n, pen);
    }

    // Bounce a ship off a contact: shared kinematic push-out + velocity reflect (Collide.Bounce),
    // then the SERVER-ONLY collision damage from the inbound normal speed. Shared by
    // ResolveHullCollision (asteroids, enemy base) and the friendly-base solid-shell branch. The
    // client runs Collide.Bounce too (no damage — health is server-authoritative).
    private void BounceShip(ShipSim s, Vec3 worldNormal, float worldPenetration)
    {
        s.LastCollisionTick = _tick;
        Collide.Bounce(ref s.State, worldNormal, worldPenetration, World.CollisionRestitution, out float vn);
        if (vn < 0f)
            ApplyDamage(s, CollisionDamage(-vn, _combat.CollisionDamageScale), _tick);
    }

    // Ray (mp + mv·t) first-entry time against a transformed hull, expanded by `margin`. Maps the
    // ray into hull-local space; t is invariant under the rigid+uniform-scale transform.
    private static bool HullRayEntry(
        ConvexHull hull,
        Vec3 center,
        Quat rot,
        float scale,
        Vec3 mp,
        Vec3 mv,
        float margin,
        float maxT,
        out float t
    )
    {
        t = 0f;
        if (scale <= 1e-6f)
            return false;
        float inv = 1f / scale;
        Quat rotInv = rot.Conjugate();
        Vec3 o = rotInv.Rotate(mp - center) * inv;
        Vec3 dir = rotInv.Rotate(mv) * inv;
        return hull.RayEntry(o, dir, maxT, margin * inv, out t);
    }

    // Sphere-vs-sphere static bounce fallback (a rock without a hull, or a base without a model):
    // shared kinematic (Collide.ResolveStaticSphere) + server-only collision damage.
    private void ResolveStaticCollision(ShipSim s, Vec3 center, float radius)
    {
        if (
            !Collide.ResolveStaticSphere(
                ref s.State,
                World.ShipRadius,
                center,
                radius,
                World.CollisionRestitution,
                out float vn
            )
        )
            return;
        s.LastCollisionTick = _tick;
        if (vn < 0f)
            ApplyDamage(s, CollisionDamage(-vn, _combat.CollisionDamageScale), _tick);
    }

    // Server-only collision damage from a closing normal speed (m/s, always positive). Below
    // the authored collision-damage-min-speed it's a harmless kiss: 0 damage (the bounce still ran). Above it,
    // scaled and capped at max-collision-damage. Shared by ship-ship, hull, and sphere-fallback bounces.
    private float CollisionDamage(float closingSpeed, float scale) =>
        closingSpeed > _combat.CollisionDamageMinSpeed ? MathF.Min(closingSpeed * scale, _combat.MaxCollisionDamage) : 0f;
}
