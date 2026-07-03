using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Mines & fields (TRACK B). One deploy scatters a seed-derived cloud of up to CloudCount mines
// behind the ship; the field arms after MineArmTicks and expires after ProjectileLifeTicks. Each
// armed mine independently proximity-triggers on the nearest ENEMY non-pod ship within
// MineTriggerRadius, dealing BlastPower directly to it and the generalized ApplyBlast splash to
// every other enemy in range, then clears its AliveMask bit — the field depletes mine-by-mine.
// Wired into Step() via Pass A (input.DropMine -> TryDeployMine) and StepMines (after StepMissiles).
public sealed partial class Simulation
{
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
    // debounce). One deploy consumes n = min(MineAmmo, MineCloudCount) ammo and lays ONE field whose
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

        int cloudCap = w.MineCloudCount > 64 ? 64 : w.MineCloudCount;
        int n = ship.MineAmmo < cloudCap ? ship.MineAmmo : cloudCap;
        if (n <= 0)
            return;

        ship.MineAmmo -= (byte)n;
        ship.LastMineTick = tick;

        ulong fieldId = _nextShipId++;
        // Aft of the ship (local -Z), CloudRadius + 10u back so the cloud lands wholly behind the hull.
        Vec3 aft = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f)) * -1f;
        Vec3 center = ship.State.Pos + aft * (w.MineCloudRadius + 10f);

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

    // Expire/deplete fields + proximity-trigger armed mines against the ship grid. Fixed list/bit
    // iteration order keeps this bit-identical across a replay (mirrors ApplyBlast's cube walk).
    // Removal-safe index loop: an expired or fully-depleted field is dropped and the clients prune it
    // from the next (possibly empty) MsgMinefields frame — expiry never emits per-mine pop FX.
    private void StepMines(uint tick)
    {
        for (int fi = 0; fi < _minefields.Count; fi++)
        {
            var field = _minefields[fi];

            // Field-level teardown: past its lifespan, or every mine already consumed.
            if (tick >= field.ExpireAtTick || field.AliveMask == 0UL)
            {
                _minefields.RemoveAt(fi);
                fi--;
                MinefieldsChangedThisStep = true;
                continue;
            }

            // Still arming — the cloud is inert (no trigger, no splash) until ArmAtTick.
            if (tick < field.ArmAtTick)
                continue;

            var w = WeaponDefs[field.WeaponId];
            var grid = _shipGrid.TryGetValue(field.SectorId, out var sg) ? sg : null;

            int mineCount = field.MinePos.Length;
            for (int mi = 0; mi < mineCount; mi++)
            {
                ulong bit = 1UL << mi;
                if ((field.AliveMask & bit) == 0UL)
                    continue;

                Vec3 mp = field.MinePos[mi];
                ShipSim? victim = NearestMineTarget(field.Team, mp, w.MineTriggerRadius, grid);
                if (victim is null)
                    continue;

                // Direct full-power hit on the triggering ship, then inverse-square splash on every
                // OTHER enemy in blast range (ApplyBlast already excludes the direct victim + friendlies
                // + pods). One hit per triggered mine per tick — a ship plowing through eats the field.
                victim.Health -= w.BlastPower;
                ApplyBlast(field.Team, w, mp, victim.ShipId, grid);

                field.AliveMask &= ~bit;
                MineGoneThisStep.Add((field.FieldId, (byte)mi, 1, field.SectorId, mp));
                MinefieldsChangedThisStep = true;
            }
        }
    }

    // Nearest live ENEMY non-pod ship within `radius` of a mine, via the same fixed-order grid-cube
    // scan ApplyBlast uses (deterministic: strict-less keeps the first-encountered ship on an exact
    // distance tie). Friendlies + pods never trigger a mine. Null when nothing is in range.
    private ShipSim? NearestMineTarget(byte team, Vec3 minePos, float radius, Dictionary<(int, int, int), List<ShipSim>>? shipGrid)
    {
        if (shipGrid is null || radius <= 0f)
            return null;

        float radiusSq = radius * radius;
        ShipSim? best = null;
        float bestDsq = float.MaxValue;

        int x0 = World.CellOf(minePos.X - radius),
            x1 = World.CellOf(minePos.X + radius);
        int y0 = World.CellOf(minePos.Y - radius),
            y1 = World.CellOf(minePos.Y + radius);
        int z0 = World.CellOf(minePos.Z - radius),
            z1 = World.CellOf(minePos.Z + radius);
        for (int cx = x0; cx <= x1; cx++)
            for (int cy = y0; cy <= y1; cy++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!shipGrid.TryGetValue((cx, cy, cz), out var shipsInCell))
                        continue;
                    foreach (var s in shipsInCell)
                    {
                        if (s.Team == team || !s.Alive || s.IsPod)
                            continue;
                        float dsq = (s.State.Pos - minePos).LengthSquared();
                        if (dsq > radiusSq)
                            continue;
                        if (dsq < bestDsq)
                        {
                            bestDsq = dsq;
                            best = s;
                        }
                    }
                }
        return best;
    }
}
