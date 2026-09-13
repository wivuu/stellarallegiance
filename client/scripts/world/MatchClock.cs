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

    // ---- Render timeline: the playback clock every server-driven entity is rendered on -------------
    //
    // RenderMs is the client's PRESENTATION clock: it advances once per frame by the delta Godot
    // reports for that frame — with vsync and the engine's delta smoothing (on by default) that is the
    // display cadence, the time the frame will actually be shown — never by a raw wall-clock read.
    // Time.GetTicksMsec() is an integer-millisecond stamp taken whenever the CPU happens to reach a
    // node; on a loaded machine that scheduling jitter (a few ms, plus the 16/17/17 ms integer pattern
    // at 60 fps) was rendered straight into remote ships' positions — a per-frame kink of ~1.4 u at
    // 150 u/s for 2 ms of jitter (tests/InterpTest, 'fjit2' and 'msclock' arms), the "jerky other
    // ship" that survived every previous smoothing pass. The own ship already renders on the same
    // delta-accumulated timeline (ShipController's prediction accumulator →
    // PredictionController.RenderAlpha), so the two clocks finally agree. Whether the delta really is
    // smoothed for a given run is reported by `[interp-stats] delta_snap`; with raw deltas this clock
    // is exactly as jittery as the wall clock was (no worse, no better).
    //
    // ServerNowMs maps RenderMs onto the server's tick timeline (tick × 50 ms — the axis every
    // MotionInterpolator sample is stamped on) through ONE client-wide offset, observed once per
    // drained snapshot (OnSnapshot). Every record in a snapshot shares the snapshot's tick, so one
    // estimator serves all entities; the old per-entity EMA re-learned the same number for every
    // ship that entered view, and its ~5 s convergence (seeded by a burst-biased first packet) was
    // itself a visible ~10 u wander on a fast hull. When several snapshots drain in one frame only
    // the freshest tick says how current the data is (the older ones are stale by construction), so
    // the estimator is fed the frame's minimum once per frame. The applied offset follows the
    // estimator directly only during the first ~2 s after the first seed (normally the lobby) and on
    // a regime jump (reconnect-sized discontinuity); otherwise it is rate-limited so estimator motion
    // can only ever read as an imperceptible ≤0.5 % speed trim.
    public double RenderMs { get; private set; }

    // Estimated server time "now" on the render clock (ms on the tick axis). Consumers subtract their
    // own playback delay. Meaningless until the first snapshot has been observed (HasServerTime).
    public double ServerNowMs => RenderMs - _offset;
    public bool HasServerTime => _seeded;

    public const double MsPerTick = FlightModel.Dt * 1000.0;
    private const double SteadyAlpha = 0.02; // ~2.5 s time constant: tracks drift, rejects jitter
    private const int SeedObservations = 40; // first ~2 s: min-track and apply unslewed (the lobby absorbs it)
    private const double MaxSlewFraction = 0.005; // applied offset moves ≤0.5 % of real time per frame
    private const double ReseedJumpMs = 500.0; // a discontinuity this large is a new regime, not jitter

    private double _offset; // applied: RenderMs − serverMs
    private double _offsetTarget; // the estimator's current belief
    private bool _seeded;
    private int _obsCount; // frames folded since the first seed (seed phase while < SeedObservations)
    private ulong _lastFrame = ulong.MaxValue;
    private double _frameObs; // minimum observation of the current frame (freshest tick)
    private bool _haveFrameObs;

    // Once per frame, before anything reads ServerNowMs. Idempotent per frame index so the first
    // caller in tree order advances and later callers are no-ops. `deltaSec` is the frame's
    // presentation delta (Godot's _Process delta; see WorldRenderer for the Movie Maker exception).
    public void Advance(double deltaSec, ulong frame)
    {
        if (frame == _lastFrame)
            return;
        _lastFrame = frame;
        double deltaMs = deltaSec * 1000.0;
        if (deltaMs <= 0.0)
            return; // a zero/negative delta (paused, clock hiccup) advances nothing
        RenderMs += deltaMs;

        if (_haveFrameObs)
        {
            Fold(_frameObs);
            _haveFrameObs = false;
        }
        double err = _offsetTarget - _offset;
        if (_obsCount < SeedObservations)
        {
            _offset = _offsetTarget; // seeding: follow directly (a rate-limited convergence would take ~20 s)
            return;
        }
        double maxStep = deltaMs * MaxSlewFraction;
        _offset += System.Math.Clamp(err, -maxStep, maxStep);
    }

    // One observation per drained snapshot: the render-clock time this tick's data became available.
    // The first ever seeds immediately (a ship in that same snapshot renders off a sane clock); the rest
    // are folded once per frame, freshest tick wins, in the next Advance.
    public void OnSnapshot(uint tick)
    {
        double obs = RenderMs - tick * MsPerTick;
        if (!_seeded)
        {
            _seeded = true;
            _offsetTarget = obs;
            _offset = obs;
        }
        if (!_haveFrameObs || obs < _frameObs)
            _frameObs = obs;
        _haveFrameObs = true;
    }

    private void Fold(double obs)
    {
        if (System.Math.Abs(obs - _offsetTarget) > ReseedJumpMs)
        {
            // A new regime (long stall, sleep, server hiccup burst): step once, then straight back to
            // the rate-limited steady state — no second unslewed seed phase mid-flight.
            _offsetTarget = obs;
            _offset = obs;
            _obsCount = SeedObservations;
            return;
        }
        if (_obsCount < SeedObservations)
            _offsetTarget = System.Math.Min(_offsetTarget, obs); // seed: the least-delayed arrival seen so far
        else
            _offsetTarget += (obs - _offsetTarget) * SteadyAlpha;
        _obsCount++;
    }

    // A world rebuild (reconnect / phase change) drops the clock back to the lobby baseline. The render
    // clock keeps running (it is the frame timeline, not match state); the server mapping re-seeds.
    public void Reset()
    {
        ServerTick = 0;
        Phase = MatchPhase.Lobby;
        Winner = null;
        _seeded = false;
        _obsCount = 0;
        _offset = 0;
        _offsetTarget = 0;
        _haveFrameObs = false;
    }
}
