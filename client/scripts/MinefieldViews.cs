using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// =====================================================================
//  MinefieldViews.cs — CLIENT MINEFIELD VISUALS (Track B)
//
//  One container node under WorldRenderer owning the live minefield mesh clouds. GameNetClient
//  decodes MsgMinefields/MsgMineGone and WorldRenderer.NetUpsertMinefield/NetMineGone forward here.
//
//  A field is a single MultiMeshInstance3D of the mine model (assets/mines/<ModelName>.glb, e.g.
//  acs41) — one draw call for up to 64 mine meshes. The mesh instances are COSMETIC only: the
//  server treats the whole field as one damage volume (see server StepMines / DamageFieldVolume),
//  so nothing is ever hit-detected here and the meshes never individually pop. Each mesh's local
//  offset is regenerated from the field Seed via the shared MinefieldLayout.Positions (the exact
//  generator the server uses), so no per-mine position is ever sent; a deterministic per-instance
//  rotation (seed-keyed) gives the cloud variety without popping on a resync. A field self-frees on
//  expiry (ServerTick past ExpireAtTick) and on Clear().
//
//  MsgMineGone is now a hit-FX ping (reason 2): the server rate-limits it per victim while a ship
//  sits inside a lethal field, and we answer with a small explosion + pop at the reported position —
//  it depletes nothing.
// =====================================================================
public partial class MinefieldViews : Node3D
{
    // Target longest-axis size (world units) each mine mesh is normalized to — small, so a mine
    // reads as an object clearly smaller than a ship (scout ≈ 4.5u long).
    private const float MineSize = 1.5f;

    // Deploy expansion: a freshly-laid field's cloud grows from clustered-at-center out to the
    // seed layout over DeployDuration with an ease-out cubic (rapid then settle). StartFactor is
    // the initial fraction of each instance's final offset (near-zero = emerge from the center).
    private const float DeployDuration = 0.35f;
    private const float StartFactor = 0.03f;

    // The mine mesh (extracted from the GLB once) + the uniform scale that normalizes it to MineSize,
    // cached per res:// path so every field shares one Mesh resource.
    private static readonly Dictionary<string, (Mesh Mesh, float Scale)> _meshCache = new();

    private sealed class FieldView
    {
        public MultiMeshInstance3D Node = null!;
        public byte Team;
        public float SecondsLeft; // TTL to self-free at ExpireAtTick (refreshed on each Upsert)

        // Per-instance final transforms (origin + rotation×scale basis), retained so _Process can
        // re-drive the origins during the deploy animation and converge to the exact seed layout.
        public Vector3[] FinalOrigins = null!;
        public Basis[] Bases = null!;
        public float DeployElapsed = -1f; // seconds into the expand; < 0 = not animating / done
    }

    private readonly Dictionary<ulong, FieldView> _fields = new();

    // Scratch reused by VisibleMinefields() so the per-frame HUD marker pass allocates nothing.
    private readonly List<(Vector3 Pos, byte Team)> _visibleScratch = new();

    // Reconcile/insert a field's mesh cloud from its seed + aliveMask.
    public void Upsert(Minefield row, WeaponDef? def, uint serverTick)
    {
        // The cloud geometry rides the WeaponDef; without it we can't size or place the cloud.
        if (def is null)
            return;

        if (_fields.TryGetValue(row.FieldId, out var existing))
        {
            // Live field: just refresh the self-free countdown (fields stream on a coarse keepalive).
            existing.SecondsLeft = row.ExpireAtTick > serverTick ? (row.ExpireAtTick - serverTick) * FlightModel.Dt : 0f;
            return;
        }

        // Mine count = highest set bit + 1 (AliveMask begins contiguous from bit 0). The server keeps
        // it full for the field's life, so this is the cosmetic mesh count.
        int n = HighBitCount(row.AliveMask);
        bool armed = serverTick >= row.ArmAtTick;

        var node = BuildCloud(row, def, n, out var finalOrigins, out var bases);
        node.Position = new Vector3(row.CenterX, row.CenterY, row.CenterZ);
        AddChild(node);

        var fv = new FieldView
        {
            Node = node,
            Team = row.Team,
            SecondsLeft = row.ExpireAtTick > serverTick ? (row.ExpireAtTick - serverTick) * FlightModel.Dt : 0f,
            FinalOrigins = finalOrigins,
            Bases = bases,
            DeployElapsed = armed ? -1f : 0f, // only a freshly-laid (un-armed) field animates open
        };
        _fields[row.FieldId] = fv;

        // Freshly laid: collapse every instance to the center so _Process expands it out to the layout.
        if (!armed)
        {
            var mm = node.Multimesh;
            for (int i = 0; i < finalOrigins.Length; i++)
                mm.SetInstanceTransform(i, new Transform3D(bases[i], finalOrigins[i] * StartFactor));
        }

        // Deploy cue: a field first seen while still arming was just laid (fields discovered mid-life —
        // sector entry, reconnect — stay silent). The layer has no HUD row focus, so this is the
        // pilot's confirmation the drop happened.
        if (!armed)
            SfxManager.Instance?.PlayAt(SfxManager.SfxId.MissileLaunch, node.Position, pitch: 0.7f, volumeDb: -6f);
    }

    // Build the field's MultiMeshInstance3D: n mine meshes at their seed-regenerated local offsets,
    // each normalized to MineSize and given a deterministic per-instance rotation. Falls back to a
    // small box marker mesh if the mine GLB is unavailable (never-invisible guarantee).
    private static MultiMeshInstance3D BuildCloud(Minefield row, WeaponDef def, int n, out Vector3[] finalOrigins, out Basis[] bases)
    {
        var (mesh, scale) = LoadMineMesh(def.ModelName);

        var offsets = new Vec3[n];
        MinefieldLayout.Positions(row.Seed, n, def.MineCloudRadius, offsets);

        var mm = new MultiMesh
        {
            Mesh = mesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = n,
        };
        finalOrigins = new Vector3[n];
        bases = new Basis[n];
        for (int i = 0; i < n; i++)
        {
            // Deterministic per-instance tumble from (seed, index) so orientation never pops on a
            // resync. Three independent [0,2π) eulers.
            float rx = MinefieldLayout.Hash01(row.Seed, (ulong)(i * 3 + 1)) * Mathf.Tau;
            float ry = MinefieldLayout.Hash01(row.Seed, (ulong)(i * 3 + 2)) * Mathf.Tau;
            float rz = MinefieldLayout.Hash01(row.Seed, (ulong)(i * 3 + 3)) * Mathf.Tau;
            var basis = Basis.FromEuler(new Vector3(rx, ry, rz)) * Basis.FromScale(Vector3.One * scale);
            var origin = new Vector3(offsets[i].X, offsets[i].Y, offsets[i].Z);
            finalOrigins[i] = origin;
            bases[i] = basis;
            mm.SetInstanceTransform(i, new Transform3D(basis, origin));
        }

        return new MultiMeshInstance3D
        {
            Name = $"Field_{row.FieldId}",
            Multimesh = mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // A hit-FX ping (reason 2): a ship is taking damage inside this field. Play a small explosion +
    // pop at the reported world position. Depletes nothing — the meshes are cosmetic and persistent.
    public void MineGone(ulong fieldId, byte mineIndex, byte reason, Vector3 pos)
    {
        byte team = _fields.TryGetValue(fieldId, out var fv) ? fv.Team : (byte)0;

        // Small blast (well under the field's damage radius) + a quiet pop, tinted to the owning team.
        var boom = ExplosionEffect.CreateBlast(10f, team);
        AddChild(boom);
        boom.Position = pos;
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, pos, pitch: 1.5f, volumeDb: -8f);
    }

    public override void _Process(double delta)
    {
        // Deploy expansion: grow each animating field's cloud from center out to the seed layout.
        foreach (var fv in _fields.Values)
        {
            if (fv.DeployElapsed < 0f)
                continue;
            fv.DeployElapsed += (float)delta;
            float t = Mathf.Min(fv.DeployElapsed / DeployDuration, 1f);
            float u = 1f - t;
            float k = 1f - u * u * u; // ease-out cubic
            var mm = fv.Node.Multimesh;
            for (int i = 0; i < fv.FinalOrigins.Length; i++)
                mm.SetInstanceTransform(i, new Transform3D(fv.Bases[i], fv.FinalOrigins[i] * k));
            if (k >= 1f)
                fv.DeployElapsed = -1f; // settled at finals (k==1 wrote them above)
        }

        // Iterate over a snapshot so a self-free during expiry doesn't mutate mid-enumeration.
        List<ulong>? expired = null;
        foreach (var (id, fv) in _fields)
        {
            fv.SecondsLeft -= (float)delta;
            if (fv.SecondsLeft <= 0f)
                (expired ??= new()).Add(id);
        }
        if (expired is not null)
            foreach (var id in expired)
                if (_fields.TryGetValue(id, out var fv2))
                    FreeField(id, fv2);
    }

    // Feed for the HUD mine glyph: center + owning team of every live field. Mirrors
    // WorldRenderer.VisibleProbes(). The set is already fog-filtered upstream (own team +
    // radar/LOS-revealed enemy). Returns a shared scratch list — read it immediately.
    public IReadOnlyList<(Vector3 Pos, byte Team)> VisibleMinefields()
    {
        _visibleScratch.Clear();
        foreach (var fv in _fields.Values)
            _visibleScratch.Add((fv.Node.Position, fv.Team));
        return _visibleScratch;
    }

    // Free every field (WorldRenderer Reset / phase→Lobby).
    public void Clear()
    {
        foreach (var fv in _fields.Values)
            fv.Node.QueueFree();
        _fields.Clear();
    }

    private void FreeField(ulong fieldId, FieldView fv)
    {
        fv.Node.QueueFree();
        _fields.Remove(fieldId);
    }

    // Highest set bit index + 1 (== the mine count, since AliveMask begins contiguous from bit 0).
    private static int HighBitCount(ulong mask)
    {
        int n = 0;
        while (mask != 0UL)
        {
            n++;
            mask >>= 1;
        }
        return n;
    }

    // Extract the mine Mesh from assets/mines/<modelName>.glb (once, cached) + the uniform scale that
    // normalizes it to MineSize. Falls back to a small box mesh when the GLB is absent so a field is
    // never invisible. The GLB scene is freed after extraction; the Mesh resource stays referenced.
    private static (Mesh Mesh, float Scale) LoadMineMesh(string? modelName)
    {
        string path = string.IsNullOrEmpty(modelName) ? "" : $"res://assets/mines/{modelName}.glb";
        if (_meshCache.TryGetValue(path, out var cached))
            return cached;

        (Mesh Mesh, float Scale) result = FallbackMesh();
        if (path.Length > 0 && GlbLoader.Load(path) is { } scene)
        {
            if (FindMesh(scene) is { Mesh: { } m })
            {
                Aabb b = m.GetAabb();
                float longest = Mathf.Max(b.Size.X, Mathf.Max(b.Size.Y, b.Size.Z));
                result = (m, longest > 1e-4f ? MineSize / longest : 1f);
            }
            scene.QueueFree();
        }

        _meshCache[path] = result;
        return result;
    }

    private static (Mesh Mesh, float Scale) FallbackMesh()
    {
        var box = new BoxMesh
        {
            Size = Vector3.One,
            Material = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.72f, 0.78f), Metallic = 0.6f, Roughness = 0.4f },
        };
        return (box, MineSize);
    }

    private static MeshInstance3D? FindMesh(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh is not null)
            return mi;
        foreach (Node child in node.GetChildren())
            if (FindMesh(child) is { } found)
                return found;
        return null;
    }
}
