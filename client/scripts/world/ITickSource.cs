// The single value TeamStateStore (and, later, other view-state) needs from the match clock: the live
// server tick, for deriving research/constructor progress. Narrowing it to this interface keeps the
// store a pure POCO with no dependency on the concrete MatchClock (or any Godot type), so it can be
// unit-tested headlessly. MatchClock is the production implementation.
public interface ITickSource
{
    uint ServerTick { get; }
}
