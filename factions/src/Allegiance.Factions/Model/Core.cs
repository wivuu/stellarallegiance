namespace Allegiance.Factions.Model;

/// <summary>
/// A complete static data set: the shared catalog of buyables/expendables plus the factions that
/// draw on it. Equivalent to the contents of a C++ "static core" (.igc) file, but represented as
/// clean object lists. Loaded from / saved to YAML (split across files via a manifest).
/// </summary>
public record Core
{
    public string? Version { get; set; }

    public List<Tech> Techs { get; set; } = [];

    public List<Hull> Hulls { get; set; } = [];

    public List<Weapon> Weapons { get; set; } = [];
    public List<Shield> Shields { get; set; } = [];
    public List<Cloak> Cloaks { get; set; } = [];
    public List<Afterburner> Afterburners { get; set; } = [];
    public List<AmmoPack> AmmoPacks { get; set; } = [];
    public List<Launcher> Launchers { get; set; } = [];

    public List<Station> Stations { get; set; } = [];
    public List<Development> Developments { get; set; } = [];
    public List<Drone> Drones { get; set; } = [];

    public List<Missile> Missiles { get; set; } = [];
    public List<Mine> Mines { get; set; } = [];
    public List<Chaff> Chaffs { get; set; } = [];
    public List<Probe> Probes { get; set; } = [];

    public List<Projectile> Projectiles { get; set; } = [];

    public List<Faction> Factions { get; set; } = [];

    /// <summary>Every mountable part across all part collections.</summary>
    public IEnumerable<Part> AllParts() =>
        Weapons.Cast<Part>()
            .Concat(Shields)
            .Concat(Cloaks)
            .Concat(Afterburners)
            .Concat(AmmoPacks)
            .Concat(Launchers);

    /// <summary>Every expendable across all expendable collections.</summary>
    public IEnumerable<Expendable> AllExpendables() =>
        Missiles.Cast<Expendable>()
            .Concat(Mines)
            .Concat(Chaffs)
            .Concat(Probes);

    /// <summary>Every buildable (anything carrying tech requirements/effects).</summary>
    public IEnumerable<Buildable> AllBuildables() =>
        Hulls.Cast<Buildable>()
            .Concat(AllParts())
            .Concat(Stations)
            .Concat(Developments)
            .Concat(Drones);

    /// <summary>Appends every entry from <paramref name="other"/> into this core's collections.</summary>
    public void Merge(Core other)
    {
        if (other.Version is not null)
            Version ??= other.Version;

        Techs.AddRange(other.Techs);
        Hulls.AddRange(other.Hulls);
        Weapons.AddRange(other.Weapons);
        Shields.AddRange(other.Shields);
        Cloaks.AddRange(other.Cloaks);
        Afterburners.AddRange(other.Afterburners);
        AmmoPacks.AddRange(other.AmmoPacks);
        Launchers.AddRange(other.Launchers);
        Stations.AddRange(other.Stations);
        Developments.AddRange(other.Developments);
        Drones.AddRange(other.Drones);
        Missiles.AddRange(other.Missiles);
        Mines.AddRange(other.Mines);
        Chaffs.AddRange(other.Chaffs);
        Probes.AddRange(other.Probes);
        Projectiles.AddRange(other.Projectiles);
        Factions.AddRange(other.Factions);
    }
}
