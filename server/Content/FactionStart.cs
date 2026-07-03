using Allegiance.Factions.Model;

namespace SimServer.Content;

// Stage-2 strategy spine: the per-match STARTING state a team derives from its faction, projected out
// of the Allegiance.Factions Core (which ContentLoader otherwise discards after projecting the wire
// defs). This is NOT wire content — it never reaches Protocol.BuildDefs; it feeds the server-only
// economy (World.TeamState) and, in Phase 5, the unlock-gating resolver. Stage-1 is a single stock
// faction, so both teams seed from the same snapshot; per-faction asymmetry is a Stage-4 concern.
//
// Credits are carried as int (the type the wire will carry in Phase 4) — Faction money is authored as
// a double but the values are whole credits. Tech/capability sets are kept as the library types so the
// per-team OWNED sets (mutable clones) can be fed straight to BuildableResolver later.
public sealed class FactionStart
{
    public int StartingCredits { get; }       // Faction.BonusMoney — credits a team starts a match with
    public int IncomePerPaycheck { get; }      // Faction.IncomeMoney — flat credits added each paycheck
    public TechSet BaseTechs { get; }          // seed for a team's OwnedTechs (cloned per team)
    public CapabilitySet BaseCapabilities { get; } // seed for a team's OwnedCapabilities (cloned per team)
    public string LifepodHullId { get; }       // reserved for Phase-5 spawn/eject wiring
    public string InitialStationId { get; }    // reserved for Phase-5 wiring

    public FactionStart(
        int startingCredits,
        int incomePerPaycheck,
        TechSet baseTechs,
        CapabilitySet baseCapabilities,
        string lifepodHullId,
        string initialStationId
    )
    {
        StartingCredits = startingCredits;
        IncomePerPaycheck = incomePerPaycheck;
        BaseTechs = baseTechs;
        BaseCapabilities = baseCapabilities;
        LifepodHullId = lifepodHullId;
        InitialStationId = initialStationId;
    }
}
