using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;
using static StellarAllegiance.Shared.Vec3;

namespace SimServer.Sim;

// Guided missiles: lock acquisition from the pilot's requested target (UpdateLock, including the
// being-locked threat warning), rack launch (TryFireMissile), the seeker/chaff substitution seam,
// turn-rate pursuit + segment sweep + detonation (StepMissiles), warhead splash (ApplyBlast), and the
// base-lock lookup shared by both (TryGetLockableBase).
// Split out of Simulation.cs on 2026-09-09 — a pure move: no behaviour, order, or signature change.

public sealed partial class Simulation
{
    // ---- Guided missiles (server-authoritative lock + launch + turn-rate pursuit) ----

    // Advance this ship's missile lock timer from its input's LockTargetId. A hull with no rack
    // never locks (early out). A valid target (alive enemy ship — NOT a pod — in the same sector,
    // within LockRange, inside the LockAngle nose cone) advances progress; anything else resets it.
    // Progress reaching LockTicks latches Locked. Also bakes the wire LockState byte.
    private void UpdateLock(ShipSim ship, in ShipInputState input, uint tick)
    {
        if (MissileMountFor(ship) is not (_, WeaponDef w))
            return; // no effective launcher on this ship (none authored, or rack emptied) — never locks

        ship.LockTargetId = input.LockTargetId; // mirror the client's requested target
        bool valid = false;
        ShipSim? threatTarget = null; // the ship being locked (A2 being-locked warning), if any
        if (GameContent.IsBaseLock(input.LockTargetId))
        {
            // Base lock: only a CanDamageBase weapon may lock a base at all (D3); otherwise
            // reuse the exact same range/cone test as a ship target, aimed at the base's pos.
            if (w.CanDamageBase && TryGetLockableBase(input.LockTargetId, ship.Team, ship.SectorId, out int bi))
            {
                Vec3 to = World.Bases[bi].Pos - ship.State.Pos;
                float d = to.Length();
                if (d > 1e-4f && d <= w.LockRange)
                {
                    Vec3 nose = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
                    if (Dot(nose, to * (1f / d)) >= MathF.Cos(w.LockAngleRad))
                        valid = true;
                }
            }
        }
        else if (
            input.LockTargetId != 0
            && _ships.TryGetValue(input.LockTargetId, out var t)
            && t.Alive
            && t.Team != ship.Team
            && !t.IsPod // locking a helpless pod is bad feel — pods are never lockable
            && t.SectorId == ship.SectorId
            // Fog of war: only a RADAR-detected foe is lockable (eyeball-tier / ghosts are not). An
            // in-flight missile keeps tracking a target that later fogs out — this gates ACQUISITION.
            && TeamRadarSees(ship.Team, t.ShipId)
        )
        {
            Vec3 to = t.State.Pos - ship.State.Pos;
            float d = to.Length();
            if (d > 1e-4f && d <= w.LockRange)
            {
                Vec3 nose = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
                if (Dot(nose, to * (1f / d)) >= MathF.Cos(w.LockAngleRad))
                {
                    valid = true;
                    threatTarget = t; // resolve its warning below, after ship.Locked updates this tick
                }
            }
        }

        if (valid)
        {
            ship.LockProgress++;
            if (ship.LockProgress >= w.LockTicks)
                ship.Locked = true;
        }
        else
        {
            ship.LockProgress = 0;
            ship.Locked = false;
        }

        // Being-locked warning (A2): raise the threat state on the TARGET ship so its wire record
        // carries the amber (locking) / red (locked) flag bits. Uses the freshly-updated ship.Locked
        // so the red banner fires the same tick the lock completes. A completed lock wins over a
        // merely-progressing one from another attacker (max, not overwrite). Base locks never warn.
        if (threatTarget is not null)
            threatTarget.ThreatLockState = ship.Locked ? (byte)2 : Math.Max(threatTarget.ThreatLockState, (byte)1);

        uint pct = w.LockTicks > 0 ? ship.LockProgress * 100u / w.LockTicks : 100u;
        if (pct > 100u)
            pct = 100u;
        ship.LockState = (byte)((ship.Locked ? 0x80 : 0) | (int)pct);
    }

    // Secondary fire (Firing2): launch one missile from the rack when armed and off cooldown.
    // A lock is NOT required — unlocked launches are dumbfire (no target, ballistic straight
    // out of the tube); a completed lock makes the round guided. Spawns at the mount
    // hardpoint's world pose (like FireBolt), inheriting ship velocity plus the missile's
    // initial boost along the mount forward. Appended directly to _missiles (safe — Pass A
    // iterates _order, not _missiles).
    private void TryFireMissile(ShipSim ship, uint tick)
    {
        if (MissileMountFor(ship) is not (Muzzle mount, WeaponDef w))
            return; // no effective rack (none authored, or emptied in the hangar)
        if (!ConsumeCargoCharge(ref ship.MissileAmmo, ref ship.LastMissileTick, tick, w))
            return; // empty rack, or still cycling/reloading

        Vec3 fwd = ship.State.Rot.Rotate(mount.Dir);
        Vec3 mp = ship.State.Pos + ship.State.Rot.Rotate(mount.Off);
        _missiles.Add(
            new MissileSim
            {
                MissileId = _nextShipId++,
                OwnerShipId = ship.ShipId,
                OwnerClientId = ship.OwnerClientId,
                Team = ship.Team,
                WeaponId = w.WeaponId,
                SectorId = ship.SectorId,
                Pos = mp,
                Vel = ship.State.Vel + fwd * w.ProjectileSpeed, // ProjectileSpeed = InitialSpeed
                // Guided only when the lock completed; LockTargetId alone is just the client's
                // REQUEST (mirrored every tick by UpdateLock) and must not steer a dumbfire.
                TargetShipId = ship.Locked ? ship.LockTargetId : 0,
                ExpireAtTick = tick + w.ProjectileLifeTicks,
            }
        );
    }

    // Resolve a missile's seeker target — the isolated chaff/flare substitution seam. Returns the
    // homed ship, or null (missile coasts ballistic) when the target is dead / friendly / a pod /
    // in another sector / gone.
    private ShipSim? ResolveSeekerTarget(MissileSim mis)
    {
        if (mis.TargetShipId == 0 || !_ships.TryGetValue(mis.TargetShipId, out var t))
            return null;
        if (!t.Alive || t.Team == mis.Team || t.IsPod || t.SectorId != mis.SectorId)
            return null;
        return t;
    }

    // Step every in-flight missile: steer (turn-rate-limited pure pursuit) + accelerate, sweep this
    // tick's segment for the first enemy ship (skip owner, friendlies, pods) or rock, detonate
    // (direct Damage * DirectHitMult on the fuse-triggering ship + ApplyBlast splash around the
    // detonation point) or expire. Deterministic f32 math, no RNG. Between Pass A and Pass C.
    // Removals collected then applied post-loop.
    private void StepMissiles(uint tick, float dt)
    {
        if (_missiles.Count == 0)
            return;

        List<MissileSim>? remove = null;
        foreach (var mis in _missiles)
        {
            var w = WeaponDefs[mis.WeaponId];

            // (1) aim point (chaff seam for ships; base-lock resolves to the base's pos, else the
            // missile coasts unguided — D4) → (2) steer + accelerate. ResolveSeekerTarget stays the
            // untouched chaff substitution seam.
            Vec3? aimPos = null;
            bool chaffDetonate = false;
            Vec3 chaffDetonatePos = default;
            // Chaff substitution seam (Track A fills TryChaffAim; the Track-0 stub returns false so a
            // seeker behaves exactly as before). A decoyed missile homes on the puff, and once it
            // reaches the puff `detonateAtChaff` forces a proximity detonation at the chaff position.
            if (TryChaffAim(mis, out Vec3 chaffAim, out bool detonateAtChaff))
            {
                aimPos = chaffAim;
                chaffDetonate = detonateAtChaff;
                chaffDetonatePos = chaffAim;
            }
            else if (GameContent.IsBaseLock(mis.TargetShipId))
            {
                if (TryGetLockableBase(mis.TargetShipId, mis.Team, mis.SectorId, out int aimBase))
                    aimPos = World.Bases[aimBase].Pos;
            }
            else if (ResolveSeekerTarget(mis) is ShipSim tg)
            {
                aimPos = tg.State.Pos;
            }
            float speed = mis.Vel.Length();
            Vec3 dir = speed > 1e-4f ? mis.Vel * (1f / speed) : new Vec3(0f, 0f, 1f);
            if (aimPos is Vec3 ap)
            {
                Vec3 to = ap - mis.Pos;
                float d = to.Length();
                if (d > 1e-4f)
                    dir = TurnToward(dir, to * (1f / d), w.MissileTurnRateRad * dt);
            }
            float newSpeed = speed + w.MissileAccel * dt;
            if (w.MissileMaxSpeed > 0f && newSpeed > w.MissileMaxSpeed)
                newSpeed = w.MissileMaxSpeed;
            Vec3 vel = dir * newSpeed;
            mis.Vel = vel;

            // Chaff detonation: TryChaffAim reported the missile is now within fuse range of the puff
            // it was decoyed onto — detonate here (splash only; no direct-hit ship) and drop it.
            if (chaffDetonate)
            {
                var cg = _shipGrid.TryGetValue(mis.SectorId, out var csg) ? csg : null;
                ApplyBlast(mis.Team, mis.OwnerClientId, w, chaffDetonatePos, 0, cg, tick, mis.SectorId);
                Events.MissileGone.Add((mis.MissileId, 1, mis.SectorId, chaffDetonatePos));
                (remove ??= new()).Add(mis);
                continue;
            }

            // (3) sweep the tick segment (mp + vel·t, t∈[0,dt]) against enemy bases + ships + rocks.
            // Bases go first (mirrors FireBolt's base-then-grid order): EVERY missile sweeps bases
            // regardless of lock, using the RAW hull/sphere test (no w.ProjectileRadius fuse-margin
            // inflation — D5) so a torpedo always registers an exact hull-surface contact, never a
            // near-miss short of the base. The ship/rock grid walk below then competes against the
            // (possibly already-reduced) bestT exactly as it did before bases existed; whichever is
            // closer wins and clears the other's hit slot.
            Vec3 mp = mis.Pos;
            float bestT = dt;
            ulong hitShip = 0;
            int hitBase = -1;
            bool detonate = false;
            for (int bi = 0; bi < World.Bases.Count; bi++)
            {
                var b = World.Bases[bi];
                if (b.SectorId != mis.SectorId || b.Team == mis.Team)
                    continue; // friendly bases are non-colliding, matching bolts
                bool bhit = World.BaseHullOf(b.BaseTypeId) is not null
                    ? BaseHullsRayEntry(b.Pos, b.BaseTypeId, mp, vel, 0f, bestT, out float bt)
                    : FirstEntryTime(mp, vel, b.Pos, default, World.BaseRadiusOf(b.BaseTypeId), bestT, out bt);
                if (bhit && bt < bestT)
                {
                    bestT = bt;
                    hitBase = bi;
                    hitShip = 0;
                    detonate = true;
                }
            }
            var shipGrid = _shipGrid.TryGetValue(mis.SectorId, out var sg) ? sg : null;
            var rockGrid = World.RockGrid(mis.SectorId);
            foreach (var cell in CellsAlongRay(mp, vel, bestT))
            {
                if (shipGrid is not null && shipGrid.TryGetValue(cell, out var shipsInCell))
                {
                    foreach (var s in shipsInCell)
                    {
                        // Skip the owner, friendlies, dead ships, and pods (strays must not gib
                        // podded pilots; seekers only ever target ships anyway).
                        if (s.Team == mis.Team || !s.Alive || s.ShipId == mis.OwnerShipId || s.IsPod)
                            continue;
                        var body = World.ShipHull(s.Class, s.IsPod);
                        if (body is World.ShipBody sb)
                        {
                            float br = sb.BoundingRadius + w.ProjectileRadius;
                            if (!FirstEntryTime(mp, vel, s.State.Pos, s.State.Vel, br, bestT, out _))
                                continue;
                            Vec3 vrel = vel - s.State.Vel;
                            if (
                                HullRayEntry(
                                    sb.Hull,
                                    s.State.Pos,
                                    s.State.Rot,
                                    1f,
                                    mp,
                                    vrel,
                                    w.ProjectileRadius,
                                    bestT,
                                    out float th
                                )
                                && th < bestT
                            )
                            {
                                bestT = th;
                                hitShip = s.ShipId;
                                hitBase = -1;
                                detonate = true;
                            }
                        }
                        else
                        {
                            float r = World.ShipRadius + w.ProjectileRadius;
                            if (FirstEntryTime(mp, vel, s.State.Pos, s.State.Vel, r, bestT, out float t) && t < bestT)
                            {
                                bestT = t;
                                hitShip = s.ShipId;
                                hitBase = -1;
                                detonate = true;
                            }
                        }
                    }
                }
                if (rockGrid.TryGetValue(cell, out var rocks))
                {
                    foreach (var a in rocks)
                    {
                        // Pre-test at spawn radius (conservative broad-phase); resolve against the live
                        // (mined-down) surface — the hull path uses the live-scaled body, the sphere
                        // fallback uses `r` = current radius.
                        if (!FirstEntryTime(mp, vel, a.Pos, default, a.Radius + w.ProjectileRadius, bestT, out _))
                            continue;
                        float r = World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale + w.ProjectileRadius;
                        bool hit = World.RockBodies.TryGetValue(a.Id, out var rbody)
                            ? HullRayEntry(
                                rbody.Hull,
                                a.Pos,
                                rbody.Rot,
                                rbody.Scale,
                                mp,
                                vel,
                                w.ProjectileRadius,
                                bestT,
                                out float t
                            )
                            : FirstEntryTime(mp, vel, a.Pos, default, r, bestT, out t);
                        if (hit && t < bestT)
                        {
                            bestT = t;
                            hitShip = 0; // a rock kills the missile (no damage dealt)
                            hitBase = -1;
                            detonate = true;
                        }
                    }
                }
            }

            // Alephs are solid barriers: a gate mouth (sized by its warp-trigger radius) on the
            // segment stops the missile, which detonates on the barrier (blast splash still applies)
            // with no direct-hit target.
            float alephR = _mech.AlephTriggerRadius + w.ProjectileRadius;
            for (int i = 0; i < World.Alephs.Count; i++)
            {
                var g = World.Alephs[i];
                if (g.SectorId != mis.SectorId)
                    continue;
                if (FirstEntryTime(mp, vel, g.Pos, default, alephR, bestT, out float at) && at < bestT)
                {
                    bestT = at;
                    hitShip = 0;
                    hitBase = -1;
                    detonate = true; // an aleph stops the missile
                }
            }

            if (detonate)
            {
                Vec3 hitPos = mp + vel * bestT;
                // MissileDamage (v41): scale the warhead by the firing team's faction multiplier (direct
                // hit + base hit here; the splash inherits it inside ApplyBlast off the same mis.Team).
                float md = TeamAttr(mis.Team, Allegiance.Factions.Model.GameAttribute.MissileDamage);
                if (hitShip != 0 && _ships.TryGetValue(hitShip, out var victim) && victim.Alive)
                    ApplyDamage(victim, w.Damage * w.DirectHitMult * md, tick, w.ShieldMult, mis.OwnerClientId); // end-of-step death pass resolves 0 health
                else if (hitBase >= 0 && w.CanDamageBase)
                    ApplyBaseDamage(hitBase, w.Damage * w.DirectHitMult * md, tick, mis.OwnerClientId); // blast never touches the base
                ApplyBlast(mis.Team, mis.OwnerClientId, w, hitPos, hitShip, shipGrid, tick, mis.SectorId);
                Events.MissileGone.Add((mis.MissileId, 1, mis.SectorId, hitPos)); // impact
                (remove ??= new()).Add(mis);
                continue;
            }

            // (6) integrate, then expiry / world-boundary.
            mis.Pos = mp + vel * dt;
            if (tick >= mis.ExpireAtTick || mis.Pos.Length() > World.SectorRadius(mis.SectorId))
            {
                Events.MissileGone.Add((mis.MissileId, 0, mis.SectorId, mis.Pos)); // expired
                (remove ??= new()).Add(mis);
            }
        }

        if (remove is not null)
            foreach (var mis in remove)
                _missiles.Remove(mis);
    }

    // Warhead splash on detonation (ship OR rock impact): every enemy ship within BlastRadius of the
    // detonation point takes BlastPower, full inside the fuse radius (ProjectileRadius = authored
    // width) and inverse-square beyond it — falloff = (fuse/d)². The direct-hit victim is excluded
    // (it already took Damage * DirectHitMult); friendlies/pods never take splash, matching the
    // sweep's no-friendly-fire rule. Grid cube query keeps this off the O(ships) path; fixed
    // dx/dy/dz iteration order + one damage write per ship keeps it deterministic.
    // attackerClientId is the launching pilot (-1 for a PIG's warhead) — the splash credits exactly
    // what the direct hit would have, so a kill made by the blast rather than the impact still lands
    // on the shooter's row.
    private void ApplyBlast(
        byte team,
        int attackerClientId,
        WeaponDef w,
        Vec3 hitPos,
        ulong directHitShip,
        Dictionary<(int, int, int), List<ShipSim>>? shipGrid,
        uint tick,
        uint sector
    )
    {
        if (w.BlastRadius <= 0f || w.BlastPower <= 0f)
            return;
        float fuseR = w.ProjectileRadius;
        // MissileDamage (v41): the splash inherits the firing team's faction multiplier (same factor the
        // direct hit applied), so a whole detonation scales consistently.
        float md = TeamAttr(team, Allegiance.Factions.Model.GameAttribute.MissileDamage);

        // Enemy probes within the blast take splash too (same inverse-square falloff), so a missile
        // detonation is a valid probe counter. Probes aren't gridded — a linear scan (few probes) in
        // the detonation sector, deterministic in list order. Done before the ship grid so a probe
        // killed here is removed for any same-tick follow-up.
        for (int i = 0; i < _probes.Count; i++)
        {
            var p = _probes[i];
            if (p.SectorId != sector || p.Team == team || p.Health <= 0f)
                continue;
            float d = (p.Pos - hitPos).Length();
            if (d > w.BlastRadius)
                continue;
            float f = d <= fuseR ? 1f : (fuseR / d) * (fuseR / d);
            DamageProbe(p, w.BlastPower * f * md, tick);
        }

        if (shipGrid is null)
            return;
        int x0 = World.CellOf(hitPos.X - w.BlastRadius),
            x1 = World.CellOf(hitPos.X + w.BlastRadius);
        int y0 = World.CellOf(hitPos.Y - w.BlastRadius),
            y1 = World.CellOf(hitPos.Y + w.BlastRadius);
        int z0 = World.CellOf(hitPos.Z - w.BlastRadius),
            z1 = World.CellOf(hitPos.Z + w.BlastRadius);
        for (int cx = x0; cx <= x1; cx++)
        for (int cy = y0; cy <= y1; cy++)
        for (int cz = z0; cz <= z1; cz++)
        {
            if (!shipGrid.TryGetValue((cx, cy, cz), out var shipsInCell))
                continue;
            foreach (var s in shipsInCell)
            {
                if (s.Team == team || !s.Alive || s.IsPod || s.ShipId == directHitShip)
                    continue;
                float d = (s.State.Pos - hitPos).Length();
                if (d > w.BlastRadius)
                    continue;
                float falloff = d <= fuseR ? 1f : (fuseR / d) * (fuseR / d);
                ApplyDamage(s, w.BlastPower * falloff * md, tick, w.ShieldMult, attackerClientId); // end-of-step death pass resolves 0 health
            }
        }
    }

    // Rotate unit `dir` toward unit `desired` by at most `maxRad`. Linear blend + renormalize (a
    // small-angle slerp approximation, exact enough per tick); deterministic f32 (server-only).
    private static Vec3 TurnToward(Vec3 dir, Vec3 desired, float maxRad)
    {
        float dot = Dot(dir, desired);
        if (dot > 1f)
            dot = 1f;
        else if (dot < -1f)
            dot = -1f;
        float ang = MathF.Acos(dot);
        if (ang <= maxRad || ang < 1e-5f)
            return desired;
        float f = maxRad / ang;
        Vec3 blended = dir * (1f - f) + desired * f;
        float n = blended.Length();
        return n > 1e-6f ? blended * (1f / n) : desired;
    }

    // Find the enemy base this lock id refers to: `lockId` must decode (GameContent.BaseIdOf) to a
    // base on the OTHER team, in the ship's sector, still standing (BaseHealth > 0). Used by both
    // UpdateLock (ship-issued base locks) and StepMissiles (in-flight missile aim-point/collision).
    private bool TryGetLockableBase(ulong lockId, byte team, uint sectorId, out int baseIndex)
    {
        ulong id = GameContent.BaseIdOf(lockId);
        for (int i = 0; i < World.Bases.Count; i++)
        {
            var b = World.Bases[i];
            if (b.Id == id && b.Team != team && b.SectorId == sectorId && World.BaseHealth[i] > 0f)
            {
                baseIndex = i;
                return true;
            }
        }
        baseIndex = -1;
        return false;
    }
}
