using StellarAllegiance.Shared;

// =====================================================================
//  collision-sidecars — write <name>.glb.simmodel beside every GLB the game collides with.
//
//      collision-sidecars <assets dir>          (the client's: client/assets)
//
//  For each model: read the GLB, build the SAME SimModel the server and a from-source client build
//  (SimModel.FromGlb → GlbReader + ConvexHull.Build, with the folder's pre-rotation), and store it in the
//  shared .simmodel format (SimModelCodec) under the key of those exact bytes. A sidecar whose key already
//  matches is left alone, so a second run costs a hash per file; a sidecar whose GLB is gone is deleted,
//  so an export never ships a model that no longer exists.
//
//  Exit code 0 = every model has a current sidecar. Anything else = do NOT export: a client packaged
//  without them predicts spheres where the server has hulls (see client/scripts/CollisionModels.cs).
// =====================================================================

// The folders whose models get a collision model, and the rigid pre-rotation baked into each. MUST agree
// with the two runtime loaders, which is why it is this short: bases are re-oriented by
// CollisionConfig.BaseModelRotation (server World.LoadBaseModel, client CollisionWorld.BaseModel /
// AssetPreloader); ships and asteroids are built as authored. Salvage parts (assets/parts) never collide
// on the client, so they get none.
(string Folder, Quat Pre)[] folders =
[
    ("bases", CollisionConfig.BaseModelRotation),
    ("ships", default),
    ("asteroids", default),
];

if (args.Length != 1 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("usage: collision-sidecars <assets dir>   (e.g. client/assets)");
    return 2;
}
string assets = Path.GetFullPath(args[0]);
if (!Directory.Exists(assets))
{
    Console.Error.WriteLine($"[sidecars] ERROR: no such directory: {assets}");
    return 2;
}

int written = 0,
    current = 0,
    removed = 0,
    failed = 0;
long bytes = 0;
foreach (var (folder, pre) in folders)
{
    string dir = Path.Combine(assets, folder);
    if (!Directory.Exists(dir))
    {
        // A missing model folder is not "nothing to do" — it is an assets dir that is not the client's.
        Console.Error.WriteLine($"[sidecars] ERROR: {dir} does not exist");
        failed++;
        continue;
    }

    string[] glbs = Directory.GetFiles(dir, "*.glb");
    Array.Sort(glbs, StringComparer.Ordinal);
    if (glbs.Length == 0)
    {
        Console.Error.WriteLine($"[sidecars] ERROR: {dir} holds no .glb models");
        failed++;
    }
    foreach (string glb in glbs)
    {
        string sidecar = glb + SimModelCodec.SidecarSuffix;
        try
        {
            byte[] data = File.ReadAllBytes(glb);
            byte[] key = SimModelCodec.KeyHash(data, pre);
            if (IsCurrent(sidecar, key))
            {
                current++;
                bytes += new FileInfo(sidecar).Length;
                continue;
            }

            SimModel model = SimModel.FromGlb(data, Path.GetFileName(glb), pre);
            if (model.LongestAxis <= 1e-3f)
                throw new InvalidDataException("the GLB holds no usable geometry (degenerate hull)");
            byte[] encoded = SimModelCodec.Encode(key, model);
            // Prove the bytes before anything can ship them: they must decode, under the same key.
            if (!SimModelCodec.TryDecode(encoded, out byte[] readKey, out _) || !readKey.AsSpan().SequenceEqual(key))
                throw new InvalidDataException("the encoded model does not read back");

            // Whole file or no file: an export that races an interrupted run must not pick up half a model.
            string tmp = sidecar + ".tmp";
            File.WriteAllBytes(tmp, encoded);
            File.Move(tmp, sidecar, overwrite: true);
            written++;
            bytes += encoded.Length;
            Console.WriteLine(
                $"[sidecars]   {folder}/{Path.GetFileName(sidecar)}  {encoded.Length, 7:N0} B  "
                    + $"{model.Hull.Planes.Length} planes, {(model.Hulls.Count == 1 && ReferenceEquals(model.Hulls[0], model.Hull) ? 0 : model.Hulls.Count)} parts, {model.Hardpoints.Count} hardpoints"
            );
        }
        catch (Exception e)
        {
            failed++;
            Console.Error.WriteLine($"[sidecars] ERROR: {folder}/{Path.GetFileName(glb)}: {e.Message}");
        }
    }

    // A sidecar that outlived its GLB (a deleted or renamed model) would ship as a ghost.
    foreach (string stale in Directory.GetFiles(dir, "*" + SimModelCodec.SidecarSuffix))
    {
        string itsGlb = stale.Substring(0, stale.Length - SimModelCodec.SidecarSuffix.Length);
        if (File.Exists(itsGlb))
            continue;
        File.Delete(stale);
        removed++;
        Console.WriteLine($"[sidecars]   removed {folder}/{Path.GetFileName(stale)} (its GLB is gone)");
    }
}

Console.WriteLine(
    $"[sidecars] {written + current} collision sidecars ({written} written, {current} already current, {removed} removed) — "
        + $"{bytes / 1024.0:0.0} KiB under {assets}"
);
if (failed > 0)
    Console.Error.WriteLine($"[sidecars] ERROR: {failed} problem(s) — do not export this tree");
return failed == 0 ? 0 : 1;

// Current = a readable, current-version sidecar stored under exactly this key (same GLB bytes, same
// pre-rotation). The WHOLE file is decoded, not just its header: a truncated sidecar must be rewritten.
static bool IsCurrent(string sidecar, byte[] key)
{
    if (!File.Exists(sidecar))
        return false;
    try
    {
        using var fs = File.OpenRead(sidecar);
        return SimModelCodec.TryRead(fs, key, out _, out _);
    }
    catch (IOException)
    {
        return false;
    }
}
