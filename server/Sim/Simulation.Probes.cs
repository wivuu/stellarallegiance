using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Deployable recon probes (WP5 of the Fog plan). A probe is a passive, STATIONARY vision-sphere
// contributor: TryDeployProbe drops one just behind the deploying ship exactly like
// TryDropChaff/TryDeployMine (ammo + cadence gate mirrors those dispensers, riding the SAME
// cargo/ammo accounting — SeedDispenserAmmo/_dispenserByCargo), and StepProbes expires it after
// its authored lifespan (ProbeLifespanSec, already resolved into WeaponDef.ProjectileLifeTicks at
// content projection, same field missiles/mines/chaff use for their own lifespans).
//
// A probe is a small SOLID collision body — it's the first "deployable" handled by the generic
// Simulation.ResolveDeployableCollisions: a ship bounces off it like a miniature base and takes
// collision damage, and — because it has LOW HP — the impact damages the probe too (DamageProbe),
// so a solid ram (or weapon fire) is enough to kill it; it never needs the base-health system. They are a DESTRUCTIBLE combat target (ProbeHitPoints/ProbeHitRadius): a bolt or missile
// blast from an enemy team removes one (DamageProbe → gone reason 2). Their vision contribution is
// an unoccluded team vision sphere of ProbeSightRadius,
// folded into Simulation.Vision.cs's existing ViewerSnap list (see CaptureVisionInput) — no new
// code path in the vision worker. They stream to the OWNING team unconditionally, PLUS any enemy
// team that can currently radar-detect them (Simulation.Vision.cs VisibleEnemyProbes → ClientHub
// BuildProbesFor), so an enemy can see what it is allowed to shoot.
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
        public float Health; // remaining hit points (seeded from WeaponDef.ProbeHitPoints; 0 = invulnerable)
    }

    // Live probes (appended by TryDeployProbe, stepped in StepProbes). Exposed to the hub for the
    // owner-team-only MsgProbes stream.
    private readonly List<ProbeSim> _probes = new();
    public IReadOnlyList<ProbeSim> Probes => _probes;

    // Probes that expired/were cleared/were destroyed this step, drained by the hub into MsgProbeGone
    // frames (broadcast to all clients now — the owner AND the destroyer both want the outcome).
    // Reason 0 = expired past lifespan, 1 = match-clear cleanup/despawn (both silent removals), 2 =
    // destroyed by enemy fire (client plays an explosion + impact FX). Cleared at the top of Step
    // (mirrors MineGoneThisStep).
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

        // Behind the ship's engine (local −Z), clear of the hull — deployed out the back like a
        // dropped buoy. It's a solid collision body now (ResolveProbeCollisions), so it ejects far
        // enough back that the deploying ship starts OUTSIDE the probe's collision sphere
        // (ShipRadius + ProbeHitRadius + margin); otherwise dropping one would kick/damage your own
        // ship. It never moves afterward — it sits where dropped for the rest of its lifespan.
        Vec3 fwd = ship.State.Rot.Rotate(new Vec3(0f, 0f, 1f));
        float hitR = w.ProbeHitRadius > 0f ? w.ProbeHitRadius : 4f;
        Vec3 pos = ship.State.Pos - fwd * (World.ShipRadius + hitR + 2f);

        _probes.Add(
            new ProbeSim
            {
                ProbeId = _nextShipId++,
                Team = ship.Team,
                WeaponId = w.WeaponId,
                SectorId = ship.SectorId,
                Pos = pos,
                ExpireAtTick = tick + w.ProjectileLifeTicks,
                Health = w.ProbeHitPoints, // 0 = authored-invulnerable (no combat target this deploy)
            }
        );
        ProbesChangedThisStep = true;
    }

    // Apply damage to a live probe (bolt or missile blast). At/below zero health the probe is removed
    // and a gone reason-2 (destroyed) is queued for broadcast. A 0-health (invulnerable) probe never
    // reaches this path — the shooter's target scan skips it. Sim-thread only (called from the shot
    // resolution / missile passes, exactly where ship/base damage is applied).
    private void DamageProbe(ProbeSim p, float dmg, uint tick)
    {
        if (p.Health <= 0f)
            return; // invulnerable — should not have been targeted
        p.Health -= dmg;
        if (p.Health > 0f)
            return;
        int idx = _probes.IndexOf(p);
        if (idx < 0)
            return; // already removed this step (e.g. a second bolt resolving the same tick)
        _probes.RemoveAt(idx);
        ProbeGoneThisStep.Add((p.ProbeId, 2, p.Team, p.SectorId, p.Pos));
        ProbesChangedThisStep = true;
    }

    // Find a live, damageable (Health > 0) probe by id — used by the shot/missile resolution passes to
    // resolve a queued probe hit (the probe may already be gone by resolution time; caller skips null).
    private ProbeSim? FindDamageableProbe(ulong id)
    {
        for (int i = 0; i < _probes.Count; i++)
            if (_probes[i].ProbeId == id && _probes[i].Health > 0f)
                return _probes[i];
        return null;
    }

    // Whether a probe id is still live (any health, incl. invulnerable 0-health probes) — used by the
    // vision apply to prune a stale enemy-visibility id whose probe expired during the compute window.
    private bool ProbeExists(ulong id)
    {
        for (int i = 0; i < _probes.Count; i++)
            if (_probes[i].ProbeId == id)
                return true;
        return false;
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
