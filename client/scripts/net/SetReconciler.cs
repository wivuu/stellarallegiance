using System.Collections.Generic;

// Reconcile-by-omission bookkeeping shared by every "the frame IS the complete set" stream (probes,
// minefields, salvage — and any future one): the last-decoded row per id, the ids the current frame
// listed, and the prune that follows. A stream's applier calls Begin(), upserts/marks each row the
// frame carries, then Prune() returns the ids the frame no longer listed so the caller can free them
// (the renderer's silent reason-255 reconcile). Scratch is reused, so a per-tick frame allocates
// nothing here. Kept delegate-free on purpose: the caller loops the pruned ids itself.
public sealed class SetReconciler<TRow>
    where TRow : class
{
    public readonly Dictionary<ulong, TRow> Rows = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<(ulong Id, TRow Row)> _pruned = new();

    public int Count => Rows.Count;

    public void Begin() => _seen.Clear();

    // Record that the current frame listed this id (the caller has already stored/updated Rows[id]).
    public void Mark(ulong id) => _seen.Add(id);

    // Every cached row the current frame did not list, removed from Rows and returned (with its last
    // decoded state — the renderer needs the sector/position to free collision and place FX) for the
    // caller to hand to the renderer. Empty when the frame listed everything we had.
    public IReadOnlyList<(ulong Id, TRow Row)> Prune()
    {
        _pruned.Clear();
        if (Rows.Count != _seen.Count)
        {
            foreach (var kv in Rows)
                if (!_seen.Contains(kv.Key))
                    _pruned.Add((kv.Key, kv.Value));
            foreach (var (id, _) in _pruned)
                Rows.Remove(id);
        }
        return _pruned;
    }

    public bool TryGet(ulong id, out TRow row) => Rows.TryGetValue(id, out row!);

    public bool Remove(ulong id) => Rows.Remove(id);

    public void Clear() => Rows.Clear();
}
