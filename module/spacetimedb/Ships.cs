using SpacetimeDB;
using StellarAllegiance.Shared;

// =====================================================================
//  Ships.cs — the Ship table schema only.
//
//  The 20 Hz match simulation (spawn/respawn/integration/combat/pods/docking) no longer
//  runs in this module: it lives entirely in the native sim server (server/), and STDB is
//  demoted to lobby/teams/chat/defs/match-results (see .PLAN/NATIVE-SIM.md step 6). The
//  Ship TABLE is kept — never written here — purely so the client's generated bindings
//  still emit the `Ship` row type, which the native game-socket client (GameNetClient)
//  reuses as its in-memory ship record. Removing the table would break that codegen.
// =====================================================================

[SpacetimeDB.Table(Accessor = "Ship", Public = true)]
public partial struct Ship
{
    [PrimaryKey]
    [AutoInc]
    public ulong ShipId;
    public Identity Owner;
    public byte Team;           // denormalized from Player for fast sim checks
    public uint SectorId;       // which sector this ship is flying in (partitions the world)
    public ShipClass Class;
    public float PosX;
    public float PosY;
    public float PosZ;
    public float VelX;
    public float VelY;
    public float VelZ;
    public float RotX;
    public float RotY;
    public float RotZ;
    public float RotW;
    // Angular velocity (rad/s, world axes). Persisted so rotational momentum
    // survives between SimTicks and the client can reconcile against it.
    public float AngVelX;
    public float AngVelY;
    public float AngVelZ;
    // Afterburner power ramp 0..1 (FlightModel ShipState.AbPower). Persisted/synced so
    // the client predicts the same afterburner spin-up/down the server integrates.
    public float AbPower;
    public float Health;
    // Physical mass, seeded from the class def at spawn (see ShipStatsFor). Drives flight
    // accel (force/mass in FlightModel.Integrate) and ship-vs-ship collision response.
    // A field (not derived) so future cargo/upgrades can vary it per ship.
    public float Mass;
    public uint LastInputTick; // highest sim tick integrated; for reconciliation
    public uint LastFireTick;  // sim tick of this ship's most recent shot (fire-rate gate)
    // True for AI-controlled combat drones (PIGs) — lets the client highlight drones on the
    // HUD. The native sim server owns this now; the module never sets it.
    public bool IsPig;
    // True for an escape pod — a weak, slow, unarmed lifeboat. Native-sim owned; the client
    // reads it to pick the pod mesh/HUD. The module never sets it.
    public bool IsPod;
}
