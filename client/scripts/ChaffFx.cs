using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  ChaffFx.cs — CLIENT CHAFF-DECOY VISUALS
//
//  One container node under WorldRenderer that owns the live chaff decoys. GameNetClient decodes
//  MsgChaff and WorldRenderer.NetSpawnChaff forwards here. Each decoy is the authored chaff model
//  (assets/chaff/<ModelName>.glb, e.g. acs40) — a physical canister that drifts with the wire
//  velocity, brakes with the same 0.95/tick drag the server applies, tumbles as it coasts, and
//  shrinks away over the tail of the chaff WeaponDef's ProjectileLifeTicks (there is no gone-message
//  — D2 — the decoy self-frees when its life elapses). A soft additive billboard puff is the
//  fallback only when the GLB is missing. Sector-gated like the rest of the world (a decoy dropped
//  in another sector stays hidden while the local view is elsewhere).
// =====================================================================
public partial class ChaffFx : Node3D
{
    // Longest local axis (world units) a loaded chaff GLB is uniform-scaled to.
    private const float ChaffSize = 3f;

    // ~20 Hz sim-tick drag applied per real-time second (0.95 per 1/20 s ⇒ 0.95^(20·dt) per frame),
    // so the client decoy coasts to a stop on the same curve as the server ChaffSim.
    private const float DragPerTick = 0.95f;

    // Fraction of life after which the decoy starts shrinking out (0.7 → last 30% fades by scale).
    private const float FadeStart = 0.7f;

    // Soft round mote for the fallback puff — built once (no per-spawn GC).
    private static readonly GradientTexture2D Dot = RadialDot();

    private sealed class Decoy
    {
        public Node3D Node = null!;
        public Vector3 Vel;
        public Vector3 AngVel; // tumble axis-rates (rad/s per euler axis)
        public double Age;
        public double Life;
        public int Sector;
    }

    private readonly List<Decoy> _decoys = new();

    // Spawn a chaff decoy at the wire position, drifting with `vel` and fading over the chaff def's
    // lifespan. Renders the authored mesh (team tint is not used — a decoy reads as a physical object).
    public void Spawn(ulong id, byte team, uint sector, Vector3 pos, Vector3 vel, WeaponDef? def)
    {
        // No baked-tuning fallback for gameplay, but this is pure cosmetics: if the def hasn't
        // streamed yet, show a sensible ~2.5 s decoy rather than nothing.
        float life = def is { ProjectileLifeTicks: > 0 } ? def.ProjectileLifeTicks / 20f : 2.5f;

        var root = new Node3D { Position = pos, Visible = SectorVisible((int)sector) };
        root.AddChild(LoadModel(def?.ModelName, team));
        AddChild(root);

        // Deterministic-ish tumble from the decoy id so each canister spins a little differently.
        Vector3 angVel = new(
            (MinefieldLayout.Hash01(id, 1) * 2f - 1f) * 1.6f,
            (MinefieldLayout.Hash01(id, 2) * 2f - 1f) * 1.6f,
            (MinefieldLayout.Hash01(id, 3) * 2f - 1f) * 1.6f);

        _decoys.Add(new Decoy
        {
            Node = root,
            Vel = vel,
            AngVel = angVel,
            Age = 0,
            Life = life,
            Sector = (int)sector,
        });

        // A soft dispensing pop — no bespoke asset (reuse Impact, pitched up and quiet).
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, pos, pitch: 1.5f, volumeDb: -14f);
    }

    public override void _Process(double delta)
    {
        if (_decoys.Count == 0)
            return;

        int viewSector = SectorOf();
        float dragThisFrame = Mathf.Pow(DragPerTick, (float)delta * 20f);

        for (int i = _decoys.Count - 1; i >= 0; i--)
        {
            var d = _decoys[i];
            d.Age += delta;
            if (d.Age >= d.Life)
            {
                d.Node.QueueFree();
                _decoys.RemoveAt(i);
                continue;
            }

            // Drift + drag (mirror the server integration).
            d.Node.Position += d.Vel * (float)delta;
            d.Vel *= dragThisFrame;

            // Tumble + shrink-out over the tail of life. Rebuild the basis each frame from the age so
            // rotation and the fade scale compose cleanly (uniform scale = no shear).
            float t = (float)(d.Age / d.Life);
            float shrink = t < FadeStart ? 1f : 1f - (t - FadeStart) / (1f - FadeStart);
            d.Node.Basis = Basis.FromEuler(d.AngVel * (float)d.Age).Scaled(Vector3.One * shrink);
            d.Node.Visible = d.Sector == viewSector;
        }
    }

    // Free every live decoy (WorldRenderer Reset / phase→Lobby).
    public void Clear()
    {
        foreach (var d in _decoys)
            d.Node.QueueFree();
        _decoys.Clear();
    }

    // The authored chaff GLB normalized to ChaffSize, or the soft additive puff fallback when it's
    // absent. Mirrors MissileView.LoadHull.
    private static Node3D LoadModel(string? modelName, byte team)
    {
        if (!string.IsNullOrEmpty(modelName) && GlbLoader.Load($"res://assets/chaff/{modelName}.glb") is { } hull)
        {
            GlbLoader.NormalizeLongestAxis(hull, ChaffSize);
            return hull;
        }
        return FallbackPuff(team);
    }

    // Fallback: the old soft additive billboard puff (team-tinted, self-lit) so a decoy is never
    // invisible when the GLB is missing.
    private static MeshInstance3D FallbackPuff(byte team)
    {
        Color tint = team == 0 ? new Color(0.8f, 0.95f, 1.25f) : new Color(1.25f, 0.9f, 0.8f);
        return new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(ChaffSize * 2f, ChaffSize * 2f) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                AlbedoTexture = Dot,
                AlbedoColor = tint,
                EmissionEnabled = true,
                EmissionTexture = Dot,
                Emission = tint,
                EmissionEnergyMultiplier = 2.2f,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
    }

    // The sector the local view is showing (F3 overview / warp aware), read from the parent
    // WorldRenderer. Falls back to "show everything" if the parent isn't resolvable yet.
    private int SectorOf() => GetParentOrNull<WorldRenderer>() is { } w ? (int)w.ViewSector : -1;

    private bool SectorVisible(int sector) => SectorOf() is var v && (v < 0 || v == sector);

    // Soft round mote: hot centre fading to transparent (same recipe as ExplosionEffect.RadialDot).
    private static GradientTexture2D RadialDot()
    {
        var gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.5f, 1f },
            Colors = new[] { new Color(1f, 1f, 1f, 0.9f), new Color(1f, 1f, 1f, 0.35f), new Color(1f, 1f, 1f, 0f) },
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 128,
            Height = 128,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
        };
    }
}
