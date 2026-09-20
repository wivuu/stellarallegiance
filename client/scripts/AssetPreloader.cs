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
//            for those same GLBs, through CollisionModels.Load — the one loader, which also owns
//            the cache and the fault ledger. Base models bake the SAME pre-rotation
//            CollisionWorld.BaseModel passes (the path-keyed cache must hold the rotated hull, or
//            prediction would collide against an unrotated station).
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
        // Salvage item meshes (guns, fuel packs): small GLBs, but a wreck drops several at once the
        // instant a ship dies, so a cold first-sight load would hitch exactly during a firefight.
        var partPaths = GlbsIn("res://assets/parts");
        paths.AddRange(basePaths);
        paths.AddRange(shipPaths);
        paths.AddRange(partPaths);
        var asteroidPaths = new List<string>();
        foreach (string v in AsteroidShapes.Variants)
            asteroidPaths.Add($"res://assets/asteroids/{v}.glb");
        paths.AddRange(asteroidPaths);

        // `--verify-assets`: judge this build's models and exit (AssetVerify) — nothing else to warm.
        if (AssetVerify.Requested)
        {
            SetProcess(false);
            GetTree().Quit(AssetVerify.Run(basePaths, shipPaths, asteroidPaths));
            return;
        }

        foreach (string path in paths)
            if (ResourceLoader.Exists(path) && ResourceLoader.LoadThreadedRequest(path) == Error.Ok)
                _pendingScenes.Add(path);

        // Phase B: collision SimModels from the raw GLB bytes on a worker (FileAccess and the
        // shared GlbReader/QuickHull are engine-free C#, safe off the main thread). Bases carry
        // the model pre-rotation so the path-keyed cache matches what CollisionWorld.BaseModel
        // would build itself — hull parity with the server is load-bearing.
        // Salvage parts are excluded: nothing on the client ever collides with a dropped item (the
        // server owns every item bounce and pickup), so building a convex hull for one would burn
        // worker time and memory on a body no code path can query.
        var hulls = new List<(string Path, Quat Pre)>();
        foreach (string path in paths)
            if (!partPaths.Contains(path))
                hulls.Add((path, basePaths.Contains(path) ? CollisionConfig.BaseModelRotation : default));
        System.Threading.Tasks.Task.Run(() =>
        {
            // A model that cannot be built is a FAULT, not a shrug: CollisionModels logs it as an error
            // and keeps it in the ledger the server browser and the HUD read (a hit is a no-op, so
            // gameplay beating us to a path costs nothing).
            foreach (var (path, pre) in hulls)
                CollisionModels.Load(path, pre);
            if (CollisionModels.Faults.Count > 0)
                Log.Error(
                    $"[Collision] {CollisionModels.Summary()} This build will mispredict collisions — "
                        + "see the FAULT lines above; `--verify-assets` reproduces the check."
                );
        });

        // Phase D: effect shaders that used to compile at first in-world spawn.
        BuildSphere.WarmShaders();
        ShieldFlash.WarmShaders();
        AlephView.WarmShaders();
    }

    // Every .glb under a res:// folder. Export builds list imported files as "<name>.glb.remap"
    // (or leave only the ".import" sidecar), so suffixes are normalized and deduped; a missing
    // folder just yields an empty list.
    //
    // Running from SOURCE the folder is the working tree, where a sidecar can outlive its GLB:
    // ".import" files are gitignored, so deleting or reverting a model leaves its sidecar (and its
    // imported scene) behind. Such an orphan is not a model this build has — listing it would warm a
    // ghost and then report a collision fault for a file that simply is not there any more. They are
    // named once and skipped. (In a package there is no raw .glb to compare against, and no orphans
    // either: the exporter only ships sidecars of files it exported.)
    private static List<string> GlbsIn(string dir)
    {
        var found = new List<string>();
        using var d = DirAccess.Open(dir);
        if (d == null)
            return found;
        var raw = new HashSet<string>();
        var listed = new List<string>();
        var seen = new HashSet<string>();
        foreach (string f in d.GetFiles())
        {
            string name = f;
            if (name.EndsWith(".glb"))
                raw.Add(name);
            if (name.EndsWith(".remap"))
                name = name.Substring(0, name.Length - ".remap".Length);
            if (name.EndsWith(".import"))
                name = name.Substring(0, name.Length - ".import".Length);
            if (name.EndsWith(".glb") && seen.Add(name))
                listed.Add(name);
        }
        var orphans = new List<string>();
        foreach (string name in listed)
            if (OS.HasFeature("editor") && !raw.Contains(name))
                orphans.Add(name);
            else
                found.Add($"{dir}/{name}");
        if (orphans.Count > 0)
            Log.Print(
                $"[AssetPreloader] {dir}: skipping {orphans.Count} orphaned import sidecar(s) whose .glb is gone "
                    + $"({string.Join(", ", orphans)}) — delete the stale *.import files to tidy up"
            );
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
            if (
                status == ResourceLoader.ThreadLoadStatus.Loaded
                && ResourceLoader.LoadThreadedGet(path) is PackedScene scene
            )
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
