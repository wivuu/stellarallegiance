// The warp flags the coordinator's warp orchestration and the fade/asteroid-insert paths share while a
// sector swap is covered by the WarpFlash. The warp TIMING (start/cover deadlines, the Warp* consts) and
// the warp METHODS stay in the coordinator; this is just the shared mutable state a couple of other
// concerns need to read. A plain holder — no Godot dependency.
public sealed class WarpState
{
    public bool Settling; // the destination-load settle window is open (TickWarpSettle drives it)
    public uint? PendingSector; // a deferred Phase-B swap is armed — the flash is covering the not-yet-applied swap
    public double LastRockSec; // real-time of the last rock insert into the settling sector (quiet-debounce)

    // A node streaming into the destination while a swap is covered (pending or settling) must appear
    // INSTANTLY, not dissolve — the flash already hides the pop and a fade would bleed out from under it.
    public bool Covering => PendingSector is not null;
}
