using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;
using static StellarAllegiance.Shared.Vec3;

namespace SimServer.Sim;

// Primary (gun) fire and the projectile-resolution pipeline behind it: the per-mount cadence gate
// (TryFire), the per-bolt analytic first-entry cast over the sector grid (FireBolt), the base-damage
// sink + win-condition/match-end latch both projectile paths feed (ApplyBaseDamage), the deferred
// shot ring that applies a bolt at its impact tick (ResolveDueShots), and the shared ray/ship-grid
// helpers (FirstEntryTime / CellsAlongRay / RebuildShipGrid) the missile and vision passes also use.
// Split out of Simulation.cs on 2026-09-09 — a pure move: no behaviour, order, or signature change.

public sealed partial class Simulation
{
    // ---- Firing (module TryFire: analytic first-entry solve over grid ray walk) ----

    private void TryFire(ShipSim ship, uint tick)
    {
        var muzzles = ship.Class < ClassMuzzles.Length ? ClassMuzzles[ship.Class] : System.Array.Empty<Muzzle>();
        if (muzzles.Length == 0)
            return; // no authored weapon hardpoint ⇒ this hull doesn't fire (e.g. a pod)

        // Primary fire is the GUNS, and each gun mount gates on its OWN cadence (mixed loadouts):
        // FireCadence.MountFires is THE shared eligibility rule — the client derives WHICH mounts
        // fired at a replicated LastFireTick by replaying it against a per-ship shadow, so the
        // wire carries no per-mount data. TryGetValue skips an empty/unbound mount (WeaponId ==
        // HardpointDef.NoWeapon never resolves); missile racks have their own cadence in
        // TryFireMissile. IMPORTANT: the loop still visits every weapon hardpoint and KEEPS the
        // array index as `barrel` (the per-barrel spread seed) — skipped slots consume their
        // index, so gun seeds stay aligned with the client (SpawnBoltFor/PredictionController)
        // regardless of where racks or emptied slots sit in the array.
        bool fired = false;
        for (byte barrel = 0; barrel < muzzles.Length; barrel++)
        {
            if (!WeaponDefs.TryGetValue(WeaponIdAt(ship, barrel), out var w) || w.Kind != WeaponKind.Bolt)
                continue; // empty/unbound mount, or a rack (fired by Firing2, not primary fire)
            ship.MountLastFire ??= new uint[muzzles.Length];
            if (!FireCadence.MountFires(tick, ship.MountLastFire[barrel], w.FireIntervalTicks))
                continue;
            ship.MountLastFire[barrel] = tick;
            FireBolt(ship, tick, w, muzzles[barrel], barrel);
            fired = true;
        }
        if (fired)
            ship.LastFireTick = tick; // wire stamp: "a gun fired this tick" — clients derive which
    }

    // Cast one bolt from a single muzzle: spawn it at the hardpoint, walk the spatial grid for the
    // first hull/base/rock it enters, and queue the damage at the impact tick. The bolt direction
    // is seeded by (ShipId, fire tick, barrel), so the client renders the same bolt from the same
    // muzzle and the per-barrel scatter agrees on both sides.
    private void FireBolt(ShipSim ship, uint tick, WeaponDef w, in Muzzle muzzle, byte barrel)
    {
        Vec3 fwd = ship.State.Rot.Rotate(muzzle.Dir);
        Vec3 shotDir = FlightModel.SpreadDirection(fwd, w.SpreadRad, ship.ShipId, tick, barrel);
        Vec3 mp = ship.State.Pos + ship.State.Rot.Rotate(muzzle.Off);
        Vec3 mv = shotDir * w.ProjectileSpeed + ship.State.Vel;

        float maxT = w.ProjectileLifeTicks * FlightModel.Dt;
        float bestT = maxT;
        ulong targetShip = 0;
        int targetBase = -1;
        ulong targetProbe = 0;

        if (w.CanDamageBase)
        {
            for (int i = 0; i < World.Bases.Count; i++)
            {
                var b = World.Bases[i];
                if (b.SectorId != ship.SectorId || b.Team == ship.Team)
                    continue;
                bool hit = World.BaseHullOf(b.BaseTypeId) is not null
                    ? BaseHullsRayEntry(b.Pos, b.BaseTypeId, mp, mv, World.ProjectileRadius, bestT, out float t)
                    : FirstEntryTime(
                        mp,
                        mv,
                        b.Pos,
                        default,
                        World.BaseRadiusOf(b.BaseTypeId) + World.ProjectileRadius,
                        bestT,
                        out t
                    );
                if (hit && t < bestT)
                {
                    bestT = t;
                    targetBase = i;
                    targetShip = 0;
                }
            }
        }

        var shipGrid = _shipGrid.TryGetValue(ship.SectorId, out var sg) ? sg : null;
        var rockGrid = World.RockGrid(ship.SectorId);
        foreach (var cell in CellsAlongRay(mp, mv, bestT))
        {
            if (shipGrid is not null && shipGrid.TryGetValue(cell, out var shipsInCell))
            {
                foreach (var s in shipsInCell)
                {
                    if (!s.Alive)
                        continue;
                    // A HEALING gun (ER Nanite) targets SAME-team ships (to heal them) and skips the
                    // shooter itself; a normal gun targets the ENEMY and skips friendlies. Enemy hits by
                    // a heal bolt are a pure client-side spark (zero server effect), so the server simply
                    // never targets them here — the heal path can only ever resolve on a friendly.
                    if (w.IsHealing ? (s.Team != ship.Team || s.ShipId == ship.ShipId) : s.Team == ship.Team)
                        continue;
                    var body = World.ShipHull(s.Class, s.IsPod);
                    if (body is World.ShipBody sb)
                    {
                        // Bounding-sphere pre-test (accounts for the ship's drift via its velocity),
                        // then the ship's convex hull. The hull is static at the ship's current pose
                        // for the bolt's short flight; the ship's linear drift is folded into the ray
                        // by using the bolt-relative velocity (mv − ship velocity), exactly as the
                        // sphere FirstEntryTime uses the relative velocity.
                        float br = sb.BoundingRadius + World.ProjectileRadius;
                        if (!FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, br, bestT, out _))
                            continue;
                        Vec3 vrel = mv - s.State.Vel;
                        if (
                            HullRayEntry(
                                sb.Hull,
                                s.State.Pos,
                                s.State.Rot,
                                1f,
                                mp,
                                vrel,
                                World.ProjectileRadius,
                                bestT,
                                out float th
                            )
                            && th < bestT
                        )
                        {
                            bestT = th;
                            targetShip = s.ShipId;
                            targetBase = -1;
                        }
                    }
                    else
                    {
                        float r = World.ShipRadius + World.ProjectileRadius;
                        if (FirstEntryTime(mp, mv, s.State.Pos, s.State.Vel, r, bestT, out float t) && t < bestT)
                        {
                            bestT = t;
                            targetShip = s.ShipId;
                            targetBase = -1;
                        }
                    }
                }
            }
            if (rockGrid.TryGetValue(cell, out var rocks))
            {
                foreach (var a in rocks)
                {
                    // Resolution radius = the rock's CURRENT (mined-down) surface; a mined He3 rock
                    // stops a bolt only at its live shell. The broad-phase pre-test stays at the SPAWN
                    // radius (conservative — rocks only shrink, so it never rejects a real hit; the hull
                    // path below tests the live-scaled body, the sphere fallback tests the live `r`).
                    float r = World.RockCurrentRadius(a.Id) * World.AsteroidCollisionScale + World.ProjectileRadius;
                    if (!FirstEntryTime(mp, mv, a.Pos, default, a.Radius + World.ProjectileRadius, bestT, out _))
                        continue;
                    bool hit = World.RockBodies.TryGetValue(a.Id, out var body)
                        ? HullRayEntry(
                            body.Hull,
                            a.Pos,
                            body.Rot,
                            body.Scale,
                            mp,
                            mv,
                            World.ProjectileRadius,
                            bestT,
                            out float t
                        )
                        : FirstEntryTime(mp, mv, a.Pos, default, r, bestT, out t);
                    if (hit && t < bestT)
                    {
                        bestT = t;
                        targetShip = 0;
                        targetBase = -1; // stopped by a rock
                    }
                }
            }
        }

        // Alephs are solid barriers to weapon fire: a gate mouth absorbs a bolt with no damage target.
        // The mouth's known extent is its warp-trigger radius (a ship warps at that distance, so it
        // never reaches the barrier — only projectiles are stopped). Few gates per sector, not in any
        // grid → a linear scan (like the probe scan below) is cheap and replay-deterministic in list
        // order. A closer aleph hit wins and clears any target.
        float alephR = _mech.AlephTriggerRadius + World.ProjectileRadius;
        for (int i = 0; i < World.Alephs.Count; i++)
        {
            var g = World.Alephs[i];
            if (g.SectorId != ship.SectorId)
                continue;
            if (FirstEntryTime(mp, mv, g.Pos, default, alephR, bestT, out float at) && at < bestT)
            {
                bestT = at;
                targetShip = 0;
                targetBase = -1;
                targetProbe = 0; // stopped by an aleph
            }
        }

        // Deployed enemy probes are destructible: a stationary hit-sphere scan (probes are few and
        // not in the ship grid, so a linear scan is cheap and stays replay-deterministic in list
        // order). A closer probe hit wins and clears any ship/base target.
        for (int i = 0; i < _probes.Count && !w.IsHealing; i++) // a healing gun never targets enemy probes
        {
            var p = _probes[i];
            if (p.SectorId != ship.SectorId || p.Team == ship.Team || p.Health <= 0f)
                continue;
            float pr = (WeaponDefs.TryGetValue(p.WeaponId, out var pw) ? pw.ProbeHitRadius : 0f) + World.ProjectileRadius;
            if (pr > World.ProjectileRadius && FirstEntryTime(mp, mv, p.Pos, default, pr, bestT, out float pt) && pt < bestT)
            {
                bestT = pt;
                targetProbe = p.ProbeId;
                targetShip = 0;
                targetBase = -1;
            }
        }

        if (targetShip != 0 || targetBase >= 0 || targetProbe != 0)
        {
            uint resolveTicks = Math.Max(1u, (uint)MathF.Ceiling(bestT / FlightModel.Dt));
            // GunDamage (v41): scale by the shooter team's faction multiplier. Applies to healing bolts
            // too — Iron's better guns heal harder (ApplyHeal reads this same Damage field as heal power),
            // consistent + simpler than a separate heal path. Bakes into PendingShot so the base-damage
            // path (ApplyBaseDamage via shot.Damage) inherits it as well.
            float dmg = w.Damage * TeamAttr(ship.Team, Allegiance.Factions.Model.GameAttribute.GunDamage);
            _shotRing[(tick + resolveTicks) % ShotRingSize]
                .Add(
                    new PendingShot(targetShip, targetBase, dmg, w.ShieldMult, targetProbe, w.IsHealing, ship.OwnerClientId)
                );
        }
    }

    // The BaseDef for a live base's type (garrison 0, outpost 1, …); null if the content has none.
    private Dictionary<byte, BaseDef>? _baseDefByType;

    private BaseDef? BaseDefForType(byte typeId)
    {
        if (_baseDefByType is null)
        {
            _baseDefByType = new Dictionary<byte, BaseDef>();
            foreach (var d in Content.Bases)
                _baseDefByType[d.BaseTypeId] = d;
        }
        return _baseDefByType.TryGetValue(typeId, out var def) ? def : null;
    }

    // A base type is a win-condition ("headquarters") base when its def carries WinCondition (the
    // `start` ability — only the garrison, in stock content). A team loses when ALL its WinCondition
    // bases are destroyed; a forward base (outpost) at 0 HP never ends the match.
    private bool IsWinConditionBase(byte typeId) => BaseDefForType(typeId)?.WinCondition ?? false;

    // Does `team` still hold a live WinCondition base (optionally excluding one index)?
    private bool TeamHasAliveWinBase(byte team, int excludeIndex)
    {
        for (int i = 0; i < World.Bases.Count; i++)
            if (
                i != excludeIndex
                && World.Bases[i].Team == team
                && World.BaseHealth[i] > 0f
                && IsWinConditionBase(World.Bases[i].BaseTypeId)
            )
                return true;
        return false;
    }

    // Apply damage to a base (health floor at 0), flag it as changed/dirty, and — when a team's LAST
    // win-condition (headquarters) base drops to 0 — latch the match end (winner = the OTHER team) and
    // schedule the return-to-lobby. Destroying a forward base (outpost) removes it from play but never
    // ends the match. Shared by the bolt path (ResolveDueShots) and missile detonation (StepMissiles).
    // attackerClientId is the pilot behind the hit (-1 = a PIG / nobody): it stamps base kill credit
    // exactly the way ApplyDamage stamps a hull's, and the killing blow is scored below.
    private void ApplyBaseDamage(int baseIndex, float damage, uint tick, int attackerClientId = -1)
    {
        bool wasAlive = World.BaseHealth[baseIndex] > 0f;
        float hp = MathF.Max(0f, World.BaseHealth[baseIndex] - damage);
        World.BaseHealth[baseIndex] = hp;
        BasesChangedThisStep = true;
        _matchDirty = true;
        // Kill credit for structures, keyed by the base's STABLE Id (World.Bases grows mid-match and
        // the whole World is swapped at StartMatch, so an index is not a durable key).
        if (attackerClientId >= 0)
            _baseLastHit[World.Bases[baseIndex].Id] = (attackerClientId, tick);
        if (hp <= 0f && wasAlive && Phase != PhaseEnded)
        {
            byte loser = World.Bases[baseIndex].Team;
            // Score the killing blow BEFORE the latch below: ScoreBaseKill guards on Phase ==
            // PhaseActive, and the garrison whose loss ENDS the match must still count.
            ScoreBaseKill(baseIndex, tick);
            // Only the loss of a team's FINAL win-condition base ends the match.
            if (IsWinConditionBase(World.Bases[baseIndex].BaseTypeId) && !TeamHasAliveWinBase(loser, baseIndex))
            {
                Winner = (byte)(loser == 0 ? 1 : 0);
                Phase = PhaseEnded;
                JustEnded = true;
                _returnToLobbyAtTick = tick + EndedToLobbyTicks;
            }
        }
    }

    private void ResolveDueShots(uint tick)
    {
        var due = _shotRing[tick % ShotRingSize];
        foreach (var shot in due)
        {
            if (shot.TargetProbeId != 0)
            {
                // The probe may have expired/been killed since the bolt was queued — skip if gone.
                if (FindDamageableProbe(shot.TargetProbeId) is { } probe)
                    DamageProbe(probe, shot.Damage, tick);
            }
            else if (shot.BaseIndex >= 0)
            {
                ApplyBaseDamage(shot.BaseIndex, shot.Damage, tick, shot.AttackerClientId);
            }
            else if (_ships.TryGetValue(shot.TargetShipId, out var s) && s.Alive)
            {
                if (shot.Heal)
                    // ER Nanite: FireBolt only ever queues a heal shot against a same-team ship, so
                    // restore hull here (clamp/no-shield inside ApplyHeal). Damage is the heal power.
                    ApplyHeal(s, shot.Damage);
                else
                    // Apply damage only; the end-of-step death/dock pass detects 0 health and
                    // ejects the pod / frees the slot — one death path, like the module.
                    ApplyDamage(s, shot.Damage, tick, shot.ShieldMult, shot.AttackerClientId);
            }
        }
        due.Clear();
    }

    private static bool FirstEntryTime(
        Vec3 shotPos,
        Vec3 shotVel,
        Vec3 targetPos,
        Vec3 targetVel,
        float radius,
        float maxT,
        out float t
    )
    {
        Vec3 d = targetPos - shotPos;
        Vec3 vrel = targetVel - shotVel;
        float a = vrel.LengthSquared();
        float b = 2f * Dot(d, vrel);
        float c = d.LengthSquared() - radius * radius;

        if (c <= 0f)
        {
            t = 0f;
            return true;
        }
        if (a < 1e-6f)
        {
            if (b >= -1e-6f)
            {
                t = 0f;
                return false;
            }
            t = -c / b;
        }
        else
        {
            float disc = b * b - 4f * a * c;
            if (disc < 0f)
            {
                t = 0f;
                return false;
            }
            t = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (t < 0f)
            {
                t = 0f;
                return false;
            }
        }
        return t <= maxT;
    }

    private IEnumerable<(int, int, int)> CellsAlongRay(Vec3 start, Vec3 vel, float maxT)
    {
        _rayCells.Clear();
        float dist = vel.Length() * maxT;
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / World.GridCell));
        for (int i = 0; i <= steps; i++)
        {
            Vec3 p = start + vel * (maxT * i / steps);
            int cx = World.CellOf(p.X),
                cy = World.CellOf(p.Y),
                cz = World.CellOf(p.Z);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                var key = (cx + dx, cy + dy, cz + dz);
                if (_rayCells.Add(key))
                    yield return key;
            }
        }
    }

    private void RebuildShipGrid()
    {
        _shipGrid.Clear();
        foreach (var s in _order)
        {
            if (!s.Alive)
                continue;
            if (!_shipGrid.TryGetValue(s.SectorId, out var grid))
                _shipGrid[s.SectorId] = grid = new Dictionary<(int, int, int), List<ShipSim>>();
            var key = (World.CellOf(s.State.Pos.X), World.CellOf(s.State.Pos.Y), World.CellOf(s.State.Pos.Z));
            if (!grid.TryGetValue(key, out var cell))
                grid[key] = cell = new List<ShipSim>();
            cell.Add(s);
        }
    }
}
