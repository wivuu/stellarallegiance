namespace Allegiance.Factions.Model;

/// <summary>
/// A launcher that carries and fires expendables (a magazine, dispenser, or chaff launcher).
/// Mirrors the C++ <c>DataLauncherTypeIGC</c> (igc.h:1881); its <see cref="Part.Slot"/> selects
/// which kind it is (<see cref="EquipmentSlot.Magazine"/> / <see cref="EquipmentSlot.Dispenser"/> /
/// <see cref="EquipmentSlot.ChaffLauncher"/>).
/// </summary>
public record Launcher : Part
{
    /// <summary>How many rounds the launcher holds.</summary>
    public int Amount { get; set; }

    /// <summary>How many are released per launch.</summary>
    public int LaunchCount { get; set; }

    /// <summary>The expendable this launcher fires; references an expendable id.</summary>
    public string? ExpendableId { get; set; }

    // ---- StellarAllegiance runtime extension (omit-when-default; see RuntimeData.cs) -----------

    /// <summary>
    /// Stable wire weapon id a hull hardpoint references. Null = not a runtime-fired launcher. A
    /// launcher with a weapon id projects to a <see cref="RuntimeWeaponKind.Missile"/> WeaponDef
    /// whose ballistics come from the referenced <see cref="ExpendableId"/> missile. Authored
    /// explicitly (the game's content id constants depend on it), sharing the weapon-id namespace.
    /// </summary>
    public uint? WeaponId { get; set; }

    /// <summary>Tick-domain launch cadence, authored directly to avoid seconds→tick rounding drift.</summary>
    public uint FireIntervalTicks { get; set; }
}
