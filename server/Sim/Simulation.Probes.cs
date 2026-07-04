using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Deployable recon probes (WP5 of the Fog plan). A probe is a passive, invulnerable, STATIONARY
// vision-sphere contributor: TryDeployProbe drops one just ahead of the deploying ship exactly
// like TryDropChaff/TryDeployMine (ammo + cadence gate mirrors those dispensers, riding the SAME
// cargo/ammo accounting — SeedDispenserAmmo/_dispenserByCargo), and StepProbes expires it after
// its authored lifespan (ProbeLifespanSec, already resolved into WeaponDef.ProjectileLifeTicks at
// content projection, same field missiles/mines/chaff use for their own lifespans).
//
// Probes carry no physics/collision and are never a combat target (v1 — "shootable probes
// deferred" per the plan); their only effect is an unoccluded team vision sphere of
// ProbeSightRadius, folded into Simulation.Vision.cs's existing ViewerSnap list (see
// CaptureVisionInput) — no new code path in the vision worker. They stream to the owning team
// ONLY (server/Net/ClientHub.cs), unconditionally (not gated on FogEnabled).
public sealed partial class Simulation
{
    // A deployed recon probe: stationary from the moment it's dropped (no velocity/integration).
    public sealed class ProbeSim
    {
        public ulong ProbeId;
        public byte Team; // owner team (the vision sphere it grants is team-scoped)
        public uint WeaponId; // probe-kind WeaponDef (ProbeSightRadius/ProbeLifespanSec/ModelName)
        public uint SectorId;
        public Vec3 Pos;
        public uint ExpireAtTick; // deploy tick + WeaponDef.ProjectileLifeTicks (ProbeLifespanSec × 20)
    }

    // Live probes (appended by TryDeployProbe, stepped in StepProbes). Exposed to the hub for the
    // owner-team-only MsgProbes stream.
    private readonly List<ProbeSim> _probes = new();
    public IReadOnlyList<ProbeSim> Probes => _probes;

    // Probes that expired/were cleared this step, drained by the hub into per-owner-team MsgProbeGone
    // frames. Reason 0 = expired past lifespan; reason 1 = match-clear cleanup/despawn (both rendered
    // as a silent removal client-side — probes are invulnerable, no impact/pop FX). Cleared at the top
    // of Step (mirrors MineGoneThisStep).
    public readonly List<(ulong id, byte reason, byte team, uint sector, Vec3 pos)> ProbeGoneThisStep = new();

    // Set whenever a probe was added/removed this step, so the hub sends a fresh (possibly empty)
    // per-team frame promptly instead of only on the coarse cadence (mirrors MinefieldsChangedThisStep).
    public bool ProbesChangedThisStep { get; private set; }

    // Deploy a stationary recon probe just ahead of the ship (ammo + cadence gated, mirroring
    // TryDropChaff/TryDeployMine's held-input debounce — the SERVER's cadence gate is the only
    // drop-input debounce; we never client-edge-detect). One deploy consumes ONE probe-cargo unit.
    private void TryDeployProbe(ShipSim ship, uint tick)
    {
        if (ship.ProbeAmmo == 0 || ship.ProbeWeaponId == 0)
            return;
        if (!WeaponDefs.TryGetValue(ship.ProbeWeaponId, out var w))
            return;
        // Authoritative cadence gate (the debounce for held-input replay).
        if (ship.LastProbeTick != 0 && tick - ship.LastProbeTick < w.FireIntervalTicks)
            return;

        ship.ProbeAmmo -= 1;
        ship.LastProbeTick = tick;

        // Just ahead of the ship (local +Z), clear of the hull — no physics, so it never has to
        // separate further; it simply sits where it was dropped for the rest of its lifespan.
        Vec3 fwd = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
        Vec3 pos = ship.State.Pos + fwd * (World.ShipRadius + 4f);

        _probes.Add(
            new ProbeSim
            {
                ProbeId = _nextShipId++,
                Team = ship.Team,
                WeaponId = w.WeaponId,
                SectorId = ship.SectorId,
                Pos = pos,
                ExpireAtTick = tick + w.ProjectileLifeTicks,
            }
        );
        ProbesChangedThisStep = true;
    }

    // Expire probes past their lifespan. Fixed index-loop order (like StepMines) keeps this
    // replay-deterministic; removal-safe so a client's next (possibly empty) MsgProbes reconciles.
    private void StepProbes(uint tick)
    {
        for (int i = 0; i < _probes.Count; i++)
        {
            var p = _probes[i];
            if (tick < p.ExpireAtTick)
                continue;
            _probes.RemoveAt(i);
            i--;
            ProbeGoneThisStep.Add((p.ProbeId, 0, p.Team, p.SectorId, p.Pos));
            ProbesChangedThisStep = true;
        }
    }
}
