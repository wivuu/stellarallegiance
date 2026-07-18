using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  AssetPreloader.cs — STARTUP CACHE WARMING
//
//  Warms every cache a first join/launch used to fill mid-gameplay, starting the moment the
//  main scene boots (under the engine splash / server-browser screen) so common assets never
//  stall a gameplay frame. Measured before this existed: the join-time world restream applied
//  ~90 frames in ONE _Process — ~2.4s — dominated by first-touch asteroid-variant GD.Loads
//  (~300ms each, the GLBs embed multi-MB textures) plus a QuickHull SimModel rebuild of the
//  same bytes (~60ms each); the spawn frame then spent ~1s in ApplySectorEnv's occluder
//  vertex readback. All of that is warmable up front:
//
//    Phase A (Godot's threaded loader): every asteroid-variant GLB scene, so a later GD.Load
//            is a cache hit.
//    Phase B (worker Task): the shared collision SimModels (GlbReader + QuickHull, pure C#)
//            for those same GLBs, consumed by CollisionWorld.LoadGlb.
//    Phase C (main thread, ONE item per frame): per-variant mesh readbacks + shadow-occluder
//            extremes + trace BVHs (WorldRenderer.WarmAsteroidVariant) — sliced so even the
//            warm itself never hitches.
//    Phase D: the per-source effect shaders (BuildSphere / ShieldFlash / AlephView) compile
//            once here instead of at their first in-world spawn.
//
//  Scope is the ASTEROID CATALOG ONLY (AsteroidShapes.Variants — wire-significant, every match
//  fields all of it) plus the effect shaders. Ships and bases are deliberately NOT warmed: the
//  model set will grow with content, most of it never appearing in a given match, so those stay
//  lazy (their first-touch costs are also an order of magnitude smaller than the rock GLBs).
//
//  Everything stays lazy-safe: if gameplay touches an asset before its warm lands, the old
//  synchronous load path runs exactly as before (and stores its result back).
// =====================================================================
public partial class AssetPreloader : Node
{
    // Collision SimModels keyed by res:// path, built off-thread here and/or stored back by
    // CollisionWorld.LoadGlb. A null entry = unreadable GLB (sphere fallback), cached so nobody
    // retries. ConcurrentDictionary: the worker Task writes while the main thread reads.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SimModel?> _simModels = new();

    public static bool TryGetSimModel(string resPath, out SimModel? model) => _simModels.TryGetValue(resPath, out model);

    public static void StoreSimModel(string resPath, SimModel? model) => _simModels[resPath] = model;

    private readonly List<string> _pendingScenes = new(); // threaded-load requests in flight
    private readonly Queue<string> _finishQueue = new(); // loaded scenes awaiting main-thread finishing
    private bool _done;
    private ulong _startMs;

    // STRONG references to the warmed scenes. Godot's resource cache holds weak refs — dropping
    // the LoadThreadedGet result would evict the scene and the next GD.Load would re-read the
    // whole GLB from disk (~300ms for an asteroid variant), defeating the warm entirely.
    private readonly Dictionary<string, PackedScene> _scenes = new();

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();

        // Phase A: imported scenes on Godot's threaded loader (texture decode included). The
        // variant catalog is wire-significant and client-known (AsteroidShapes).
        var paths = new List<string>();
        foreach (string v in AsteroidShapes.Variants)
        {
            string path = $"res://assets/asteroids/{v}.glb";
            paths.Add(path);
            if (ResourceLoader.Exists(path) && ResourceLoader.LoadThreadedRequest(path) == Error.Ok)
                _pendingScenes.Add(path);
        }

        // Phase B: collision SimModels from the raw GLB bytes on a worker (FileAccess and the
        // shared GlbReader/QuickHull are engine-free C#, safe off the main thread).
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (string path in paths)
            {
                if (_simModels.ContainsKey(path))
                    continue; // gameplay beat us to it
                SimModel? model = null;
                byte[] bytes = FileAccess.GetFileAsBytes(path);
                if (bytes is { Length: > 0 })
                    try
                    {
                        model = SimModel.FromGlb(bytes, path);
                    }
                    catch (System.Exception e)
                    {
                        Log.Warn($"[AssetPreloader] hull build failed for {path}: {e.Message}");
                    }
                else
                    Log.Warn($"[AssetPreloader] could not read {path}");
                _simModels[path] = model;
            }
        });

        // Phase D: effect shaders that used to compile at first in-world spawn.
        BuildSphere.WarmShaders();
        ShieldFlash.WarmShaders();
        AlephView.WarmShaders();
    }

    public override void _Process(double delta)
    {
        // Collect finished threaded loads (LoadThreadedGet moves them into the resource cache,
        // making any later GD.Load of the path a hit).
        for (int i = _pendingScenes.Count - 1; i >= 0; i--)
        {
            string path = _pendingScenes[i];
            var status = ResourceLoader.LoadThreadedGetStatus(path);
            if (status == ResourceLoader.ThreadLoadStatus.InProgress)
                continue;
            _pendingScenes.RemoveAt(i);
            if (status == ResourceLoader.ThreadLoadStatus.Loaded && ResourceLoader.LoadThreadedGet(path) is PackedScene scene)
            {
                _scenes[path] = scene;
                _finishQueue.Enqueue(path);
            }
            else
                Log.Warn($"[AssetPreloader] threaded load failed: {path}");
        }

        // Main-thread finishing, ONE item per frame: vertex readbacks / BVH bakes cost tens of
        // ms each, so they're sliced to keep even the warm-up screens hitch-free.
        if (_finishQueue.Count > 0)
            WorldRenderer.WarmAsteroidVariant(_finishQueue.Dequeue().GetFile().GetBaseName());

        if (!_done && _pendingScenes.Count == 0 && _finishQueue.Count == 0)
        {
            _done = true;
            Log.Print($"[AssetPreloader] warm complete: {_scenes.Count} variants in {Time.GetTicksMsec() - _startMs}ms");
            SetProcess(false);
        }
    }
}
