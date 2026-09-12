using StellarAllegiance.Shared;

namespace SimServer.Sim;

// Everything the sim reports to the hub about ONE step: the one-shot events that become frames
// (deaths, gone-events, chaff pops, lost contacts, reclaims, new bases), the change flags that gate
// the low-rate streams, and the system-chat notices addressed to a team or a pilot. Written by the
// sim during Step(), read by ClientHub.AfterStep() on the same thread once Step() has returned, and
// cleared as a unit at the top of the next Step() — the sim/hub contract is this one object, so a
// new event kind is one field here (plus its producer and consumer), and a test can fabricate a
// step's events without driving the whole sim.
//
// Tuple element names are load-bearing: consumers read them by name (g.reason, g.byShipId, ...).
public sealed class StepEvents
{
    // ---- one-shot events (each becomes a reliable/lossy frame in AfterStep) ----

    // Ships that died this step: (id, reason) with reason = Simulation.GoneDestroyed / GoneClean.
    public readonly List<(ulong id, byte reason)> Deaths = new();

    // Fog: a ship that left a team's streamed union this vision apply (reason-2 quiet fade to that team).
    public readonly List<(byte team, ulong shipId)> LostContacts = new();

    // Missile detonation / expiry FX: reason 0 expired, 1 impact.
    public readonly List<(ulong id, byte reason, uint sector, Vec3 pos)> MissileGone = new();

    // Chaff puffs ejected this step (one-shot spawn broadcast; the client expires them locally).
    public readonly List<Simulation.ChaffSim> ChaffSpawned = new();

    // Single mines that popped: per-mine FX + aliveMask reconcile.
    public readonly List<(ulong fieldId, byte mineIndex, byte reason, uint sector, Vec3 pos)> MineGone = new();

    // Probes removed: reason 0 expired, 1 match cleanup, 2 destroyed by enemy fire.
    public readonly List<(ulong id, byte reason, byte team, uint sector, Vec3 pos)> ProbeGone = new();

    // Salvage items that left the world: reason 0 expired, 1 match cleanup, 2 picked up by byShipId.
    public readonly List<(ulong id, byte reason, uint sector, Vec3 pos, ulong byShipId)> SalvageGone = new();

    // Reconnect reclaims resolved this step: (old client id, new client id) — the scoreboard memo
    // drops the old id BEFORE building a frame so a reclaimed pilot never shows twice.
    public readonly List<(int oldClientId, int newClientId)> Reclaims = new();

    // Bases created this step (constructor completions): fog-off broadcasts a one-slice reveal.
    public readonly List<ulong> BasesCreated = new();

    // ---- change flags (the low-rate streams' cadence gates: "changed OR coarse keepalive") ----

    public bool MinefieldsChanged;
    public bool ProbesChanged;
    public bool BasesChanged;
    public bool TeamStateChanged;
    public bool LoadoutsChanged;
    public bool StatsChanged;
    public bool ResearchChanged;
    public bool ConstructorChanged;

    // Salvage changes are PER SECTOR (items drift for seconds after a kill; a global flag would
    // re-stream every other sector every tick for the whole burst).
    public readonly HashSet<uint> SalvageChangedSectors = new();
    public bool SalvageChanged => SalvageChangedSectors.Count > 0;

    // ---- notices (system chat), drained in AfterStep in a stable order ----

    public readonly List<(byte Team, string Text)> MinerNotices = new();
    public readonly List<(byte Team, string Text)> ConstructorNotices = new();
    public readonly List<(byte Team, string Text)> ResearchTeamNotices = new();
    public readonly List<(int ClientId, string Text)> ResearchNotices = new();
    public readonly List<(int ClientId, string Text)> PilotNotices = new();
    public readonly List<(int ClientId, string Text)> OrderNotices = new();

    // Team-wide GOLD directives (MsgChatRelay scope 2) once the sim has validated a commander order.
    public readonly List<(byte Team, string Issuer, string Text)> OrderDirectives = new();

    // Reset at the top of Step(). (World.RocksChangedThisStep / RocksRemovedThisStep live on World and
    // are cleared beside this call — they are world state the sim mutates, not sim events.)
    public void Clear()
    {
        Deaths.Clear();
        LostContacts.Clear();
        MissileGone.Clear();
        ChaffSpawned.Clear();
        MineGone.Clear();
        MinefieldsChanged = false;
        ProbeGone.Clear();
        ProbesChanged = false;
        SalvageGone.Clear();
        SalvageChangedSectors.Clear();
        PilotNotices.Clear();
        BasesChanged = false;
        TeamStateChanged = false;
        LoadoutsChanged = false;
        StatsChanged = false;
        Reclaims.Clear();
        MinerNotices.Clear();
        ConstructorNotices.Clear();
        BasesCreated.Clear();
        OrderNotices.Clear();
        OrderDirectives.Clear();
        ResearchChanged = false;
        ResearchNotices.Clear();
        ResearchTeamNotices.Clear();
        ConstructorChanged = false;
    }
}
