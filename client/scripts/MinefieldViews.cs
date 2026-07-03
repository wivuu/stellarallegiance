using System.Collections.Generic;
using Godot;
using StellarAllegiance.Net;
using StellarAllegiance.Shared;

// =====================================================================
//  MinefieldViews.cs — CLIENT MINEFIELD VISUALS (Track B)
//
//  One container node under WorldRenderer owning the live minefield sprite clouds. GameNetClient
//  decodes MsgMinefields/MsgMineGone and WorldRenderer.NetUpsertMinefield/NetMineGone forward here.
//
//  Each field is a small Node3D parked at the wire Center, holding one billboard "◈" diamond quad
//  per mine. The mine LOCAL offsets are regenerated from the field Seed via the shared
//  MinefieldLayout.Positions — the exact generator the server sim uses — so no per-mine position is
//  ever sent. A mine's quad is shown while its AliveMask bit is set (reconciled every frame) and
//  hidden the instant it pops. Unarmed fields glow dim; an armed field's mines pulse brighter. A
//  field self-frees on expiry (ServerTick past ExpireAtTick), when fully depleted, and on Clear().
//  The team tint (cyan = friendly, warm = enemy) tells whose field it is.
// =====================================================================
public partial class MinefieldViews : Node3D
{
    // Shared diamond glyph texture (built once) — a bright "◈" double-diamond, tinted per field.
    private static readonly ImageTexture DiamondTex = BuildDiamondTexture();

    // World-unit edge length of a mine billboard quad.
    private const float MineSize = 6f;

    private sealed class FieldView
    {
        public Node3D Container = null!;
        public StandardMaterial3D Mat = null!; // one tinted additive material shared by the field's quads
        public readonly List<MeshInstance3D> Mines = new();
        public byte Team;
        public float BlastRadius;
        public uint Seed;
        public float CloudRadius;
        public bool Armed;
        public float SecondsLeft; // TTL to self-free at ExpireAtTick (refreshed on each Upsert)
    }

    private readonly Dictionary<ulong, FieldView> _fields = new();
    private double _pulse; // shared phase for the armed-mine breathing glow

    // Reconcile/insert a field's sprite cloud from its seed + aliveMask.
    public void Upsert(Minefield row, WeaponDef? def, uint serverTick)
    {
        // The mine cloud/blast geometry rides the WeaponDef; without it we can't size the cloud.
        if (def is null)
            return;

        // Mine count = highest set bit + 1. The server's AliveMask starts (1<<n)-1, and
        // MinefieldLayout.Positions is a deterministic prefix, so this regenerates the exact live
        // offsets even if we first see the field mid-depletion (already-popped low bits just hide).
        int n = HighBitCount(row.AliveMask);
        bool armed = serverTick >= row.ArmAtTick;

        if (!_fields.TryGetValue(row.FieldId, out var fv))
        {
            fv = new FieldView
            {
                Container = new Node3D { Name = $"Field_{row.FieldId}", Position = new Vector3(row.CenterX, row.CenterY, row.CenterZ) },
                Mat = MineMaterial(row.Team),
                Team = row.Team,
                BlastRadius = def.BlastRadius,
                Seed = row.Seed,
                CloudRadius = def.MineCloudRadius,
            };
            AddChild(fv.Container);
            _fields[row.FieldId] = fv;
        }

        fv.BlastRadius = def.BlastRadius;
        fv.Armed = armed;
        // Refresh the self-free countdown from the authoritative expiry (fields stream on a coarse
        // keepalive, so this stays accurate even between changes).
        fv.SecondsLeft = row.ExpireAtTick > serverTick ? (row.ExpireAtTick - serverTick) * FlightModel.Dt : 0f;

        // Grow the quad list to n, placing each new quad at its regenerated local offset.
        if (fv.Mines.Count < n)
        {
            var offsets = new Vec3[n];
            MinefieldLayout.Positions(row.Seed, n, def.MineCloudRadius, offsets);
            for (int i = fv.Mines.Count; i < n; i++)
            {
                var q = new MeshInstance3D
                {
                    Name = $"Mine_{i}",
                    Mesh = new QuadMesh { Size = new Vector2(MineSize, MineSize) },
                    MaterialOverride = fv.Mat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = new Vector3(offsets[i].X, offsets[i].Y, offsets[i].Z),
                };
                fv.Container.AddChild(q);
                fv.Mines.Add(q);
            }
        }

        // Reconcile visibility against the current AliveMask (also hides quads above a shrunk n).
        for (int i = 0; i < fv.Mines.Count; i++)
            fv.Mines[i].Visible = (row.AliveMask & (1UL << i)) != 0UL;
    }

    // A single mine popped: hide its quad + play the pop FX at the reported world position.
    public void MineGone(ulong fieldId, byte mineIndex, byte reason, Vector3 pos)
    {
        byte team = 0;
        float blastRadius = 25f;
        if (_fields.TryGetValue(fieldId, out var fv))
        {
            team = fv.Team;
            blastRadius = fv.BlastRadius;
            if (mineIndex < fv.Mines.Count)
                fv.Mines[mineIndex].Visible = false;
        }

        // Pop VFX + SFX at the authoritative detonation point (mirrors WorldRenderer.NetMissileGone).
        var boom = ExplosionEffect.CreateBlast(blastRadius, team);
        AddChild(boom);
        boom.Position = pos;
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Explosion, pos, pitch: 1.15f);

        // Fully-depleted field: nothing left to show — drop it now rather than waiting for expiry.
        if (fv is not null && AllHidden(fv))
            FreeField(fieldId, fv);
    }

    public override void _Process(double delta)
    {
        _pulse += delta;
        // Breathing glow: armed fields pulse bright, unarmed ones sit dim + steady.
        float armedE = 2.4f + 1.2f * (float)Mathf.Sin(_pulse * 4.0);

        // Iterate over a snapshot so a self-free during expiry doesn't mutate mid-enumeration.
        List<ulong>? expired = null;
        foreach (var (id, fv) in _fields)
        {
            fv.Mat.EmissionEnergyMultiplier = fv.Armed ? armedE : 0.7f;
            fv.SecondsLeft -= (float)delta;
            if (fv.SecondsLeft <= 0f)
                (expired ??= new()).Add(id);
        }
        if (expired is not null)
            foreach (var id in expired)
                if (_fields.TryGetValue(id, out var fv2))
                    FreeField(id, fv2);
    }

    // Free every field (WorldRenderer Reset / phase→Lobby).
    public void Clear()
    {
        foreach (var fv in _fields.Values)
            fv.Container.QueueFree();
        _fields.Clear();
    }

    private void FreeField(ulong fieldId, FieldView fv)
    {
        fv.Container.QueueFree();
        _fields.Remove(fieldId);
    }

    private static bool AllHidden(FieldView fv)
    {
        foreach (var m in fv.Mines)
            if (m.Visible)
                return false;
        return true;
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

    // A per-field additive, self-lit material tinted to the owning team (emission drives the glow/
    // bloom). One instance per field so its emission energy can be pulsed independently.
    private static StandardMaterial3D MineMaterial(byte team)
    {
        Color tint = Nameplate.TeamColor(team);
        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoTexture = DiamondTex,
            AlbedoColor = tint,
            EmissionEnabled = true,
            EmissionTexture = DiamondTex,
            Emission = new Color(tint.R, tint.G, tint.B),
            EmissionEnergyMultiplier = 1.5f,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    // A soft "◈" glyph: a bright diamond border wrapping a smaller solid diamond core, white so the
    // material tint carries the team colour. Manhattan distance (|u|+|v|) gives the diamond field.
    private static ImageTexture BuildDiamondTexture()
    {
        const int size = 64;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                float u = (px + 0.5f) / size * 2f - 1f;
                float v = (py + 0.5f) / size * 2f - 1f;
                float d = Mathf.Abs(u) + Mathf.Abs(v); // 0 = centre, 1 = diamond edge

                float a = 0f;
                if (d <= 1f)
                {
                    // Outer border band (bright ring just inside the edge).
                    float border = 1f - Mathf.Min(1f, Mathf.Abs(d - 0.88f) / 0.12f);
                    // Inner solid diamond core.
                    float core = d < 0.34f ? 1f : 0f;
                    a = Mathf.Max(border, core);
                }
                img.SetPixel(px, py, new Color(1f, 1f, 1f, a));
            }
        return ImageTexture.CreateFromImage(img);
    }
}
