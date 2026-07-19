using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// The one authoritative match clock — the latest server tick + match phase/winner, mirrored from each
// MsgMatch snapshot. Everything that needs "now" (research/constructor progress, the rock-spin phase, the
// match-end banner) reads it here instead of each concern caching its own tick. Written only by the
// coordinator (WorldRenderer.NetSetMatch / Reset). A plain holder — no Godot dependency.
public sealed class MatchClock : ITickSource
{
    // Latest authoritative sim tick (Match.Tick). ShipController slaves its prediction clock to this so
    // client/server ticks index the same integration.
    public uint ServerTick { get; set; }

    // Match phase + winning team (T9). Read by Hud to show the match-end banner; winner null = none.
    public MatchPhase Phase { get; set; } = MatchPhase.Lobby;
    public byte? Winner { get; set; }

    // The authoritative tick in seconds. The rock tumble (visual + predicted hull) is phased on this so
    // they rotate together and stay within ~1° of the server's live hull.
    public float Seconds => ServerTick * FlightModel.Dt;

    // A world rebuild (reconnect / phase change) drops the clock back to the lobby baseline.
    public void Reset()
    {
        ServerTick = 0;
        Phase = MatchPhase.Lobby;
        Winner = null;
    }
}
