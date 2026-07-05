using Godot;
using StellarAllegiance.Shared;

// =====================================================================
//  ProbeView.cs — CLIENT DEPLOYED RECON-PROBE VISUAL
//
//  One node per live server probe (WorldRenderer._probes). Clones the MissileView loading recipe
//  (assets/probes/<ModelName>.glb, e.g. acs64, via the launching probe-dispenser WeaponDef) but the
//  probe itself never moves once deployed — the server never sends a new position for it, so unlike
//  MissileView there's no dead-reckoning/easing here. A soft additive billboard puff (ChaffFx's
//  team-tinted fallback) covers the rare case the GLB fails to load. A slow self-rotation is purely
//  cosmetic — it just reads as a live sensor buoy rather than a static prop.
// =====================================================================
public partial class ProbeView : Node3D
{
    // Guard fallback for the longest-axis normalization when a def carries no authored ProbeModelSize
    // (0). NOT a tuning default — the authored value (expendables.yaml model-size) is the real size.
    private const float FallbackSize = 1.2f;

    // Slow cosmetic yaw spin (deg/s) so a stationary probe still reads as "alive".
    private const float SpinRateDeg = 12f;

    public byte Team { get; private set; }

    // Server hit-sphere radius (from the def), so the client's cosmetic bolt sparks land where the
    // server would resolve a hit. Falls back to the visual size when unauthored.
    public float HitRadius { get; private set; }

    // Build the visual from the launching probe-kind WeaponDef (model only — probes have no trail).
    // A null def (shouldn't happen; defs precede any ship) falls back to the team-tinted puff.
    public void Initialize(Vector3 pos, byte team, WeaponDef? def)
    {
        Team = team;
        Position = pos;
        float size = def is { ProbeModelSize: > 0f } ? def.ProbeModelSize : FallbackSize;
        HitRadius = def is { ProbeHitRadius: > 0f } ? def.ProbeHitRadius : size;
        AddChild(LoadHull(def?.ModelName, team, size));
    }

    public override void _Process(double delta)
    {
        RotateY(Mathf.DegToRad(SpinRateDeg) * (float)delta);
    }

    // Load `assets/probes/<name>.glb` normalized to `size`, or the ChaffFx team-tinted puff when
    // it's absent. Mirrors MissileView.LoadHull / ChaffFx.LoadModel.
    private static Node3D LoadHull(string? modelName, byte team, float size)
    {
        if (!string.IsNullOrEmpty(modelName) && GlbLoader.Load($"res://assets/probes/{modelName}.glb") is { } hull)
        {
            GlbLoader.NormalizeLongestAxis(hull, size);
            return hull;
        }
        var puff = ChaffFx.FallbackPuff(team);
        puff.Scale = Vector3.One * (size / FallbackSize);
        return puff;
    }
}
