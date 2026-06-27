using System.Text.Json;

namespace StellarAllegiance.Shared;

// =====================================================================
//  GlbReader.cs — MINIMAL IN-REPO GLB PARSER (no Godot, no NuGet)
//
//  The native sim has no engine to load art, but it needs the SAME geometry the Godot
//  client renders so collision/hit-detection and hardpoints line up. This reads just the
//  subset we need from a binary .glb:
//    - every mesh's POSITION vertices, transformed by their node's world matrix (mirrors
//      the client's GlbLoader.MeshAabb tree walk), as a point cloud for the convex hull;
//    - every HP_<Kind>_<Index> node's world position + forward (local +Z), the same
//      hardpoint contract the client/BaseModelLoader uses.
//
//  Scope is deliberately tiny: GLB container + glTF nodes/meshes/accessors for FLOAT VEC3
//  POSITION. No animations, skins, sparse accessors, or non-float positions (our authored
//  assets use none). Lives in shared/ so the CLIENT can build the same hulls from its own
//  res:// GLB bytes (via Parse) while the server reads them from disk (via Read).
// =====================================================================
public sealed class GlbModel
{
    // Mesh POSITION vertices in the GLB's scene-graph world space (node transforms applied).
    public readonly List<Vec3> Vertices = new();

    // HP_ nodes: name, world position, world forward (the node's local +Z).
    public readonly List<(string Name, Vec3 Pos, Vec3 Forward)> Hardpoints = new();
}

public static class GlbReader
{
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint ChunkJson = 0x4E4F534A; // "JSON"
    private const uint ChunkBin = 0x004E4942; // "BIN\0"

    // Server path: read the .glb from disk and parse. The client uses Parse() directly with bytes
    // it pulls from res:// via Godot FileAccess (same bytes → same hull).
    public static GlbModel Read(string path) => Parse(File.ReadAllBytes(path), path);

    public static GlbModel Parse(byte[] bytes, string label = "<glb>")
    {
        if (bytes.Length < 12 || BitConverter.ToUInt32(bytes, 0) != GlbMagic)
            throw new InvalidDataException($"{label}: not a GLB file");

        // Chunk scan: a JSON chunk then (usually) a BIN chunk.
        ReadOnlySpan<byte> json = default;
        byte[]? bin = null;
        int off = 12;
        while (off + 8 <= bytes.Length)
        {
            uint len = BitConverter.ToUInt32(bytes, off);
            uint type = BitConverter.ToUInt32(bytes, off + 4);
            int dataAt = off + 8;
            if (dataAt + (int)len > bytes.Length)
                break;
            if (type == ChunkJson)
                json = bytes.AsSpan(dataAt, (int)len);
            else if (type == ChunkBin)
                bin = bytes.AsSpan(dataAt, (int)len).ToArray();
            off = dataAt + (int)len;
            if ((len & 3) != 0)
                off += 4 - (int)(len & 3); // chunks are 4-byte aligned
        }
        if (json.IsEmpty)
            throw new InvalidDataException($"{label}: no glTF JSON chunk");

        using var doc = JsonDocument.Parse(json.ToArray());
        JsonElement root = doc.RootElement;
        var model = new GlbModel();

        JsonElement nodes = GetArray(root, "nodes");
        JsonElement meshes = GetArray(root, "meshes");
        JsonElement accessors = GetArray(root, "accessors");
        JsonElement views = GetArray(root, "bufferViews");

        // Roots = nodes that are nobody's child. Walk the hierarchy accumulating world matrices.
        int nodeCount = nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;
        var isChild = new bool[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            foreach (int c in Children(nodes[i]))
                if (c >= 0 && c < nodeCount)
                    isChild[c] = true;

        for (int i = 0; i < nodeCount; i++)
            if (!isChild[i])
                Walk(i, Mat4.Identity, nodes, meshes, accessors, views, bin, model);

        return model;
    }

    private static void Walk(
        int nodeIndex,
        Mat4 parent,
        JsonElement nodes,
        JsonElement meshes,
        JsonElement accessors,
        JsonElement views,
        byte[]? bin,
        GlbModel model
    )
    {
        JsonElement node = nodes[nodeIndex];
        Mat4 world = parent * LocalMatrix(node);

        if (
            node.TryGetProperty("name", out var nameEl)
            && nameEl.GetString() is string name
            && name.StartsWith("HP_", StringComparison.Ordinal)
        )
        {
            Vec3 pos = world.TransformPoint(new Vec3(0f, 0f, 0f));
            Vec3 fwd = Normalize(world.TransformDir(new Vec3(0f, 0f, 1f)));
            model.Hardpoints.Add((name, pos, fwd));
        }

        if (node.TryGetProperty("mesh", out var meshEl) && meshEl.TryGetInt32(out int meshIdx) && bin is not null)
            AddMeshVertices(meshes[meshIdx], accessors, views, bin, world, model.Vertices);

        foreach (int c in Children(node))
            Walk(c, world, nodes, meshes, accessors, views, bin, model);
    }

    private static void AddMeshVertices(
        JsonElement mesh,
        JsonElement accessors,
        JsonElement views,
        byte[] bin,
        Mat4 world,
        List<Vec3> outVerts
    )
    {
        if (!mesh.TryGetProperty("primitives", out var prims))
            return;
        foreach (JsonElement prim in prims.EnumerateArray())
        {
            if (!prim.TryGetProperty("attributes", out var attrs))
                continue;
            if (!attrs.TryGetProperty("POSITION", out var posEl) || !posEl.TryGetInt32(out int accIdx))
                continue;
            JsonElement acc = accessors[accIdx];
            // Expect FLOAT (5126) VEC3.
            if (GetInt(acc, "componentType", 0) != 5126)
                continue;
            if (GetString(acc, "type") != "VEC3")
                continue;
            int count = GetInt(acc, "count", 0);
            int accByteOff = GetInt(acc, "byteOffset", 0);
            int viewIdx = GetInt(acc, "bufferView", -1);
            if (viewIdx < 0)
                continue;
            JsonElement view = views[viewIdx];
            int viewOff = GetInt(view, "byteOffset", 0);
            int stride = GetInt(view, "byteStride", 12);
            if (stride <= 0)
                stride = 12;
            int basePos = viewOff + accByteOff;
            for (int i = 0; i < count; i++)
            {
                int p = basePos + i * stride;
                if (p + 12 > bin.Length)
                    break;
                float x = BitConverter.ToSingle(bin, p);
                float y = BitConverter.ToSingle(bin, p + 4);
                float z = BitConverter.ToSingle(bin, p + 8);
                outVerts.Add(world.TransformPoint(new Vec3(x, y, z)));
            }
        }
    }

    // ---- glTF node local transform ----

    private static Mat4 LocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Array)
        {
            var a = new float[16];
            int i = 0;
            foreach (var e in m.EnumerateArray())
            {
                if (i < 16)
                    a[i++] = (float)e.GetDouble();
            }
            return new Mat4(a);
        }
        Vec3 t = ReadVec3(node, "translation", new Vec3(0f, 0f, 0f));
        Quat r = ReadQuat(node, "rotation");
        Vec3 s = ReadVec3(node, "scale", new Vec3(1f, 1f, 1f));
        return Mat4.Trs(t, r, s);
    }

    private static IEnumerable<int> Children(JsonElement node)
    {
        if (node.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var e in c.EnumerateArray())
                if (e.TryGetInt32(out int v))
                    yield return v;
    }

    // ---- JSON helpers ----

    private static JsonElement GetArray(JsonElement root, string name) => root.TryGetProperty(name, out var e) ? e : default;

    private static int GetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var e) && e.TryGetInt32(out int v) ? v : fallback;

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var e) ? e.GetString() ?? "" : "";

    private static Vec3 ReadVec3(JsonElement node, string name, Vec3 fallback)
    {
        if (!node.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array)
            return fallback;
        var v = new float[3];
        int i = 0;
        foreach (var e in a.EnumerateArray())
        {
            if (i < 3)
                v[i++] = (float)e.GetDouble();
        }
        return new Vec3(v[0], v[1], v[2]);
    }

    private static Quat ReadQuat(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array)
            return Quat.Identity;
        var v = new float[4];
        int i = 0;
        foreach (var e in a.EnumerateArray())
        {
            if (i < 4)
                v[i++] = (float)e.GetDouble();
        }
        return new Quat(v[0], v[1], v[2], v[3]);
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float len = v.Length();
        return len > 1e-6f ? v * (1f / len) : new Vec3(0f, 0f, 1f);
    }
}

// Tiny column-major 4x4 (glTF convention) — only what GlbReader needs.
internal readonly struct Mat4
{
    private readonly float[] _m; // column-major: _m[col*4 + row]

    public Mat4(float[] m)
    {
        _m = m;
    }

    public static Mat4 Identity => new(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });

    public static Mat4 Trs(Vec3 t, Quat q, Vec3 s)
    {
        float x = q.X,
            y = q.Y,
            z = q.Z,
            w = q.W;
        // row-major rotation entries Rij
        float r00 = 1 - 2 * (y * y + z * z),
            r01 = 2 * (x * y - w * z),
            r02 = 2 * (x * z + w * y);
        float r10 = 2 * (x * y + w * z),
            r11 = 1 - 2 * (x * x + z * z),
            r12 = 2 * (y * z - w * x);
        float r20 = 2 * (x * z - w * y),
            r21 = 2 * (y * z + w * x),
            r22 = 1 - 2 * (x * x + y * y);
        return new Mat4(
            new float[]
            {
                r00 * s.X,
                r10 * s.X,
                r20 * s.X,
                0f,
                r01 * s.Y,
                r11 * s.Y,
                r21 * s.Y,
                0f,
                r02 * s.Z,
                r12 * s.Z,
                r22 * s.Z,
                0f,
                t.X,
                t.Y,
                t.Z,
                1f,
            }
        );
    }

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var c = new float[16];
        for (int col = 0; col < 4; col++)
        for (int row = 0; row < 4; row++)
        {
            float sum = 0f;
            for (int k = 0; k < 4; k++)
                sum += a._m[k * 4 + row] * b._m[col * 4 + k];
            c[col * 4 + row] = sum;
        }
        return new Mat4(c);
    }

    public Vec3 TransformPoint(Vec3 v) =>
        new(
            _m[0] * v.X + _m[4] * v.Y + _m[8] * v.Z + _m[12],
            _m[1] * v.X + _m[5] * v.Y + _m[9] * v.Z + _m[13],
            _m[2] * v.X + _m[6] * v.Y + _m[10] * v.Z + _m[14]
        );

    public Vec3 TransformDir(Vec3 v) =>
        new(
            _m[0] * v.X + _m[4] * v.Y + _m[8] * v.Z,
            _m[1] * v.X + _m[5] * v.Y + _m[9] * v.Z,
            _m[2] * v.X + _m[6] * v.Y + _m[10] * v.Z
        );
}
