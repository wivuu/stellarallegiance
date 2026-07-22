namespace StellarAllegiance.Shared;

// =====================================================================
//  DockRules.cs — the station-class launch/dock restriction rules shared by the server sim and
//  client prediction (2026-07-21). A hull with a non-zero ShipClassDef.LaunchClassMask may only
//  launch from / dock at friendly bases whose station class is in the mask, and at an allowed
//  base may only dock through the LARGEST door (side doors stay small-ship-only). Both peers
//  must run these exact rules over the same DockFaceParser output so prediction and the sim
//  agree bit-for-bit at the bay mouth.
// =====================================================================
public static class DockRules
{
    // A base whose type has no station-catalog entry (e.g. a raw test-world base). A restricted
    // hull never docks at an unknown-class base; unrestricted hulls don't care.
    public const byte UnknownStationClass = 255;

    // May a hull with this launch-class mask use a base of this station class? Mask 0 =
    // unrestricted. The mask carries bits 0..15 (ushort); UnknownStationClass falls out via the
    // `< 16` guard.
    public static bool ClassAllowed(ushort launchClassMask, byte stationClass) =>
        launchClassMask == 0 || (stationClass < 16 && (launchClassMask & (1 << stationClass)) != 0);

    // The base's largest docking door by rectangle area (Eu*Ev; the 4x constant factor cancels).
    // Tie -> lowest index; -1 for a null/empty array. Plain float compare over the shared
    // parser's identical DockFace[] on both peers, so the pick is deterministic.
    public static int LargestFaceIndex(DockFace[]? faces)
    {
        if (faces is null || faces.Length == 0)
            return -1;
        int best = 0;
        float bestArea = faces[0].Eu * faces[0].Ev;
        for (int i = 1; i < faces.Length; i++)
        {
            float area = faces[i].Eu * faces[i].Ev;
            if (area > bestArea)
            {
                best = i;
                bestArea = area;
            }
        }
        return best;
    }

    // The door filter for a hull at a class-allowed base: -1 (all doors) when unrestricted, else
    // only the precomputed largest door. Feed the result to Collide.IntersectsDockFace's
    // `onlyFace` parameter.
    public static int AllowedFace(ushort launchClassMask, int largestFaceIndex) =>
        launchClassMask == 0 ? -1 : largestFaceIndex;
}
