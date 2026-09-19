using System.Collections.Generic;
using StellarAllegiance.Shared;

// Crew-served TURRET STATION lookup + the gunner's mouse→gimbal gain, factored out of the Godot
// seams that need them (ShipRenderer.ApplyTurrets rebuilding a remote turret's bolts,
// TurretController driving the local gimbal, TurretBarrelView placing a barrel). Every one of them
// has to agree on WHICH hardpoints are stations and in what order, because the wire speaks a
// SeatIndex (= HardpointDef.Index) while the per-ship state arrays are indexed by station slot.
// Pure BCL + shared defs, no Godot type — so tests/CrewStoreTest exercises the real mapping.
public static class TurretStations
{
    // Every REAL crew station on a hull, in hardpoint DECLARATION order (the order the server's
    // ClassTurretStations builds too). An unauthored mesh HP_Turret node is NonMountable and is not a
    // station at all — it never gets a seat, a barrel, or a slot.
    public static List<HardpointDef> Of(IReadOnlyList<HardpointDef>? hardpoints)
    {
        var list = new List<HardpointDef>();
        if (hardpoints is null)
            return list;
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == HardpointKind.Turret && hp.Mount != WeaponMountKind.NonMountable)
                list.Add(hp);
        return list;
    }

    // Wire seat index (HardpointDef.Index) -> station slot; -1 when this hull has no such station.
    // Mirrors the server's Simulation.TurretSlotOf so a hull that ever authors its turret indices out
    // of declaration order still resolves to the same slot on both peers.
    public static int SlotOf(IReadOnlyList<HardpointDef> stations, byte seatIndex)
    {
        for (int i = 0; i < stations.Count; i++)
            if (stations[i].Index == seatIndex)
                return i;
        return -1;
    }

    // The station hardpoint a seat index names, or null when the hull has no such station.
    public static HardpointDef? Find(IReadOnlyList<HardpointDef>? hardpoints, byte seatIndex)
    {
        if (hardpoints is null)
            return null;
        foreach (HardpointDef hp in hardpoints)
            if (hp.Kind == HardpointKind.Turret && hp.Mount != WeaponMountKind.NonMountable && hp.Index == seatIndex)
                return hp;
        return null;
    }

    // Radians of gimbal travel per unit of ShipController's virtual-stick gain, so one mouse
    // sensitivity setting drives BOTH flight and the turret. The pilot's stick gain is a
    // deflection-per-pixel (DefaultMouseSens 0.01 × the UserPrefs multiplier); a turret has no
    // self-centering stick, so that gain is converted straight to an angle here — strictly LINEAR,
    // no curve. 0.12 is ~0.07°/px at the default sensitivity (a 90° sweep ≈ 1300 px), the usual
    // mouse-look gain; the old 0.4 (0.23°/px) made a careful nudge jump (user report 2026-09-19).
    // TURRET_GAIN overrides it for feel-tuning (scripts/turret-test.ps1).
    public const float RadPerStickUnit = 0.12f;

    public static float AimDeltaRad(float pixels, float stickGain, float radPerStickUnit = RadPerStickUnit) =>
        pixels * stickGain * radPerStickUnit;
}
