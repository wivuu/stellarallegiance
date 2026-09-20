using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  CollisionModels.cs — THE ONE PLACE the client turns a res:// GLB into the shared collision
//  SimModel, and the ledger of every model it could NOT build.
//
//  Hull parity with the server is load-bearing (CollisionWorld): the local ship predicts its bounces
//  against the same convex hulls the sim resolves against. A model that cannot be built does not crash
//  anything — the caller degrades to a sphere — and that is exactly why it must never be QUIET: a
//  sphere where the server has a station hull makes the predicted ship bounce off empty space, and every
//  tick of that disagreement is a reconcile. (v0.0.13 + v0.0.14 shipped with NO collision data at all —
//  Godot's exporter replaces an imported .glb by its imported scene, so the raw bytes this reader needs
//  were never in the package — and the only trace was a warning nobody reads. It looked like server lag.)
//
//  WHERE A MODEL COMES FROM — two sources, the same result:
//    - the RAW .glb (SimModel.FromGlb: the shared GlbReader + ConvexHull.Build over the bytes the server
//      reads). Only there when running from SOURCE, where res:// is the working tree.
//    - its COLLISION SIDECAR, <name>.glb.simmodel (SimModelCodec): that same model, already built, written
//      by tools/collision-sidecars right before an export and shipped by the presets' include_filter.
//      The ONLY source inside a package, where an imported .glb exists as its imported scene alone.
//  The sidecar stores the face planes ConvexHull.Build produced, float for float, so either source
//  resolves a contact bit-identically to the server. From source the raw file wins (it is the truth on
//  disk; a sidecar there may predate an edit); in a package the sidecar does.
//
//  So a failure here is a FAULT, reported three ways:
//    - an ERROR in the log, once per model;
//    - the ledger below, which the server browser turns into a standing alert before anyone joins;
//    - "fielded" faults — a model the SERVER actually put in this match (CollisionWorld falling back
//      to a sphere for it) — which the flight HUD shows for as long as they stand.
//  and AssetVerify (`--verify-assets`) turns the same ledger into an exit code for the packaging scripts,
//  so a build in that state is never published again.
//
//  Threading: AssetPreloader warms models on a worker Task while gameplay may ask from the main thread,
//  so everything here is concurrent-safe and there are NO events. Consumers poll `Version` (a counter
//  bumped on every change) from their own _Process and refresh when it moves.
// =====================================================================
public static class CollisionModels
{
    public readonly record struct Fault(string Path, string Reason);

    // Built models keyed by res:// path. A null entry = a fault (see _faults), cached so nobody retries.
    private static readonly ConcurrentDictionary<string, SimModel?> _models = new();
    private static readonly ConcurrentDictionary<string, string> _faults = new();

    // What the server fielded that we cannot collide with, as the label shown to the player
    // ("Garrison station", "Scout hull"). Cleared with the world (CollisionWorld.Clear).
    private static readonly ConcurrentDictionary<string, byte> _fielded = new();

    // Labels already written to the log. The fielded SET is cleared and refilled on every world rebuild
    // (a join alone rebuilds three or four times); the log says it once per run.
    private static readonly ConcurrentDictionary<string, byte> _fieldedLogged = new();

    private static int _version;
    private static int _built;
    private static int _fromSidecar;

    // Bumped whenever a fault is recorded or the fielded set changes. Poll it; never subscribe.
    public static int Version => Volatile.Read(ref _version);

    // Models built successfully so far (the "n of m" in the alert), and how many of those were decoded
    // from a collision sidecar rather than built from a raw .glb (all of them, in a package).
    public static int BuiltCount => Volatile.Read(ref _built);
    public static int FromSidecarCount => Volatile.Read(ref _fromSidecar);

    // Stations first, then hulls, then rocks — the order of how badly a missing model hurts in play (and
    // so the order the alert should name them in), alphabetical within each.
    public static IReadOnlyList<Fault> Faults =>
        _faults
            .Select(kv => new Fault(kv.Key, kv.Value))
            .OrderBy(f =>
                f.Path.Contains("/bases/") ? 0
                : f.Path.Contains("/ships/") ? 1
                : 2
            )
            .ThenBy(f => f.Path, System.StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> FieldedFaults =>
        _fielded.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToList();

    public static bool TryGet(string resPath, out SimModel? model) => _models.TryGetValue(resPath, out model);

    // Build (or fetch) the collision model for a res:// GLB. `pre` is the rigid pre-rotation baked into
    // the model — bases pass CollisionConfig.BaseModelRotation, everything else identity. The cache is
    // keyed by PATH alone, which is safe because the pre-rotation is a pure function of the path's
    // category (every base load, warm or on demand, passes the same rotation).
    //
    // Returns null on a fault (the caller falls back to a sphere) — after recording it. Two threads
    // asking for the same cold path may both build it; the result is identical, so the race is benign.
    public static SimModel? Load(string resPath, Quat pre = default)
    {
        if (_models.TryGetValue(resPath, out SimModel? cached))
            return cached;

        SimModel? model = null;
        string? reason = null;
        bool fromSidecar = false;
        if (IsForcedFault(resPath))
            reason = "forced by SA_FAULT_COLLISION_MODELS (test hook)";
        else
        {
            // From source the raw .glb is the truth on disk; in a package only the sidecar exists, so
            // it goes first there (and SA_COLLISION_SIDECARS=only makes a dev run behave like one).
            string? rawWhy = null;
            string? sidecarWhy = null;
            if (SidecarsOnly || !OS.HasFeature("editor"))
                model =
                    FromSidecar(resPath, out fromSidecar, out sidecarWhy)
                    ?? (SidecarsOnly ? null : FromRaw(resPath, pre, out rawWhy));
            else
                model = FromRaw(resPath, pre, out rawWhy) ?? FromSidecar(resPath, out fromSidecar, out sidecarWhy);
            if (model is null)
                reason = string.Join("; ", new[] { sidecarWhy, rawWhy }.Where(w => w is not null));
        }

        if (!_models.TryAdd(resPath, model))
            return _models[resPath]; // the other thread's identical result won the race; it also reported

        if (model is not null)
        {
            Interlocked.Increment(ref _built);
            if (fromSidecar)
                Interlocked.Increment(ref _fromSidecar);
        }
        else
        {
            _faults[resPath] = reason!;
            Interlocked.Increment(ref _version);
            // Under --verify-assets the report IS the output: thirty ERROR blocks with backtraces would
            // bury its thirty lines in a CI log.
            if (!AssetVerify.Requested)
                Log.Error(
                    $"[Collision] FAULT: no collision model for {resPath} — {reason}. "
                        + "Contacts with it will be predicted against a sphere and rubber-band."
                );
        }
        return model;
    }

    // The model built from the RAW .glb bytes — not the imported scene: the shared GlbReader +
    // ConvexHull.Build over the same bytes the server reads is what makes the two hulls bit-identical.
    private static SimModel? FromRaw(string resPath, Quat pre, out string? why)
    {
        why = null;
        byte[] bytes = FileAccess.GetFileAsBytes(resPath);
        if (bytes is not { Length: > 0 })
        {
            why = "its raw .glb bytes are not readable in this build";
            return null;
        }
        try
        {
            SimModel model = SimModel.FromGlb(bytes, resPath, pre);
            if (model.LongestAxis > 1e-3f)
                return model;
            why = "the GLB holds no usable geometry (degenerate hull)";
        }
        catch (System.Exception e)
        {
            why = $"the hull build threw {e.GetType().Name}: {e.Message}";
        }
        return null;
    }

    // The model decoded from the GLB's collision sidecar. `pre` is not needed: whatever rotation the
    // model takes was baked in when the sidecar was written (tools/collision-sidecars, by folder).
    private static SimModel? FromSidecar(string resPath, out bool used, out string? why)
    {
        used = false;
        why = null;
        string sidecar = resPath + SimModelCodec.SidecarSuffix;
        byte[] bytes = FileAccess.GetFileAsBytes(sidecar);
        if (bytes is not { Length: > 0 })
        {
            why = $"it has no collision sidecar ({sidecar.GetFile()})";
            return null;
        }
        if (SimModelCodec.TryDecode(bytes, out _, out SimModel? model) && model is { LongestAxis: > 1e-3f })
        {
            used = true;
            return model;
        }
        why = $"its collision sidecar ({sidecar.GetFile()}) does not decode — corrupt, or written by another format version";
        return null;
    }

    // CollisionWorld calls this when it has just degraded something the SERVER put in the match to a
    // sphere because its model faulted. `label` is player-facing ("Garrison station").
    public static void ReportFielded(string label, string resPath)
    {
        if (!_fielded.TryAdd(label, 0))
            return;
        Interlocked.Increment(ref _version);
        if (!_fieldedLogged.TryAdd(label, 0))
            return;
        Log.Error(
            $"[Collision] FAULT IN PLAY: the server fielded {label} ({resPath}) and this client cannot build its "
                + "collision model — prediction near it is wrong until the install is repaired."
        );
    }

    // A world rebuild re-adds every body, and with it re-reports whatever still faults.
    public static void ClearFielded()
    {
        if (_fielded.IsEmpty)
            return;
        _fielded.Clear();
        Interlocked.Increment(ref _version);
    }

    // One line for an alert's sub-text: how much is broken, and a few names so a report is actionable.
    public static string Summary()
    {
        var faults = Faults;
        if (faults.Count == 0)
            return "";
        int total = faults.Count + BuiltCount;
        var names = faults.Take(3).Select(f => f.Path.GetFile().GetBaseName());
        string more = faults.Count > 3 ? ", …" : "";
        return $"{faults.Count} of {total} collision models could not be built ({string.Join(", ", names)}{more}).";
    }

    // Test hook: SA_FAULT_COLLISION_MODELS=all, or a comma-list of path fragments ("garrison,fig13"),
    // makes the matching loads fault exactly as an unreadable GLB does — the way to see the alerts (and
    // the reconcile storm they explain) from a dev run, where the raw files are always readable.
    // Test hook: SA_COLLISION_SIDECARS=only makes a run from source load models the way a PACKAGE does —
    // from the sidecars, never the raw .glb — so the shipped path can be flown (and `--verify-assets`
    // can check the sidecars are complete) without exporting anything.
    private static readonly bool SidecarsOnly = System.Environment.GetEnvironmentVariable("SA_COLLISION_SIDECARS") == "only";

    // Read through .NET, not Godot's OS singleton: the first Load may well run on AssetPreloader's worker.
    private static readonly string[] _forced = (
        System.Environment.GetEnvironmentVariable("SA_FAULT_COLLISION_MODELS") ?? ""
    ).Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);

    private static bool IsForcedFault(string resPath)
    {
        foreach (string f in _forced)
            if (f == "all" || resPath.Contains(f, System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
