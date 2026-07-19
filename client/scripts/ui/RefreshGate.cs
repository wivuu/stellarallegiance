namespace StellarAllegiance.Ui;

// Shared per-tab refresh throttle for the Build/Research catalog tabs. Each frame the tab hands in
// the current catalog size and an order-independent status signature; the gate decides whether to
// refresh this frame — when the signature or catalog size changed, OR the quarter-second timer
// elapsed — and reports whether the change is STRUCTURAL (signature/size changed → a full rebuild,
// vs a plain timed refresh). It owns the cached signature/count/timer so the two tabs stop
// re-implementing the identical bookkeeping. Behaviour is byte-identical to the inlined gate it
// replaced (E-R20): Run == (timer elapsed || structural); Structural is evaluated against the
// PREVIOUS signature/count, before they are updated.
internal sealed class RefreshGate
{
    private const double IntervalSeconds = 0.25;

    private double _timer;
    private long _sig = long.MinValue;
    private int _count = -1;

    // Returns (Run, Structural). Run == refresh this frame; Structural == the signature or catalog
    // size changed (Run is always true when Structural is). Advances the internal timer every call,
    // and — only when it fires — latches the new signature/count and resets the timer.
    public (bool Run, bool Structural) Tick(double delta, int catalogCount, long sig)
    {
        _timer -= delta;
        bool structural = sig != _sig || catalogCount != _count;
        if (_timer > 0 && !structural)
            return (false, false);
        _timer = IntervalSeconds;
        _count = catalogCount;
        _sig = sig;
        return (true, structural);
    }

    // Force the next Tick to fire structurally (mirrors the old `_statusSig = long.MinValue`
    // seams that reflected a pending affordance / rebuild immediately).
    public void Invalidate() => _sig = long.MinValue;
}
