// Headless stand-in for the client's StellarAllegiance.Net.MatchPhase (from client/scripts/NetTypes.cs), so MatchClock.cs
// links without the Godot client's NetTypes.
namespace StellarAllegiance.Net;

public enum MatchPhase : byte
{
    Lobby,
    Active,
    Ended,
}
