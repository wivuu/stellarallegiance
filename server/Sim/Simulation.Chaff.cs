using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Chaff (sensor-decoy) countermeasures. TRACK 0 lands the shared surface only: the ChaffSim entity,
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
        // Track A: implement.
    }

    // Advance every live chaff puff (drag + expiry). TRACK A fills the body. Track-0 stub: no-op.
    private void StepChaff(uint tick)
    {
        // Track A: implement.
    }

    // The chaff substitution seam (D5), called in StepMissiles ahead of ResolveSeekerTarget. TRACK A
    // fills the body: a sticky DecoyChaffId, else a stateless pure-hash decoy roll over _chaff; on a
    // win it breaks the ship lock and homes on the puff, detonating within fuse range. Track-0 stub
    // reports "not decoyed" so a seeker behaves exactly as it did before chaff existed.
    private bool TryChaffAim(MissileSim mis, out Vec3 chaffAim, out bool detonateAtChaff)
    {
        chaffAim = default;
        detonateAtChaff = false;
        return false;
    }
}
