using System.Collections.Generic;
using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Mines & fields. TRACK 0 lands the shared surface only: the MineFieldSim entity, the _minefields
// list (exposed via Simulation.Minefields), and no-op TryDeployMine / StepMines seams that keep the
// game behaviourally identical. TRACK B fills the bodies (seed-based cloud deploy + the fixed-order
// grid-cube proximity scan that depletes a field mine-by-mine). Wired into Step() via Pass A
// (input.DropMine) and StepMines (after StepMissiles).
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
    }

    // Live minefields (deployed by TryDeployMine, stepped in StepMines). Exposed to the hub for the
    // per-anchor-sector MsgMinefields stream via Simulation.Minefields.
    private readonly List<MineFieldSim> _minefields = new();

    // Deploy a minefield behind this ship (ammo + cadence gated). TRACK B fills the body (one deploy
    // = one field of min(MineAmmo, CloudCount) mines; Seed derived deterministically; positions via
    // MinefieldLayout; AliveMask = (1<<n)-1; set MinefieldsChangedThisStep). Track-0 stub: no-op.
    private void TryDeployMine(ShipSim ship, uint tick)
    {
        // Track B: implement.
    }

    // Expire/deplete fields + proximity-trigger armed mines against the ship grid. TRACK B fills the
    // body (fixed-order grid-cube scan per armed mine; on trigger apply blast + push MineGone + clear
    // the mask bit + set MinefieldsChangedThisStep). Track-0 stub: no-op, so no field is ever active.
    private void StepMines(uint tick)
    {
        // Track B: implement.
    }
}
