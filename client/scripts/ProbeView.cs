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
    // Longest local axis (world units) a loaded probe GLB is uniform-scaled to.
    private const float TargetSize = 1.2f;

    // Slow cosmetic yaw spin (deg/s) so a stationary probe still reads as "alive".
    private const float SpinRateDeg = 12f;

    public byte Team { get; private set; }

    // Build the visual from the launching probe-kind WeaponDef (model only — probes have no trail).
    // A null def (shouldn't happen; defs precede any ship) falls back to the team-tinted puff.
    public void Initialize(Vector3 pos, byte team, WeaponDef? def)
    {
        Team = team;
        Position = pos;
        AddChild(LoadHull(def?.ModelName, team));
    }

    public override void _Process(double delta)
    {
        RotateY(Mathf.DegToRad(SpinRateDeg) * (float)delta);
    }

    // Load `assets/probes/<name>.glb` normalized to TargetSize, or the ChaffFx team-tinted puff when
    // it's absent. Mirrors MissileView.LoadHull / ChaffFx.LoadModel.
    private static Node3D LoadHull(string? modelName, byte team)
    {
        if (!string.IsNullOrEmpty(modelName) && GlbLoader.Load($"res://assets/probes/{modelName}.glb") is { } hull)
        {
            GlbLoader.NormalizeLongestAxis(hull, TargetSize);
            return hull;
        }
        return ChaffFx.FallbackPuff(team);
    }
}
