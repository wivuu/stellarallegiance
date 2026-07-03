using System.Collections.Generic;
using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  ChaffFx.cs — CLIENT CHAFF-PUFF VISUALS
//
//  One container node under WorldRenderer that owns the live chaff-puff sprites. GameNetClient
//  decodes MsgChaff and WorldRenderer.NetSpawnChaff forwards here. Each puff is a soft, additive,
//  team-tinted billboard cloud (the same unshaded + additive + HDR-emission idiom as
//  ExplosionEffect so the WorldEnvironment glow blooms it). It drifts with the wire velocity,
//  brakes with the same 0.95/tick drag the server applies, expands slightly as it disperses, and
//  fades out over the chaff WeaponDef's ProjectileLifeTicks — there is no gone-message (D2), the
//  puff self-frees when its life elapses. Sector-gated like the rest of the world (a puff dropped
//  in another sector stays hidden while the local view is elsewhere).
// =====================================================================
public partial class ChaffFx : Node3D
{
    // A soft round mote shared by every puff — built once (no per-spawn GC), same recipe as
    // ExplosionEffect.RadialDot: hot centre fading to transparent so the additive quad reads as a
    // fuzzy cloud rather than a hard disc.
    private static readonly GradientTexture2D Dot = RadialDot();

    // ~20 Hz sim-tick drag applied per real-time second (0.95 per 1/20 s ⇒ 0.95^(20·dt) per frame),
    // so the client puff coasts to a stop on the same curve as the server ChaffSim.
    private const float DragPerTick = 0.95f;

    private sealed class Puff
    {
        public MeshInstance3D Node = null!;
        public StandardMaterial3D Mat = null!;
        public Vector3 Vel;
        public double Age;
        public double Life;
        public int Sector;
        public float BaseEnergy;
        public float StartScale;
    }

    private readonly List<Puff> _puffs = new();

    // Spawn a chaff puff visual at the wire position, drifting with `vel` and fading over the chaff
    // def's lifespan. Team-tinted subtly (chaff reads as a silvery cloud with a faint faction hue).
    public void Spawn(ulong id, byte team, uint sector, Vector3 pos, Vector3 vel, WeaponDef? def)
    {
        // No baked-tuning fallback for gameplay, but this is pure cosmetics: if the def hasn't
        // streamed yet, show a sensible ~2.5 s puff rather than nothing.
        float life = def is { ProjectileLifeTicks: > 0 } ? def.ProjectileLifeTicks / 20f : 2.5f;
        float scale = 6f;

        // Faint faction tint over a bright near-white core (HDR > 1 so it blooms). Team 0 cool,
        // team 1 warm — subtle enough that chaff still reads as chaff, not a team flare.
        Color tint = team == 0 ? new Color(0.8f, 0.95f, 1.25f) : new Color(1.25f, 0.9f, 0.8f);
        const float energy = 2.2f;

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoTexture = Dot,
            AlbedoColor = tint,
            EmissionEnabled = true,
            EmissionTexture = Dot,
            Emission = tint,
            EmissionEnergyMultiplier = energy,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var node = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(scale, scale) },
            MaterialOverride = mat,
            Position = pos,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = SectorVisible((int)sector),
        };
        AddChild(node);

        _puffs.Add(new Puff
        {
            Node = node,
            Mat = mat,
            Vel = vel,
            Age = 0,
            Life = life,
            Sector = (int)sector,
            BaseEnergy = energy,
            StartScale = scale,
        });

        // A soft dispensing pop — no bespoke asset (reuse Impact, pitched up and quiet).
        SfxManager.Instance?.PlayAt(SfxManager.SfxId.Impact, pos, pitch: 1.5f, volumeDb: -14f);
    }

    public override void _Process(double delta)
    {
        if (_puffs.Count == 0)
            return;

        int viewSector = SectorOf();
        float dragThisFrame = Mathf.Pow(DragPerTick, (float)delta * 20f);

        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            var p = _puffs[i];
            p.Age += delta;
            if (p.Age >= p.Life)
            {
                p.Node.QueueFree();
                _puffs.RemoveAt(i);
                continue;
            }

            // Drift + drag (mirror the server integration), disperse (grow) and fade over life.
            p.Node.Position += p.Vel * (float)delta;
            p.Vel *= dragThisFrame;

            float t = (float)(p.Age / p.Life);
            float fade = 1f - t; // linear burn-down
            p.Mat.EmissionEnergyMultiplier = p.BaseEnergy * fade;
            p.Mat.AlbedoColor = p.Mat.AlbedoColor with { A = fade };
            p.Node.Scale = Vector3.One * Mathf.Lerp(0.7f, 1.6f, t); // cloud disperses as it fades
            p.Node.Visible = p.Sector == viewSector;
        }
    }

    // Free every live puff (WorldRenderer Reset / phase→Lobby).
    public void Clear()
    {
        foreach (var p in _puffs)
            p.Node.QueueFree();
        _puffs.Clear();
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
