using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  AssetPreloader.cs — STARTUP CACHE WARMING
//
//  Warms every cache a first join/launch/first-sight used to fill mid-gameplay, starting the
//  moment the main scene boots (under the engine splash / server-browser screen) so common
//  assets never stall a gameplay frame. Measured before this existed: the join-time world
//  restream applied ~90 frames in ONE _Process — ~2.4s — dominated by first-touch
//  asteroid-variant GD.Loads (~300ms each, the GLBs embed multi-MB textures) plus a QuickHull
//  SimModel rebuild of the same bytes (~60ms each); the spawn frame then spent ~1s in
//  ApplySectorEnv's occluder vertex readback. Base/ship GLBs had the same first-sight profile
//  (a cold garrison insert measured ~385ms: GD.Load + QuickHull + raycast BVH + occluder
//  readback), landing raw mid-flight the first time an enemy station/hull class was scouted.
//  All of that is warmable up front:
//
//    Phase A (Godot's threaded loader): every asteroid-variant, base, and ship GLB scene, so a
//            later GD.Load is a cache hit.
//    Phase B (worker Task): the shared collision SimModels (GlbReader + QuickHull, pure C#)
//            for those same GLBs, consumed by CollisionWorld.LoadGlb. Base models bake the
//            SAME pre-rotation CollisionWorld.BaseModel passes (the path-keyed cache must hold
//            the rotated hull, or prediction would collide against an unrotated station).
//    Phase C (main thread, ONE item per frame): per-mesh readbacks + shadow-occluder extremes
//            + trace BVHs (WarmAsteroidVariant / EnvironmentRenderer.WarmModelScene) — sliced
//            so even the warm itself never hitches.
//    Phase D: the per-source effect shaders (BuildSphere / ShieldFlash / AlephView) compile
//            once here instead of at their first in-world spawn.
//
//  Scope: the ASTEROID CATALOG (AsteroidShapes.Variants — wire-significant) plus every GLB in
//  assets/bases/ and assets/ships/ (small, bounded sets; a first-sight miss costs a raw
//  gameplay-frame hitch, so match-agnostic warming wins even for models a match never fields).
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
        // variant catalog is wire-significant and client-known (AsteroidShapes); base/ship GLBs
        // are enumerated from their asset folders. Bases + ships go FIRST throughout: their cold
        // first-sight cost is the largest single hitch (a garrison ≈ 300-400ms), they're few, and
        // rock inserts are time-sliced anyway so a late asteroid warm hurts far less.
        var paths = new List<string>();
        var basePaths = GlbsIn("res://assets/bases");
        var shipPaths = GlbsIn("res://assets/ships");
        paths.AddRange(basePaths);
        paths.AddRange(shipPaths);
        foreach (string v in AsteroidShapes.Variants)
            paths.Add($"res://assets/asteroids/{v}.glb");

        foreach (string path in paths)
            if (ResourceLoader.Exists(path) && ResourceLoader.LoadThreadedRequest(path) == Error.Ok)
                _pendingScenes.Add(path);

        // Phase B: collision SimModels from the raw GLB bytes on a worker (FileAccess and the
        // shared GlbReader/QuickHull are engine-free C#, safe off the main thread). Bases carry
        // the model pre-rotation so the path-keyed cache matches what CollisionWorld.BaseModel
        // would build itself — hull parity with the server is load-bearing.
        var hulls = new List<(string Path, Quat Pre)>();
        foreach (string path in paths)
            hulls.Add((path, basePaths.Contains(path) ? CollisionConfig.BaseModelRotation : default));
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var (path, pre) in hulls)
            {
                if (_simModels.ContainsKey(path))
                    continue; // gameplay beat us to it
                SimModel? model = null;
                byte[] bytes = FileAccess.GetFileAsBytes(path);
                if (bytes is { Length: > 0 })
                    try
                    {
                        model = SimModel.FromGlb(bytes, path, pre);
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

    // Every .glb under a res:// folder. Export builds list imported files as "<name>.glb.remap"
    // (or leave only the ".import" sidecar), so suffixes are normalized and deduped; a missing
    // folder just yields an empty list.
    private static List<string> GlbsIn(string dir)
    {
        var found = new List<string>();
        using var d = DirAccess.Open(dir);
        if (d == null)
            return found;
        var seen = new HashSet<string>();
        foreach (string f in d.GetFiles())
        {
            string name = f;
            if (name.EndsWith(".remap"))
                name = name.Substring(0, name.Length - ".remap".Length);
            if (name.EndsWith(".import"))
                name = name.Substring(0, name.Length - ".import".Length);
            if (name.EndsWith(".glb") && seen.Add(name))
                found.Add($"{dir}/{name}");
        }
        return found;
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
        {
            string path = _finishQueue.Dequeue();
            if (path.Contains("/assets/asteroids/"))
                WorldRenderer.WarmAsteroidVariant(path.GetFile().GetBaseName());
            else
                EnvironmentRenderer.WarmModelScene(_scenes[path]);
        }

        if (!_done && _pendingScenes.Count == 0 && _finishQueue.Count == 0)
        {
            _done = true;
            Log.Print($"[AssetPreloader] warm complete: {_scenes.Count} models in {Time.GetTicksMsec() - _startMs}ms");
            SetProcess(false);
        }
    }
}
