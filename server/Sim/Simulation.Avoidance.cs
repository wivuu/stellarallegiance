using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Obstacle-avoidance steering fields — the `avoid` delegates injected into the shared AutoSteer
// geometry by every synthesized-input flyer (PIG combat brain, player autopilot, miners,
// constructors). Each pass bends a desired flight direction around solid obstacles ahead by
// summing a perpendicular push per obstacle, scaled by how soon (proj/lookahead) and how squarely
// (perp/clearance) the current line pierces it. Tuning comes from the PIG knobs (world.yaml `ai:`
// avoid-lookahead / avoid-margin) — one authored avoidance feel for every AI flyer.
//
// All of this is SERVER-ONLY steering: clients never predict AI-driven ships (Simulation.Pig.cs
// banner), so there is no cross-runtime determinism contract here — the behavioral AI suites
// (Aleph/Rescue/Missile/Mine/Fog/Autopilot) are the guard on any tuning change. AvoidRocks is
// the arithmetic of the original PIG asteroid avoidance, moved verbatim. Every flyer that flies
// AT an obstacle on purpose must exclude it (excludeRockId/excludeBaseId) or the push fights the
// approach — dock targets, keep-station anchors, mining claims, attack-standoff bases.
public sealed partial class Simulation
{
    // Bend desiredDir around asteroids lying ahead within the lookahead distance.
    // excludeRockId (default 0 = none) skips one rock from the avoidance scan — used by a miner flying
    // AT / harvesting a rock, so the avoid deflection never swings its nose off the very rock it's
    // heading for. Default keeps every combat-PIG caller byte-identical (PIG-determinism guarded).
    private Vec3 AvoidRocks(uint sector, Vec3 pos, Vec3 desiredDir, ulong excludeRockId = 0)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = default;
        var grid = World.RockGrid(sector);
        int cx = World.CellOf(pos.X),
            cy = World.CellOf(pos.Y),
            cz = World.CellOf(pos.Z);
        for (int gx = cx - 1; gx <= cx + 1; gx++)
        for (int gy = cy - 1; gy <= cy + 1; gy++)
        for (int gz = cz - 1; gz <= cz + 1; gz++)
        {
            if (!grid.TryGetValue((gx, gy, gz), out var cell))
                continue;
            foreach (var rock in cell)
            {
                if (rock.Id == excludeRockId)
                    continue; // don't avoid the rock we're flying at / mining
                Vec3 toA = rock.Pos - pos;
                float proj = Dot(toA, dir);
                if (proj <= 0f || proj > PigAvoidLookahead)
                    continue;
                Vec3 closest = pos + dir * proj;
                Vec3 off = closest - rock.Pos;
                // Deliberately the SPAWN radius (not RockCurrentRadius): steering wider around a mined
                // rock is harmless (extra clearance), and this runs inside the PIG-determinism-guarded
                // brain — reading live ore state here would couple avoidance to harvest timing. v1 keeps
                // PIG avoidance at spawn size (conservative, documented).
                float clearance = rock.Radius + World.ShipRadius + PigAvoidMargin;
                float perp = off.Length();
                if (perp >= clearance)
                    continue;
                Vec3 pushDir = NormalizeOr(off, PerpendicularTo(dir));
                float strength = (1f - proj / PigAvoidLookahead) * (1f - perp / clearance);
                steer += pushDir * strength;
            }
        }
        if (steer.LengthSquared() < 1e-8f)
            return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    // Bend desiredDir around base hull spheres lying ahead — the same push-field SHAPE as AvoidRocks,
    // applied to World.Bases in the sector (every base is solid to every ship: enemy hulls bounce
    // outright, own bases bounce everywhere but the docking doors, and destroyed bases keep their slot
    // AND their collision, so no health filter here). Three deltas from the rock pass, all because a
    // base is an order of magnitude FATTER than a rock:
    //   - a DOUBLED lookahead window, measured to the base SURFACE (2*PigAvoidLookahead + radius to
    //     the center) — the rock formula's center-measured window would not react until the ship is
    //     nearly touching the hull, and even surface-measured, one rock-lookahead of warning is not
    //     enough room for a full-speed ship to displace a ~120-unit clearance shell sideways;
    //   - NO (1 - proj/lookahead) distance ramp — that ramp makes the push near-zero exactly where a
    //     fat obstacle needs it most (far out, where a fast ship still has room to bend a trajectory
    //     100+ units sideways); the push is full-strength the moment the line pierces the sphere and
    //     fades only as the line clears it (perp -> clearance);
    //   - a fatter clearance shell (an extra radius/5) — trajectory lags the nose, so a ship riding
    //     the shell boundary dips inside it; the rock margin alone (14) sits too close to the hull.
    // excludeBaseId skips the base the caller is deliberately flying at (dock target / keep-station
    // anchor) so avoidance never fights an intentional approach — the dock maneuver's own detour
    // geometry (DockApproach) and the arrival stop shells own that base's clearance.
    private Vec3 AvoidBases(uint sector, Vec3 pos, Vec3 desiredDir, ulong excludeBaseId)
    {
        Vec3 dir = NormalizeOr(desiredDir, new Vec3(0f, 0f, 1f));
        Vec3 steer = default;
        foreach (var b in World.Bases)
        {
            if (b.SectorId != sector || b.Id == excludeBaseId)
                continue;
            float radius = World.BaseRadiusOf(b.BaseTypeId);
            float lookahead = PigAvoidLookahead * 2f + radius;
            Vec3 toB = b.Pos - pos;
            float proj = Dot(toB, dir);
            if (proj <= 0f || proj > lookahead)
                continue;
            Vec3 closest = pos + dir * proj;
            Vec3 off = closest - b.Pos;
            float clearance = radius + World.ShipRadius + PigAvoidMargin + radius * 0.2f;
            float perp = off.Length();
            if (perp >= clearance)
                continue;
            Vec3 pushDir = NormalizeOr(off, PerpendicularTo(dir));
            float strength = 1f - perp / clearance;
            steer += pushDir * strength;
        }
        if (steer.LengthSquared() < 1e-8f)
            return dir;
        return NormalizeOr(dir + steer * 1.5f, dir);
    }

    // The full obstacle field for every AI flyer (PIGs, player autopilot, miners, constructors):
    // rocks (minus the one being flown at) then base hulls (minus the one being docked at /
    // station-kept on / shelled from standoff).
    private Vec3 AvoidObstacles(uint sector, Vec3 pos, Vec3 desiredDir, ulong excludeRockId = 0, ulong excludeBaseId = 0) =>
        AvoidBases(sector, pos, AvoidRocks(sector, pos, desiredDir, excludeRockId), excludeBaseId);
}
