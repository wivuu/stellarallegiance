namespace Allegiance.Factions.Model;

/// <summary>
/// A complete static data set: the shared catalog of buyables/expendables plus the factions that
/// draw on it. Equivalent to the contents of a C++ "static core" (.igc) file, but represented as
/// clean object lists. Loaded from / saved to YAML (split across files via a manifest).
/// </summary>
public record Core
{
    /// <summary>Free-form schema/content version tag for this dataset.</summary>
    public string? Version { get; set; }

    /// <summary>The tech-tree node catalog.</summary>
    public List<Tech> Techs { get; set; } = [];

    /// <summary>The ship chassis catalog.</summary>
    public List<Hull> Hulls { get; set; } = [];

    /// <summary>The mountable weapon (gun) catalog.</summary>
    public List<Weapon> Weapons { get; set; } = [];

    /// <summary>The mountable shield generator catalog.</summary>
    public List<Shield> Shields { get; set; } = [];

    /// <summary>The mountable cloaking device catalog.</summary>
    public List<Cloak> Cloaks { get; set; } = [];

    /// <summary>The mountable afterburner catalog.</summary>
    public List<Afterburner> Afterburners { get; set; } = [];

    /// <summary>The mountable ammo pack (magazine) catalog.</summary>
    public List<AmmoPack> AmmoPacks { get; set; } = [];

    /// <summary>The mountable expendable-launcher catalog.</summary>
    public List<Launcher> Launchers { get; set; } = [];

    /// <summary>The buildable station/building catalog.</summary>
    public List<Station> Stations { get; set; } = [];

    /// <summary>The research/tech-purchase catalog.</summary>
    public List<Development> Developments { get; set; } = [];

    /// <summary>The AI-piloted drone type catalog.</summary>
    public List<Drone> Drones { get; set; } = [];

    /// <summary>The missile expendable catalog.</summary>
    public List<Missile> Missiles { get; set; } = [];

    /// <summary>The mine expendable catalog.</summary>
    public List<Mine> Mines { get; set; } = [];

    /// <summary>The chaff/decoy expendable catalog.</summary>
    public List<Chaff> Chaffs { get; set; } = [];

    /// <summary>The probe expendable catalog.</summary>
    public List<Probe> Probes { get; set; } = [];

    /// <summary>The fuel-pod expendable catalog (reserve afterburner fuel carried as cargo).</summary>
    public List<FuelPod> Fuels { get; set; } = [];

    /// <summary>The weapon-bolt/projectile definition catalog.</summary>
    public List<Projectile> Projectiles { get; set; } = [];

    /// <summary>The playable faction template catalog.</summary>
    public List<Faction> Factions { get; set; } = [];

    /// <summary>Every mountable part across all part collections.</summary>
    public IEnumerable<Part> AllParts() =>
        Weapons.Cast<Part>().Concat(Shields).Concat(Cloaks).Concat(Afterburners).Concat(AmmoPacks).Concat(Launchers);

    /// <summary>Every expendable across all expendable collections. Fuels stays last so the
    /// existing Missiles→Mines→Chaffs→Probes cargo-catalog order is unchanged.</summary>
    public IEnumerable<Expendable> AllExpendables() =>
        Missiles.Cast<Expendable>().Concat(Mines).Concat(Chaffs).Concat(Probes).Concat(Fuels);

    /// <summary>Every buildable (anything carrying tech requirements/effects).</summary>
    public IEnumerable<Buildable> AllBuildables() =>
        Hulls.Cast<Buildable>().Concat(AllParts()).Concat(Stations).Concat(Developments).Concat(Drones);

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
        Fuels.AddRange(other.Fuels);
        Projectiles.AddRange(other.Projectiles);
        Factions.AddRange(other.Factions);
    }
}
