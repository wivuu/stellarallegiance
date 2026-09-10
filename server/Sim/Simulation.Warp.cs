using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using SimServer.Content;
using StellarAllegiance.Shared;
using static StellarAllegiance.Shared.Vec3;

namespace SimServer.Sim;

// Aleph warp (module TryWarp): a ship inside a gate mouth's trigger radius emerges at the partner
// mouth along a jittered exit cone (server-authoritative RNG), re-pointed down that exit vector, with
// its arrival sector rock-scouted and revealed for its team. Carries the LookRotationZ helper it
// shares with the launch path.
// Split out of Simulation.cs on 2026-09-09 — a pure move: no behaviour, order, or signature change.

public sealed partial class Simulation
{
    // ---- Warp (module TryWarp): emerge out the partner mouth toward the dest sector
    // center, jittered by a small random cone so successive ships fan out instead of
    // stacking in a line. Server-authoritative RNG — clients read the result, never
    // reproduce it. The funnel discards heading; only raw speed carries through. ----

    private void TryWarp(ShipSim s)
    {
        foreach (var g in World.Alephs)
        {
            if (g.SectorId != s.SectorId)
                continue;
            float rr = _mech.AlephTriggerRadius + World.ShipRadius;
            if ((s.State.Pos - g.Pos).LengthSquared() > rr * rr)
                continue;

            float speed = s.State.Vel.Length();
            Vec3 mouth = g.PartnerPos * -1f; // toward the dest sector center (origin)
            float mlen = mouth.Length();
            Vec3 m = mlen > 0.001f ? mouth * (1f / mlen) : new Vec3(0f, 1f, 0f);

            // Jitter around the mouth axis (per-axis ±warp-exit-jitter), then renormalize so
            // ships emerging together spread into a cone rather than overlapping on one line.
            Vec3 e = new Vec3(
                m.X + (float)(_rng.NextDouble() * 2.0 - 1.0) * _mech.WarpExitJitter,
                m.Y + (float)(_rng.NextDouble() * 2.0 - 1.0) * _mech.WarpExitJitter,
                m.Z + (float)(_rng.NextDouble() * 2.0 - 1.0) * _mech.WarpExitJitter
            );
            float elen = e.Length();
            e = elen > 1e-4f ? e * (1f / elen) : m;

            float exit = _mech.AlephTriggerRadius + World.ShipRadius + _mech.WarpExitOffset;
            s.SectorId = g.DestSectorId;
            s.State.Pos = g.PartnerPos + e * exit;
            s.State.Vel = e * speed;
            // Emerge facing out of the aleph: point ship-local forward (+Z) along the
            // exit direction and drop any residual spin, so the ship comes through
            // pointed the way it's travelling instead of keeping its pre-warp heading.
            s.State.Rot = LookRotationZ(e);
            s.State.AngVel = default;
            // Fog: immediately scout the rocks around the arrival point (same tick) so gate-exit
            // surroundings reveal now instead of at the next 2 Hz vision boundary (Simulation.Vision.cs, F8).
            WarpDiscoverRocks(s);
            // Fog: a ship physically arriving in a sector reveals it (belt-and-braces — the gate
            // discovery usually already did via both aleph endpoints). Immediate write is safe
            // here, unlike DiscoveredRocks: the vision worker never reads DiscoveredSectors (only
            // the lock-holding Welcome/reveal builders do), so no _warpRevealPending deferral.
            if (FogEnabled && _teamVisions.TryGetValue(s.Team, out var wtv))
                lock (wtv.DiscoverLock)
                {
                    if (wtv.DiscoveredSectors.Add(g.DestSectorId))
                        wtv.RevealLogSectors.Add(g.DestSectorId);
                }
            return;
        }
    }

    // Shortest-arc rotation that aligns ship-local forward (+Z) with `dir` (unit),
    // with minimal roll. a=(0,0,1): cross(a,dir)=(-dir.Y,dir.X,0), dot(a,dir)=dir.Z.
    private static Quat LookRotationZ(Vec3 dir)
    {
        float d = dir.Z;
        // Antiparallel (facing -Z): the formula degenerates; spin 180° about X instead.
        if (d < -0.99999f)
            return new Quat(1f, 0f, 0f, 0f);
        return new Quat(-dir.Y, dir.X, 0f, 1f + d).Normalized();
    }
}
