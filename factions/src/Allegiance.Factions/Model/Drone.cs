namespace Allegiance.Factions.Model;

/// <summary>An AI-piloted ship (miner, constructor, turret, bot). Mirrors <c>DataDroneTypeIGC</c> (igc.h:2698).</summary>
public record Drone : Buildable
{
    public double ShootSkill { get; set; }
    public double MoveSkill { get; set; }
    public double Bravery { get; set; }

    public PilotKind Pilot { get; set; }

    /// <summary>The hull this drone flies; references a <see cref="Hull.Id"/>.</summary>
    public string HullId { get; set; } = "";

    /// <summary>What this drone lays/deploys (e.g. mines); references an expendable id.</summary>
    public string? DeployedExpendableId { get; set; }
}
