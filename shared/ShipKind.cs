namespace StellarAllegiance.Shared
{
    // A ship's mutually-exclusive ROLE. This is ONE of three orthogonal axes that describe a ship:
    //   - Kind (this enum)  — the ship's role/form: combat hull, ejected pod, ore miner, constructor.
    //   - ShipClass         — the hull MODEL (Scout/Fighter/Bomber); which mesh + flight stats.
    //   - IsPig (bool)       — AI-controlled vs human-piloted; independent of role (a PIG escape pod
    //                          is BOTH IsPig and Kind.Pod).
    //
    // Serialized into the ShipRecord flags byte on the wire — see Protocol.ShipFlagPod/Miner/
    // Constructor. Combat is the default (no role bit set). Kept in the shared library so the native
    // sim server and the Godot client branch on the SAME type.
    public enum ShipKind : byte
    {
        Combat = 0,  // player or PIG combat hull (default — no role bit on the wire)
        Pod,         // ejected escape pod (a form change of a combat ship)
        Miner,       // AI ore harvester (server/Sim/Simulation.Mining.cs)
        Constructor, // AI base-builder drone (spawn/brain/build in server/Sim/Simulation.Constructors.cs)
    }
}
