using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Mines & fields (TRACK B). One deploy scatters a seed-derived cloud of up to CloudCount mine
// MESHES behind the ship; the field arms after MineArmTicks and expires after ProjectileLifeTicks.
// The field is ONE damage VOLUME, not N hit-detected mines — the scattered meshes are cosmetic
// (client-side only). Once armed, every ENEMY non-pod ship inside the cloud-radius sphere takes
// BlastPower damage-per-second SCALED BY ITS OWN SPEED (mines are static, so a fast plow-through
// hurts and a near-stationary ship takes ~0), every tick, until the field expires. AliveMask never
// depletes; a rate-limited MsgMineGone(reason=2) ping drives the client's small hit explosion+pop.
// Wired into Step() via Pass A (input.DropMine -> TryDeployMine) and StepMines (after StepMissiles).
public sealed partial class Simulation
{
    // Speed the field deals its full authored DPS at (u/s); a victim's damage scales speed/ref.
    private const float MineSpeedRef = 120f;
    // Cap on the speed multiplier — a blisteringly fast pass can't exceed this × the authored DPS.
    private const float MineMaxSpeedMult = 2.5f;
    // Min ticks between hit-FX pings for one victim (~0.4s at 20 Hz) so a plow-through pops a few
    // explosions rather than one every tick.
    private const uint MineFxIntervalTicks = 8;

    // A deployed minefield: a pseudo-random cloud of up to CloudCount mines whose LOCAL offsets are
    // regenerated from Seed via the shared MinefieldLayout (server sim + client agree). AliveMask
    // tracks which mines are still live (CloudCount capped at 64). Streamed per anchor-sector.
    public sealed class MineFieldSim
    {
        public ulong FieldId;
        public uint WeaponId; // mine-kind WeaponDef (blast / arm / trigger / cloud)
        public byte Team; // owner team (a mine only triggers on an ENEMY non-pod)
        public uint SectorId;
        public Vec3 Center;
        public uint Seed; // MinefieldLayout offset seed
        public uint ArmAtTick; // deploy tick + MineArmTicks
        public uint ExpireAtTick; // deploy tick + ProjectileLifeTicks
        public ulong AliveMask; // bit i set = mine i still live

        // World-space position of each mine (Center + MinefieldLayout local offset), computed once at
        // deploy so the proximity scan never re-derives them. Length == the initial popcount of
        // AliveMask (n = min(MineAmmo, MineCloudCount)); the client regenerates the same array from
        // Seed + Center via MinefieldLayout.Positions, so these are authoritative-but-not-wired.
        public Vec3[] MinePos = System.Array.Empty<Vec3>();
    }

    // Live minefields (deployed by TryDeployMine, stepped in StepMines). Exposed to the hub for the
    // per-anchor-sector MsgMinefields stream via Simulation.Minefields.
    private readonly List<MineFieldSim> _minefields = new();

    // Deploy a minefield behind this ship (ammo + cadence gated, mirroring TryFireMissile's held-input
    // debounce). One deploy consumes ONE mine-cargo unit and lays ONE field of MineCloudCount COSMETIC
    // meshes (the meshes are decoration, not per-mine ammo — the whole field is one damage volume). The
    // Seed is derived deterministically from the field id (no RNG) so server + client + a replay all
    // regenerate the identical cloud.
    private void TryDeployMine(ShipSim ship, uint tick)
    {
        if (ship.MineAmmo == 0 || ship.MineWeaponId == 0)
            return;
        if (!WeaponDefs.TryGetValue(ship.MineWeaponId, out var w))
            return;
        // Authoritative cadence gate (the debounce for held-input replay — never client-edge-detect).
        if (ship.LastMineTick != 0 && tick - ship.LastMineTick < w.FireIntervalTicks)
            return;

        // Mesh count is the authored cosmetic density, capped at the 64-bit aliveMask; it is NOT tied to
        // how many mines the ship carries. One deploy costs exactly one mine-cargo unit.
        int n = w.MineCloudCount > 64 ? 64 : w.MineCloudCount;
        if (n <= 0)
            return;

        ship.MineAmmo -= 1;
        ship.LastMineTick = tick;

        ulong fieldId = _nextShipId++;
        // Just aft of the ship (local -Z): CloudRadius + a small hull clearance back, so the cluster
        // sits right behind the tail rather than far downrange.
        Vec3 aft = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f)) * -1f;
        Vec3 center = ship.State.Pos + aft * (w.MineCloudRadius + 4f);

        // Deterministic uint seed from (fieldId, World.Seed) — the wire carries this exact value, so
        // the client regenerates the same offsets. Hash01 gives a stable [0,1) roll; scale to a uint.
        uint seed = (uint)(MinefieldLayout.Hash01(fieldId, World.Seed) * 4294967295.0);
        ulong mask = n >= 64 ? ulong.MaxValue : (1UL << n) - 1UL;

        var pos = new Vec3[n];
        MinefieldLayout.Positions(seed, n, w.MineCloudRadius, pos);
        for (int i = 0; i < n; i++)
            pos[i] = pos[i] + center;

        _minefields.Add(
            new MineFieldSim
            {
                FieldId = fieldId,
                WeaponId = w.WeaponId,
                Team = ship.Team,
                SectorId = ship.SectorId,
                Center = center,
                Seed = seed,
                ArmAtTick = tick + w.MineArmTicks,
                ExpireAtTick = tick + w.ProjectileLifeTicks,
                AliveMask = mask,
                MinePos = pos,
            }
        );
        MinefieldsChangedThisStep = true;
    }

    // Expire fields + damage every enemy inside an armed field's volume. Fixed grid-cube iteration
    // order keeps this bit-identical across a replay (mirrors ApplyBlast's cube walk). Removal-safe
    // index loop: an expired field is dropped and the clients prune it from the next (possibly empty)
    // MsgMinefields frame.
    private void StepMines(uint tick)
    {
        for (int fi = 0; fi < _minefields.Count; fi++)
        {
            var field = _minefields[fi];

            // Field-level teardown: past its lifespan.
            if (tick >= field.ExpireAtTick)
            {
                _minefields.RemoveAt(fi);
                fi--;
                MinefieldsChangedThisStep = true;
                continue;
            }

            // Still arming — the volume is inert until ArmAtTick.
            if (tick < field.ArmAtTick)
                continue;

            DamageFieldVolume(field, tick);
        }
    }

    // The armed field is ONE lethal sphere (center + MineCloudRadius). Damage every ENEMY non-pod
    // ship inside it this tick by BlastPower (damage/sec at MineSpeedRef) scaled by the victim's own
    // speed — a stationary ship takes ~0, a fast plow-through takes the capped max. AliveMask is left
    // untouched (the meshes are cosmetic); a rate-limited MsgMineGone(reason=2) ping per victim drives
    // the client's small hit explosion + pop. Fixed grid-cube order = replay-deterministic.
    private void DamageFieldVolume(MineFieldSim field, uint tick)
    {
        var w = WeaponDefs[field.WeaponId];
        var grid = _shipGrid.TryGetValue(field.SectorId, out var sg) ? sg : null;
        float radius = w.MineCloudRadius;
        if (grid is null || radius <= 0f)
            return;

        float radiusSq = radius * radius;
        Vec3 c = field.Center;
        int x0 = World.CellOf(c.X - radius),
            x1 = World.CellOf(c.X + radius);
        int y0 = World.CellOf(c.Y - radius),
            y1 = World.CellOf(c.Y + radius);
        int z0 = World.CellOf(c.Z - radius),
            z1 = World.CellOf(c.Z + radius);
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!grid.TryGetValue((cx, cy, cz), out var shipsInCell))
                        continue;
                    foreach (var s in shipsInCell)
                    {
                        if (s.Team == field.Team || !s.Alive || s.IsPod)
                            continue;
                        if ((s.State.Pos - c).LengthSquared() > radiusSq)
                            continue;

                        // Speed-scaled damage. Static mines: only a MOVING ship gets hurt, and faster
                        // hurts more (up to the cap). A parked ship inside the cloud takes nothing.
                        float speed = s.State.Vel.Length();
                        float mult = speed / MineSpeedRef;
                        if (mult > MineMaxSpeedMult)
                            mult = MineMaxSpeedMult;
                        float dmg = w.BlastPower * mult * FlightModel.Dt;
                        if (dmg <= 0f)
                            continue;
                        ApplyDamage(s, dmg, tick, w.ShieldMult);

                        // Rate-limited hit-FX ping: a small explosion + pop at the nearest cosmetic
                        // mine (a mine "near you" going off). Doesn't deplete anything.
                        if (s.LastMineFxTick == 0 || tick - s.LastMineFxTick >= MineFxIntervalTicks)
                        {
                            s.LastMineFxTick = tick;
                            Vec3 fxPos = NearestMinePos(field, s.State.Pos);
                            MineGoneThisStep.Add((field.FieldId, 0, 2, field.SectorId, fxPos));
                        }
                    }
                }
    }

    // Nearest scattered mine position to `p` (the field's cosmetic meshes), for placing the hit-FX
    // ping. Falls back to the field center when the cloud is empty.
    private static Vec3 NearestMinePos(MineFieldSim field, Vec3 p)
    {
        Vec3 best = field.Center;
        float bestDsq = float.MaxValue;
        foreach (var mp in field.MinePos)
        {
            float dsq = (mp - p).LengthSquared();
            if (dsq < bestDsq)
            {
                bestDsq = dsq;
                best = mp;
            }
        }
        return best;
    }
}
