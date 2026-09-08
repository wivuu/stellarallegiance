using Orleans;
using Orleans.Concurrency;

namespace PublicLobby.Grains;

// Trivial round-trip grain (WP0.2 acceptance: "lobby boots as a silo locally; a trivial grain
// round-trips"). Proves the co-hosted silo actually activates and calls a grain end-to-end under
// whichever clustering mode is active — nothing more. Not a template for the real entity grains
// (PlayerGrain/GameServerGrain/MatchGrain, later work packages): those load state from EF Core on
// activation and are the sole writers of their rows (ADR-0002); this one carries no state at all.
public interface IPingGrain : IGrainWithStringKey
{
    [ReadOnly]
    ValueTask<string> Ping();
}

public sealed class PingGrain : Grain, IPingGrain
{
    public ValueTask<string> Ping() => ValueTask.FromResult("pong:" + this.GetPrimaryKeyString());
}
