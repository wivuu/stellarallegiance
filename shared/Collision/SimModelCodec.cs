using System.Security.Cryptography;

namespace StellarAllegiance.Shared;

// =====================================================================
//  SimModelCodec.cs — THE `.simmodel` BYTE FORMAT: a built SimModel (merged hull planes + hardpoints +
//  compound sub-hulls) with the key of the GLB it was built from. One format, two jobs:
//
//    - the SERVER's hull cache (server/Assets SimModelCache → <cache dir>/<name>.simmodel): skip the
//      QuickHull on every boot after the first; the key says whether the GLB changed since.
//    - the CLIENT's COLLISION SIDECARS (client/assets/**/<name>.glb.simmodel, written at export time by
//      tools/collision-sidecars, shipped through the presets' include_filter): the ONLY collision data a
//      packaged client has. Godot's exporter replaces an imported .glb by its imported scene, so the raw
//      bytes SimModel.FromGlb needs are never in a package — v0.0.13 and v0.0.14 shipped with nothing,
//      and predicted a sphere where the server had a station (client/scripts/CollisionModels.cs).
//
//  What is stored is the RESULT of ConvexHull.Build — face planes, as the exact floats it produced — not
//  the vertices, so a reader never re-hulls and cannot drift: a decoded model resolves contacts
//  bit-identically to one built from the GLB, on any machine. That is the whole point of sharing it.
//
//  Layout (little-endian, BinaryWriter; strings are its 7-bit-length-prefixed UTF-8):
//      u32 magic "SMDL" · i32 version · 32-byte key
//      f32 boundingRadius · f32 longestAxis · i32 planeCount · planeCount × (f32 nx, ny, nz, d)
//      i32 hardpointCount · hardpointCount × (string name, f32 pos xyz, f32 forward xyz)
//      i32 hullCount · hullCount × (f32 boundingRadius, f32 longestAxis, i32 planeCount, planes…)
//  hullCount 0 = a PARTLESS model (every ship / asteroid / un-baked GLB): its Hulls list aliases the
//  merged hull, exactly as a fresh SimModel's does. Version 2 added that last block; a v1 file fails
//  the version gate and its owner rebuilds it.
//
//  The bytes are unchanged from when this lived inside the server cache — existing cache files stay
//  valid, and tests/CollisionTest pins the round trip.
// =====================================================================
public static class SimModelCodec
{
    public const uint Magic = 0x4C444D53; // "SMDL"
    public const int Version = 2;
    public const int KeyLength = 32;

    // Appended to the GLB's own file name: garrison.glb → garrison.glb.simmodel. Not an extension Godot
    // imports, so an include_filter ships it as a plain file a packaged client can read.
    public const string SidecarSuffix = ".simmodel";

    // Refuse absurd counts before allocating for them: a truncated or foreign file must read as
    // "not a model", never as an out-of-memory. Real models are far below these (a base has ~60
    // merged planes and a few dozen parts; a rock a few hundred planes).
    private const int MaxPlanes = 1 << 20;
    private const int MaxHardpoints = 1 << 16;
    private const int MaxHulls = 1 << 16;

    // Key = SHA-256 of the GLB bytes, with a non-identity pre-rotation's four quaternion floats mixed
    // in, so re-orienting a model invalidates what was built from it. An identity `pre` (default or
    // W=1) hashes the bare bytes, which keeps every un-rotated ship/asteroid key equal to the file hash.
    public static byte[] KeyHash(ReadOnlySpan<byte> glb, Quat pre)
    {
        if (pre.X == 0f && pre.Y == 0f && pre.Z == 0f && (pre.W == 0f || pre.W == 1f))
            return SHA256.HashData(glb);
        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ih.AppendData(glb);
        Span<byte> q = stackalloc byte[16];
        BitConverter.TryWriteBytes(q.Slice(0, 4), pre.X);
        BitConverter.TryWriteBytes(q.Slice(4, 4), pre.Y);
        BitConverter.TryWriteBytes(q.Slice(8, 4), pre.Z);
        BitConverter.TryWriteBytes(q.Slice(12, 4), pre.W);
        ih.AppendData(q);
        return ih.GetHashAndReset();
    }

    public static void Write(Stream stream, ReadOnlySpan<byte> key, SimModel model)
    {
        if (key.Length != KeyLength)
            throw new ArgumentException($"a .simmodel key is {KeyLength} bytes, got {key.Length}", nameof(key));
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(key);
        WriteHull(w, model.Hull);
        w.Write(model.Hardpoints.Count);
        foreach (var (name, pos, fwd) in model.Hardpoints)
        {
            w.Write(name);
            w.Write(pos.X);
            w.Write(pos.Y);
            w.Write(pos.Z);
            w.Write(fwd.X);
            w.Write(fwd.Y);
            w.Write(fwd.Z);
        }

        // A partless model's Hulls aliases the single merged hull (same object reference) → persist 0
        // so ship/asteroid files stay minimal and the reader re-aliases [Hull]. A baked base persists
        // each authored part's planes (already-built hulls, not vertices).
        bool partless = model.Hulls.Count == 1 && ReferenceEquals(model.Hulls[0], model.Hull);
        w.Write(partless ? 0 : model.Hulls.Count);
        if (!partless)
            foreach (var h in model.Hulls)
                WriteHull(w, h);
    }

    public static byte[] Encode(ReadOnlySpan<byte> key, SimModel model)
    {
        using var ms = new MemoryStream();
        Write(ms, key, model);
        return ms.ToArray();
    }

    // Just the key of a stored model (header only) — enough to tell whether a file is still current
    // for its GLB without decoding the rest. False for anything that is not a current-version file.
    public static bool TryReadKey(Stream stream, out byte[] key)
    {
        key = [];
        try
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version)
                return false;
            byte[] k = r.ReadBytes(KeyLength);
            if (k.Length != KeyLength)
                return false;
            key = k;
            return true;
        }
        catch (IOException)
        {
            return false; // EndOfStreamException ⊂ IOException — truncated
        }
    }

    // Decode a stored model. `expectKey`, when given, must match the stored key or the read stops
    // there (the server cache: "is this still the file for THAT GLB?"). A packaged client passes null —
    // it has no GLB to hash, and the sidecar was built from the very files it was exported beside.
    // False = not a usable current-version model (wrong magic/version/key, truncated, garbled); never throws
    // for bad DATA.
    public static bool TryRead(Stream stream, ReadOnlySpan<byte> expectKey, out byte[] key, out SimModel? model)
    {
        model = null;
        if (!TryReadKey(stream, out key))
            return false;
        if (!expectKey.IsEmpty && !expectKey.SequenceEqual(key))
            return false;
        try
        {
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (!TryReadHull(r, out ConvexHull? merged))
                return false;

            int hpCount = r.ReadInt32();
            if (hpCount < 0 || hpCount > MaxHardpoints)
                return false;
            var hps = new List<(string, Vec3, Vec3)>(hpCount);
            for (int i = 0; i < hpCount; i++)
            {
                string name = r.ReadString();
                var pos = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var fwd = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                hps.Add((name, pos, fwd));
            }

            // 0 ⇒ partless: FromPrebuilt(null) aliases [merged] exactly like a fresh partless SimModel
            // (single hull, zero drift). >0 ⇒ each authored part from its stored planes — NO QuickHull.
            int hullCount = r.ReadInt32();
            if (hullCount < 0 || hullCount > MaxHulls)
                return false;
            ConvexHull[]? subHulls = null;
            if (hullCount > 0)
            {
                subHulls = new ConvexHull[hullCount];
                for (int h = 0; h < hullCount; h++)
                {
                    if (!TryReadHull(r, out ConvexHull? sub))
                        return false;
                    subHulls[h] = sub!;
                }
            }
            model = SimModel.FromPrebuilt(merged!, hps, subHulls);
            return true;
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException)
        {
            // Truncated (EndOfStream), or a string length/encoding that is not one — garbled either way.
            model = null;
            return false;
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out byte[] key, out SimModel? model)
    {
        using var ms = new MemoryStream(bytes.ToArray(), writable: false);
        return TryRead(ms, default, out key, out model);
    }

    private static void WriteHull(BinaryWriter w, ConvexHull hull)
    {
        w.Write(hull.BoundingRadius);
        w.Write(hull.LongestAxis);
        w.Write(hull.Planes.Length);
        foreach (var p in hull.Planes)
        {
            w.Write(p.N.X);
            w.Write(p.N.Y);
            w.Write(p.N.Z);
            w.Write(p.D);
        }
    }

    private static bool TryReadHull(BinaryReader r, out ConvexHull? hull)
    {
        hull = null;
        float boundingRadius = r.ReadSingle();
        float longestAxis = r.ReadSingle();
        int planeCount = r.ReadInt32();
        if (planeCount < 0 || planeCount > MaxPlanes)
            return false;
        var planes = new ConvexHull.Plane[planeCount];
        for (int i = 0; i < planeCount; i++)
            planes[i] = new ConvexHull.Plane(new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), r.ReadSingle());
        hull = ConvexHull.FromPlanes(planes, boundingRadius, longestAxis);
        return true;
    }
}
