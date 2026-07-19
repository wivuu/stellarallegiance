using System;
using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// Client-synthesized projectile visuals — every live bolt (no server Projectile rows exist): the local
// ship's from fire prediction (SpawnLocalBolt), remote ships' rebuilt from LastFireTick (SpawnBoltFor).
// Bolts are cosmetic; the server resolved the real damage analytically at fire time. Each bolt's flight
// time is clipped at spawn against the static ClipCache geometry (rocks + base hulls), and per-frame the
// swept path is tested against ships/probes/alephs for a hit spark (CheckBoltImpacts); expired bolts are
// culled (CullTick). Owns its bolt nodes under the injected `_projectiles` container.
public sealed class BoltRenderer
{
    private readonly Node3D _projectiles;
    private readonly DefRegistry _defs;
    private readonly StandardMaterial3D _projectileMat;
    private readonly StandardMaterial3D _healBoltMat;
    private readonly SectorView _sectors;
    private readonly ClipCache _clip;
    private readonly CollisionWorld _collisionWorld;
    private readonly IEffectSink _effects;
    private readonly ShipRenderer _ships; // concrete: needs Nodes/TryGetShield/LocalShip + MountsFor/MountShadow
    private readonly IProbeQuery _probes;
    private readonly IAlephQuery _aleph;
    private readonly Func<float> _pingMs;

    // Enemy-shot masking lead (see ProjectileView). -1 = auto (derive from measured one-way latency);
    // >= 0 = a fixed override in ms, pinned via SHOT_MASK_MS for playtest tuning. Parsed once at boot.
    private readonly float _shotMaskMs;

    // Client-side hit-spark tuning. A bolt sparks when its swept path this frame passes within
    // VisualHitRadius of a ship's rendered centre. The firing ship is excluded by bolt OwnerShipId
    // (see CheckBoltImpacts), so a shot never sparks on its own hull; otherwise team-agnostic by
    // design (friendly fire sparks too).
    private const float VisualHitRadius = 5f;

    // Smallest impact time we treat as "past the muzzle": a hit inside this window is the gun firing
    // from within/against the hull, and is killed silently rather than sparked on itself.
    private const float ImpactEps = 1e-3f;

    // Cyan shield-bubble tint (#37E0FF), matching the HUD SHLD arc; alpha sets the flash's base opacity.
    private static readonly Color ShieldFlashTint = new(0.216f, 0.878f, 1f, 0.3f);
    private static readonly Color HealSparkTint = new(0.35f, 1f, 0.5f, 1f); // ER Nanite heal-impact spark (green)

    // Every live bolt, all client-synthesized. Culled on TTL expiry (CullTick) or on visually striking a
    // ship (CheckBoltImpacts).
    private readonly List<ProjectileView> _bolts = new();

    public BoltRenderer(
        Node3D projectiles,
        DefRegistry defs,
        StandardMaterial3D projectileMat,
        StandardMaterial3D healBoltMat,
        SectorView sectors,
        ClipCache clip,
        CollisionWorld collisionWorld,
        IEffectSink effects,
        ShipRenderer ships,
        IProbeQuery probes,
        IAlephQuery aleph,
        Func<float> pingMs,
        float shotMaskMs
    )
    {
        _projectiles = projectiles;
        _defs = defs;
        _projectileMat = projectileMat;
        _healBoltMat = healBoltMat;
        _sectors = sectors;
        _clip = clip;
        _collisionWorld = collisionWorld;
        _effects = effects;
        _ships = ships;
        _probes = probes;
        _aleph = aleph;
        _pingMs = pingMs;
        _shotMaskMs = shotMaskMs;
    }

    // A REMOTE ship's row showed a new LastFireTick: rebuild the shot the server fired — the exact mirror
    // of the module's TryFire muzzle math. The spread direction is deterministic in (ShipId, fire tick) via
    // the shared FlightModel.SpreadDirection, and WHICH mounts fired is derived by replaying the shared
    // FireCadence rule against this ship's per-mount shadow (per-mount cooldowns; the wire carries only
    // LastFireTick), so every client and the server derive the same bolts from the same replicated row. A
    // fresh shadow (first sight / loadout change / reconnect) renders the first volley from every
    // off-cooldown mount and is in lockstep from then on; a lossy far-tier ship that skips fire events
    // drifts and self-corrects — visual only.
    public void SpawnBoltFor(Ship row)
    {
        var slots = _defs.SlotsForShip((byte)row.Class, _ships.MountsFor(row.ShipId));
        if (slots.Count == 0)
            return;
        var shadow = _ships.MountShadow(row.ShipId, slots.Count);

        var state = ShipMath.StateFromRow(row);

        // Under server catch-up, one row update can span several sim ticks; the row's position is at
        // LastInputTick while the shot left at LastFireTick. Rewind the ship along its (constant-velocity
        // approximation) path to the fire tick so the muzzle sits where the ship was when it fired.
        uint ticksPast =
            row.LastInputTick > row.LastFireTick ? System.Math.Min(row.LastInputTick - row.LastFireTick, 8u) : 0u;
        Vec3 firePos = state.Pos - state.Vel * (ticksPast * FlightModel.Dt);

        // One bolt per FIRING weapon slot, each from its own muzzle offset and with its own barrel-seeded
        // scatter — the exact mirror of the server's TryFire.
        for (byte barrel = 0; barrel < slots.Count; barrel++)
        {
            var (hp, weapon) = slots[barrel];
            // Skip empty slots and missile racks: they don't fire bolts. The barrel index is STILL consumed
            // so the per-barrel spread seed stays aligned with the server's TryFire loop regardless of where
            // racks/empties sit in the hardpoint array.
            if (weapon is null || weapon.Kind != WeaponKind.Bolt)
                continue;
            // Off cooldown at the observed fire tick? (The same gate the server fired by.)
            if (!FireCadence.MountFires(row.LastFireTick, shadow[barrel], weapon.FireIntervalTicks))
                continue;
            shadow[barrel] = row.LastFireTick;
            Vec3 fwd = state.Rot.Rotate(new Vec3(hp.DirX, hp.DirY, hp.DirZ));
            Vec3 shotDir = FlightModel.SpreadDirection(fwd, weapon.SpreadRad, row.ShipId, row.LastFireTick, barrel);
            Vec3 mp = firePos + state.Rot.Rotate(new Vec3(hp.OffX, hp.OffY, hp.OffZ));
            Vec3 mv = shotDir * weapon.ProjectileSpeed + state.Vel;

            AddBolt(
                ShipMath.ToGodot(mp),
                ShipMath.ToGodot(mv),
                ShipMath.ToGodot(shotDir),
                row.SectorId,
                weapon.ProjectileLifeTicks * FlightModel.Dt,
                row.ShipId,
                ShotMaskLeadSec(),
                weapon.BoltRadius,
                weapon.BoltLength,
                weapon.IsHealing
            );
        }
    }

    // The LOCAL ship's fire prediction produced a shot this tick (ShipController). Same rendering as a
    // remote bolt, no masking lead (prediction is already now-correct).
    public void SpawnLocalBolt(
        Vector3 pos,
        Vector3 vel,
        Vector3 aimDir,
        float lifeSec,
        float boltRadius,
        float boltLength,
        bool isHeal
    ) =>
        AddBolt(
            pos,
            vel,
            aimDir,
            _sectors.LocalSector,
            lifeSec,
            _ships.LocalShip?.ShipId ?? 0,
            0f,
            boltRadius,
            boltLength,
            isHeal
        );

    private void AddBolt(
        Vector3 pos,
        Vector3 vel,
        Vector3 aimDir,
        uint sector,
        float lifeSec,
        ulong ownerShipId,
        float leadSec,
        float boltRadius,
        float boltLength,
        bool isHeal
    )
    {
        var pv = new ProjectileView { Name = "Bolt", IsHeal = isHeal };
        _projectiles.AddChild(pv);
        pv.AddChild(NewProjectileMesh(boltRadius, boltLength, isHeal));
        float ttl = ClipBoltTtl(sector, pos, vel, lifeSec, out Vector3 impact, out bool impactAtExpiry);
        pv.Initialize(pos, vel, aimDir, ttl, ownerShipId, leadSec);
        // Carry the static-surface impact (if any) so the TTL-expiry cull sparks it (see CullTick).
        pv.ImpactPoint = impact;
        pv.ImpactAtExpiry = impactAtExpiry;
        pv.Sector = sector;
        _sectors.SetNodeSector(pv, sector);
        _bolts.Add(pv);
        // Single chokepoint for every shot (local + remote), so the muzzle report fires once per bolt at
        // the muzzle position.
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.WeaponFire, pos, pitch: 0.95f + GD.Randf() * 0.1f);
    }

    // How far ahead to render an enemy/remote shot to mask its ~1 RTT-late pop-in (see
    // ProjectileView._renderLeadSec). Auto mode uses the measured one-way latency (≈ half RTT);
    // SHOT_MASK_MS pins a fixed value. Clamped so a bad reading can't fling shots downrange. Returns 0 on
    // localhost (PingMs unmeasured) — no masking needed.
    private float ShotMaskLeadSec()
    {
        if (_shotMaskMs >= 0f)
            return Mathf.Min(_shotMaskMs, 400f) / 1000f;
        float oneWayMs = _pingMs() * 0.5f;
        return Mathf.Clamp(oneWayMs, 0f, 250f) / 1000f;
    }

    // Clip a bolt's flight time at the first STATIC obstruction (asteroid / enemy-or-any base) along its
    // line, so the visual stops at a rock the way the server's analytic solve does. Static geometry is
    // fully replicated, so this is a spawn-time pass over the local caches — ships stay dynamic and are
    // handled by the per-frame spark sweep.
    //
    // `impact`/`impactAtExpiry` report where — and whether — the bolt terminates on a base's VISIBLE
    // surface: the TTL-expiry cull drops a HitFlash + impact sound at that point, so a shot no longer
    // vanishes in the empty space between the coarse BaseDef sphere and the real superstructure. COSMETIC
    // ONLY, and a deliberately looser fit than the server: the server kills real bolts / applies damage at
    // CONVEX-HULL entry, so this visual may fly slightly farther (to the actual mesh face) or slip through a
    // concave gap the hull shrink-wraps over. Accepted for Phase A; Phase B's authored compound hulls close
    // that gap.
    private float ClipBoltTtl(uint sector, Vector3 pos, Vector3 vel, float ttl, out Vector3 impact, out bool impactAtExpiry)
    {
        impact = Vector3.Zero;
        impactAtExpiry = false;

        // Asteroids first (unchanged, silent): whatever they clip to bounds the base ray below, so a rock
        // nearer than the base ends the segment before it reaches the base and no base spark registers — the
        // asteroid clip naturally "wins" without any explicit flag bookkeeping. (Asteroid impact sparks are
        // a later follow-up; today the tracer just stops at the rock.)
        foreach (var a in _clip.Asteroids)
        {
            if (a.Sector != sector)
                continue;
            ClipSphere(pos, vel, a.Pos, a.Radius, ref ttl);
        }

        float baseR = _defs.GetBaseDef(BaseRenderer.DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius;
        foreach (var b in _clip.Bases)
        {
            if (b.Sector != sector)
                continue;
            // Cheap broadphase: reject bolts whose (already asteroid-clipped) segment can't come near the
            // base at all. A touch fatter than the sphere so a near-graze still gets the precise test. Never
            // mutates ttl — that's the tiered narrow-phase's job.
            if (!SegmentNearSphere(pos, vel, b.Pos, baseR * 1.1f, ttl))
                continue;

            if (b.Ray != null)
            {
                // Tier 1: the real visible mesh. A hit past the muzzle terminates the bolt on the rendered
                // surface (spark there); a hit at/behind the muzzle (t ≤ eps) means the gun is inside/against
                // the hull — kill the bolt silently, mirroring ClipSphere's c ≤ 0 path and the server killing
                // at t ≈ 0 (no self-spark on your own hull).
                if (b.Ray.IntersectSegment(pos, pos + vel * ttl, out Vector3 hitW, out _))
                {
                    float tHit = SegmentTime(pos, vel, hitW);
                    if (tHit > ImpactEps)
                    {
                        ttl = tHit;
                        impact = hitW;
                        impactAtExpiry = true;
                    }
                    else
                    {
                        ttl = 0f;
                        impactAtExpiry = false;
                    }
                }
            }
            else if (
                _collisionWorld.BaseRayEntry(
                    sector,
                    new Vec3(pos.X, pos.Y, pos.Z),
                    new Vec3(vel.X, vel.Y, vel.Z),
                    ttl,
                    out float tHull
                )
            )
            {
                // Tier 2: procedural placeholder rendered, but the server-parity convex hull is loaded —
                // still far tighter than the sphere. Spark at the hull-entry point, unless the muzzle is
                // already inside (t ≤ eps), which is silent like tier 1.
                if (tHull > ImpactEps)
                {
                    ttl = tHull;
                    impact = pos + vel * tHull;
                    impactAtExpiry = true;
                }
                else
                {
                    ttl = 0f;
                    impactAtExpiry = false;
                }
            }
            else
            {
                // Tier 3: no hull either (sphere-collision fallback) — the coarse sphere is too far from the
                // real surface to decorate, so clip silently, exactly as before.
                ClipSphere(pos, vel, b.Pos, baseR, ref ttl);
            }
        }
        return ttl;
    }

    // Parameter t along pos + vel·t of a point known to lie on that line (the mesh/hull hit).
    private static float SegmentTime(Vector3 pos, Vector3 vel, Vector3 point)
    {
        float a = vel.LengthSquared();
        return a < 1e-6f ? 0f : (point - pos).Dot(vel) / a;
    }

    // Does the segment pos + vel·[0, ttl] come within `radius` of `center`? A pure boolean broadphase
    // (mirrors ClipSphere's quadratic) that never touches ttl — used to skip the precise base ray-cast for
    // bolts that clearly miss the base entirely.
    private static bool SegmentNearSphere(Vector3 pos, Vector3 vel, Vector3 center, float radius, float ttl)
    {
        Vector3 d = center - pos;
        float a = vel.LengthSquared();
        if (a < 1e-6f)
            return d.LengthSquared() <= radius * radius;
        float c = d.LengthSquared() - radius * radius;
        if (c <= 0f)
            return true; // muzzle already inside the broadphase sphere
        float b = -2f * d.Dot(vel);
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
            return false;
        float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
        return t > 0f && t < ttl;
    }

    // How much of the cosmetic Sun's line-of-sight is clear (1 = fully visible, 0 = fully blocked) from a
    // camera at camPos looking along the unit sunDir. The sky Sun quad is a real depth-tested billboard, so
    // it already hides behind rocks/bases; this exists purely so the LensFlare — a screen-space overlay with
    // no depth of its own — can fade out when the disc it anchors to is occluded, instead of bleeding light
    // through solid geometry. Analytic ray-vs-sphere over the same static caches the bolt clip uses, in the
    // viewed sector only (that's what's actually drawn around the camera). A soft feather ring around each
    // occluder keeps the flare from popping on/off at a hard silhouette edge.
    public float SunVisibility(Vector3 camPos, Vector3 sunDir)
    {
        float occ = 0f; // strongest single occluder wins
        uint sector = _sectors.ViewSector;
        foreach (var a in _clip.Asteroids)
        {
            if (a.Sector == sector)
                occ = Mathf.Max(occ, RayOcclusion(camPos, sunDir, a.Pos, a.Radius));
        }
        float baseR = _defs.GetBaseDef(BaseRenderer.DefaultBaseTypeId)?.Radius ?? BaseModelLoader.FallbackRadius;
        foreach (var b in _clip.Bases)
        {
            if (b.Sector == sector)
                occ = Mathf.Max(occ, RayOcclusion(camPos, sunDir, b.Pos, baseR));
        }
        return 1f - occ;
    }

    // Occlusion (0..1) of a ray from origin along unit dir by a sphere: 1 when the ray passes through the
    // sphere, easing to 0 across a feather ring of half a radius outside it, and 0 for any sphere behind the
    // camera. The sun sits far beyond any sector geometry, so a sphere in front along the ray always lies
    // between camera and disc — no far-limit needed.
    private static float RayOcclusion(Vector3 origin, Vector3 dir, Vector3 center, float radius)
    {
        Vector3 l = center - origin;
        float t = l.Dot(dir); // distance to the point on the ray closest to the sphere centre
        if (t <= 0f)
            return 0f; // occluder is behind the camera
        float perp = Mathf.Sqrt(Mathf.Max(0f, l.LengthSquared() - t * t));
        float feather = radius * 0.5f;
        // perp <= radius: fully inside -> 1; perp >= radius+feather: clear -> 0.
        return 1f - Mathf.SmoothStep(radius, radius + feather, perp);
    }

    // Smallest positive entry time of the line pos+vel·t into a static sphere, if it is within the current
    // ttl — the client-side mirror of the module's FirstEntryTime specialized to a static target.
    private static void ClipSphere(Vector3 pos, Vector3 vel, Vector3 center, float radius, ref float ttl)
    {
        Vector3 d = center - pos;
        float a = vel.LengthSquared();
        if (a < 1e-6f)
            return;
        float b = -2f * d.Dot(vel);
        float c = d.LengthSquared() - radius * radius;
        if (c <= 0f)
        {
            ttl = 0f;
            return;
        } // spawned inside (e.g. muzzle against the rock)
        float disc = b * b - 4f * a * c;
        if (disc < 0f)
            return;
        float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
        if (t > 0f && t < ttl)
            ttl = t;
    }

    // Bolt visual size is authored per-projectile (WeaponDef.BoltRadius/BoltLength); a 0 falls back to the
    // built-in default so an unauthored weapon still renders a bolt.
    private MeshInstance3D NewProjectileMesh(float radius, float height, bool isHeal)
    {
        float r = radius > 0f ? radius : 0.22f;
        float h = height > 0f ? height : 2.2f;
        return new MeshInstance3D
        {
            // Slim tracer bolt. The cylinder's long axis is local +Y; rotate it to local +Z so it runs along
            // ProjectileView's forward, which is aimed down the bolt's velocity.
            Mesh = new CylinderMesh
            {
                TopRadius = r,
                BottomRadius = r,
                Height = h,
                RadialSegments = 8,
                Rings = 1,
            },
            MaterialOverride = isHeal ? _healBoltMat : _projectileMat,
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            // Self-lit glowing tracers: casting shadows would be wasteful and wrong-looking.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // Purely client-side hit sparks: flash where a rendered bolt visually meets a ship this frame, then
    // consume the bolt so it stops on impact. Cosmetic and team-agnostic (friendly fire sparks like anything
    // else); the server resolved the real damage analytically at fire time. The muzzle-clearance gate keeps
    // a bolt from sparking on the ship that fired it. Visibility gates both bolt and ship to the local
    // sector — sectors share world coordinates, so this also avoids cross-sector hits.
    public void CheckBoltImpacts(double delta)
    {
        if (_bolts.Count == 0 || (_ships.Nodes.Count == 0 && _probes.Nodes.Count == 0 && _aleph.Nodes.Count == 0))
            return;

        uint localSector = _sectors.LocalSector;
        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            var pv = _bolts[i];
            if (!pv.Visible)
                continue;
            Vector3 b = pv.GlobalPosition;
            Vector3 a = b - pv.Velocity * (float)delta; // swept path across this frame
            bool consumed = false;
            foreach (var (shipId, ship) in _ships.Nodes)
            {
                // Never spark on the firing ship. Skipping by owner id (rather than a static muzzle-distance
                // gate) holds even when the ship flies forward with its own bolt — flying straight while
                // shooting no longer sparks on your own hull.
                if (shipId == pv.OwnerShipId)
                    continue;
                if (!ship.Visible)
                    continue;
                Vector3 c = ship.GlobalPosition;
                Vector3 hit = ClosestPointOnSegment(a, b, c);
                if (c.DistanceSquaredTo(hit) <= VisualHitRadius * VisualHitRadius)
                {
                    if (pv.IsHeal)
                    {
                        // ER Nanite heal impact: a green spark on the (same-team) ship it restores; a heal
                        // bypasses shields, so never the shield-bubble flash even if it's up.
                        _effects.SpawnEffect(
                            new HitFlash { CoreColor = HealSparkTint, EmissionColor = HealSparkTint },
                            hit,
                            localSector
                        );
                        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, hit, pitch: 1.15f + GD.Randf() * 0.12f);
                    }
                    // Shield up on the struck ship → a hemisphere shield-bubble flash + shield sound;
                    // otherwise the plain hull spark + impact sound. Both cosmetic/predicted.
                    else if (_ships.TryGetShield(shipId, out float sh) && sh > 0f)
                    {
                        _effects.SpawnEffect(
                            new ShieldFlash(hit - c, VisualHitRadius * 1.2f, ShieldFlashTint),
                            c,
                            localSector
                        );
                        SfxManager.Instance?.PlayAt(SfxManager.SfxId.ShieldImpact, hit, pitch: 0.95f + GD.Randf() * 0.12f);
                    }
                    else
                    {
                        _effects.SpawnEffect(new HitFlash(), hit, localSector);
                        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, hit, pitch: 0.92f + GD.Randf() * 0.16f);
                    }
                    pv.QueueFree();
                    _bolts.RemoveAt(i);
                    consumed = true;
                    break;
                }
            }
            if (consumed)
                continue;

            // A deployed probe is a solid, shootable object too: spark + consume the bolt where it meets a
            // visible probe (the server resolved the real damage). Team-agnostic, like ships.
            foreach (var (probeId, probe) in _probes.Nodes)
            {
                if (!probe.Visible)
                    continue;
                Vector3 c = probe.GlobalPosition;
                float r = Mathf.Max(VisualHitRadius, probe.HitRadius);
                Vector3 hit = ClosestPointOnSegment(a, b, c);
                if (c.DistanceSquaredTo(hit) <= r * r)
                {
                    _effects.SpawnEffect(new HitFlash(), hit, localSector);
                    SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, hit, pitch: 0.92f + GD.Randf() * 0.16f);
                    pv.QueueFree();
                    _bolts.RemoveAt(i);
                    consumed = true;
                    break;
                }
            }
            if (consumed)
                continue;

            // An aleph is a solid barrier: the server already stopped the shot with no damage
            // (Simulation.FireBolt), so mirror that visually by absorbing the tracer at the gate mouth — no
            // spark, it just vanishes into the vortex. Team-agnostic like ships/probes.
            foreach (var node in _aleph.Nodes.Values)
            {
                if (!node.Visible)
                    continue;
                Vector3 c = node.GlobalPosition;
                Vector3 hit = ClosestPointOnSegment(a, b, c);
                if (c.DistanceSquaredTo(hit) <= AlephView.BlockRadius * AlephView.BlockRadius)
                {
                    pv.QueueFree();
                    _bolts.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // Cull bolts whose (obstruction-clipped) flight life has elapsed. A bolt whose TTL was clipped against a
    // base's visible surface sparks + sounds there before it frees — the same client-side interception
    // CheckBoltImpacts does for ships, at the stored impact point in the bolt's own sector (not necessarily
    // the local one). Bolts that simply outran their flight in open space (ImpactAtExpiry false) expire
    // silently, as before.
    public void CullTick()
    {
        for (int i = _bolts.Count - 1; i >= 0; i--)
        {
            var pv = _bolts[i];
            if (pv.Expired)
            {
                if (pv.ImpactAtExpiry)
                {
                    _effects.SpawnEffect(new HitFlash(), pv.ImpactPoint, pv.Sector);
                    SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, pv.ImpactPoint, pitch: 0.92f + GD.Randf() * 0.16f);
                }
                pv.QueueFree();
                _bolts.RemoveAt(i);
            }
        }
    }

    // Closest point to p on segment [a, b], clamped to the endpoints.
    private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f)
            return a;
        return a + ab * Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
    }

    // World rebuild: the bolt nodes are freed by the _projectiles QueueFree sweep; drop our tracking list.
    public void Clear() => _bolts.Clear();
}
