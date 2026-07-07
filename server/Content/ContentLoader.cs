using System.IO;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Validation;

namespace SimServer.Content;

// Loads the authoritative content from a YAML bundle authored in the canonical Allegiance.Factions
// format (Stage-1 PIVOT). There is NO compile-in content; the values live only in the YAML. The
// pipeline is:
//   1. CoreSerializer.Load(manifest)  — merge the manifest's catalog fragments + faction files into
//      one Core (the canonical model that also feeds Stage-2 unlock gating / Stage-4 factions);
//   2. CoreValidator.Validate         — referential-integrity gate on the Core (unique ids, resolvable
//      cross-refs, start-station ability). The client has no fallback, so an invalid bundle throws
//      here and the server refuses to start;
//   3. FactionsContentProjection      — project the Core into the existing runtime ContentSet
//      (ShipClassDef/WeaponDef/BaseDef/WorldConfig), unchanged on the wire (Protocol.BuildDefs) and
//      client. The caller (Program.cs) runs the shared ContentValidator on the projected defs as a
//      SECOND gate (keeps the dangling-hardpoint / non-positive-hull / dup-id guarantees).
//
// The WORLD config is NOT part of the bundle manifest: it is a standalone server file
// (content/core/world.yaml) loaded by WorldLoader and carried onto the ContentSet here — the
// tech tree tunes buyable gameplay/balance, world.yaml tunes the server's world defaults + sim.
public static class ContentLoader
{
    // Load a complete content bundle from its manifest path plus the standalone world tuning file.
    // Throws on a missing/malformed/invalid bundle or world file so the caller fails fast at boot
    // (FileNotFoundException / InvalidDataException).
    public static ContentSet Load(string manifestPath, string worldPath)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"content manifest not found: {manifestPath}");

        var core = CoreSerializer.Load(manifestPath);

        var vr = CoreValidator.Validate(core);
        if (!vr.IsValid)
            throw new InvalidDataException(
                $"content bundle '{manifestPath}' failed validation ({vr.Errors.Count} error(s)):\n  - "
                + string.Join("\n  - ", vr.Errors));

        return FactionsContentProjection.Project(core, WorldLoader.Load(worldPath));
    }
}
