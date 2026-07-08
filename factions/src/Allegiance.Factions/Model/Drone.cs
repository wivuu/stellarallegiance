namespace Allegiance.Factions.Model;

/// <summary>An AI-piloted ship (miner, constructor, turret, bot). Mirrors <c>DataDroneTypeIGC</c> (igc.h:2698).</summary>
public record Drone : Buildable
{
    /// <summary>AI aiming accuracy/skill, from 0 (worst) to 1 (best).</summary>
    public double ShootSkill { get; set; }
    /// <summary>AI piloting/maneuvering skill, from 0 (worst) to 1 (best).</summary>
    public double MoveSkill { get; set; }
    /// <summary>AI willingness to engage/hold in combat, from 0 (timid) to 1 (fearless).</summary>
    public double Bravery { get; set; }

    /// <summary>Behaviour profile this drone's AI follows (miner, wingman, layer, builder).</summary>
    public PilotKind Pilot { get; set; }

    /// <summary>The hull this drone flies; references a hull <c>id</c>.</summary>
    public string HullId { get; set; } = "";

    /// <summary>What this drone lays/deploys (e.g. mines); references an expendable id.</summary>
    public string? DeployedExpendableId { get; set; }
}
