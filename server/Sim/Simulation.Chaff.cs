using System;
using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Chaff (decoy) countermeasures. TRACK 0 lands the shared surface only: the ChaffSim entity,
// the _chaff list + accessor, and no-op TryDropChaff / StepChaff / TryChaffAim seams that keep the
// game behaviourally identical. TRACK A fills the bodies (eject + drag + the D5 decoy-substitution
// hash roll). Wired into Step() via Pass A (input.DropChaff), StepChaff (before StepMissiles), and
// TryChaffAim (ahead of ResolveSeekerTarget in the missile aim block).
public sealed partial class Simulation
{
    // An in-flight chaff puff — a decoy the seeker substitution can home onto. A separate entity from
    // ShipSim (own list, no flight integration): it drifts + expires in StepChaff. Ids come from the
    // shared _nextShipId counter (unique across ships / missiles / chaff).
    public sealed class ChaffSim
    {
        public ulong ChaffId;
        public ulong OwnerShipId; // the ship that ejected it (its own missiles ignore it)
        public byte Team; // owner team (a missile only decoys onto an ENEMY puff)
        public uint WeaponId; // chaff-kind WeaponDef (lifespan / strength / decoy radius)
        public uint SectorId;
        public Vec3 Pos;
        public Vec3 Vel;
        public uint ExpireAtTick; // eject tick + ProjectileLifeTicks

        // Decoy stats snapshotted from the chaff WeaponDef at eject (server-only; not on the wire —
        // the client re-derives its own visual lifespan from ProjectileLifeTicks). Reading them off
        // the puff keeps TryChaffAim's hot scan off a per-candidate dictionary lookup.
        public float Strength; // ChaffStrength — how strongly this puff decoys a missile lock (D5)
        public float DecoyRadius; // u radius within which a missile can be decoyed onto this puff
    }

    // Live chaff puffs (appended by TryDropChaff, stepped in StepChaff). One-shot broadcast on spawn
    // (ChaffSpawnedThisStep); the client animates + expires them locally (D2 — no gone message).
    private readonly List<ChaffSim> _chaff = new();
    public IReadOnlyList<ChaffSim> Chaff => _chaff;

    // Eject a chaff puff from this ship's dispenser (ammo + cadence gated). TRACK A fills the body
    // (ammo/LastChaffTick gate, eject-aft velocity, lifespan from the chaff WeaponDef, append to
    // _chaff + ChaffSpawnedThisStep). Track-0 stub: no-op, so no chaff ever spawns.
    private void TryDropChaff(ShipSim ship, uint tick)
    {
        if (ship.ChaffAmmo == 0 || ship.ChaffWeaponId == 0)
            return; // no dispenser cargo on this hull, or spent
        var w = WeaponDefs[ship.ChaffWeaponId];
        // Authoritative cadence debounce (mirror TryFireMissile's LastMissileTick): held-input replay
        // re-asserts DropChaff every tick, so the server — NOT the client — edge-detects a new eject.
        if (ship.LastChaffTick != 0 && tick - ship.LastChaffTick < w.FireIntervalTicks)
            return;

        ship.ChaffAmmo--;
        ship.LastChaffTick = tick;

        // Eject aft: a puff behind the ship, inheriting half the ship's velocity plus a small kick
        // backward so it lags behind and lingers where the seeker is coming from.
        Vec3 aft = ship.State.Rot.Rotate(new Vec3(0f, 0f, -1f));
        var puff = new ChaffSim
        {
            ChaffId = _nextShipId++,
            OwnerShipId = ship.ShipId,
            Team = ship.Team,
            WeaponId = w.WeaponId,
            SectorId = ship.SectorId,
            Pos = ship.State.Pos + aft * 4f,
            Vel = ship.State.Vel * 0.5f + aft * 10f,
            ExpireAtTick = tick + w.ProjectileLifeTicks,
            Strength = w.ChaffStrength,
            DecoyRadius = w.DecoyRadius,
        };
        _chaff.Add(puff);
        ChaffSpawnedThisStep.Add(puff); // one-shot MsgChaff broadcast (D2)
    }

    // Advance every live chaff puff (drift + drag + expiry). Deterministic f32 only (no RNG/DateTime)
    // so the two-sim replay stays bit-identical. Removals collected then applied post-loop.
    private void StepChaff(uint tick)
    {
        if (_chaff.Count == 0)
            return;
        float dt = FlightModel.Dt;
        List<ChaffSim>? remove = null;
        foreach (var c in _chaff)
        {
            c.Pos += c.Vel * dt;
            c.Vel *= 0.95f; // drag: the puff coasts to a near-stop and hangs in space
            if (tick >= c.ExpireAtTick)
                (remove ??= new()).Add(c);
        }
        if (remove is not null)
            foreach (var c in remove)
                _chaff.Remove(c);
    }

    // The chaff substitution seam (D5), called in StepMissiles ahead of ResolveSeekerTarget. TRACK A
    // fills the body: a sticky DecoyChaffId, else a stateless pure-hash decoy roll over _chaff; on a
    // win it breaks the ship lock and homes on the puff, detonating within fuse range. Track-0 stub
    // reports "not decoyed" so a seeker behaves exactly as it did before chaff existed.
    private bool TryChaffAim(MissileSim mis, out Vec3 chaffAim, out bool detonateAtChaff)
    {
        chaffAim = default;
        detonateAtChaff = false;
        var missileDef = WeaponDefs[mis.WeaponId];

        // (1) Sticky latch: once decoyed, this missile homes on the SAME puff every tick (the lock is
        // permanently broken — TargetShipId is already 0). If that puff has expired, clear the latch
        // and report "not decoyed" so the missile coasts unguided to expiry.
        if (mis.DecoyChaffId != 0)
        {
            ChaffSim? puff = null;
            foreach (var c in _chaff)
                if (c.ChaffId == mis.DecoyChaffId)
                {
                    puff = c;
                    break;
                }
            if (puff is null)
            {
                mis.DecoyChaffId = 0;
                return false;
            }
            chaffAim = puff.Pos;
            detonateAtChaff = (puff.Pos - mis.Pos).Length() <= missileDef.ProjectileRadius + 2f;
            return true;
        }

        // (2) Only a live SHIP-locked seeker can be decoyed. A dumbfire / already-coasting missile
        // (TargetShipId==0) or a base-lock never substitutes onto chaff.
        if (mis.TargetShipId == 0 || GameContent.IsBaseLock(mis.TargetShipId))
            return false;

        // (3) Fresh roll: scan puffs in list order (deterministic). First enemy puff, same sector,
        // within its DecoyRadius of the MISSILE that wins the stateless pure-hash roll (D5) breaks the
        // lock permanently and the seeker homes on it. Same (missileId, chaffId) → same answer every
        // tick, so no RNG state and the two-sim replay stays bit-identical.
        foreach (var c in _chaff)
        {
            if (c.Team == mis.Team || c.SectorId != mis.SectorId)
                continue; // own/friendly puffs never decoy; cross-sector puffs are invisible
            if ((c.Pos - mis.Pos).Length() > c.DecoyRadius)
                continue;
            float pWin = c.Strength / MathF.Max(1e-3f, c.Strength + missileDef.ChaffResistance);
            if (MinefieldLayout.Hash01(mis.MissileId, c.ChaffId) < pWin)
            {
                mis.TargetShipId = 0;
                mis.DecoyChaffId = c.ChaffId;
                chaffAim = c.Pos;
                detonateAtChaff = (c.Pos - mis.Pos).Length() <= missileDef.ProjectileRadius + 2f;
                return true;
            }
        }
        return false;
    }
}
