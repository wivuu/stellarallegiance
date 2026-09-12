using System.Collections.Generic;
using System.IO;
using Allegiance.Factions.Serialization;
using Allegiance.Factions.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StellarAllegiance.Shared;

namespace SimServer.Content;

// Loads the authoritative content from a YAML bundle authored in the canonical Allegiance.Factions
// format (Stage-1 PIVOT). There is NO compile-in content; the values live only in the YAML. The
// pipeline is:
//   1. CoreSerializer.Load(manifest)  — merge the manifest's catalog fragments + faction files into
//      one Core (the canonical model that also feeds Stage-2 unlock gating / Stage-4 factions);
//   2. CoreValidator.Validate         — referential-integrity gate on the Core (unique ids, resolvable
//      cross-refs, start-station ability). The client has no fallback, so an invalid bundle throws
//      here and the server refuses to start;
//   2b. HardpointGeometryMerge.Apply  — fold each hull/station's GLB HP_<Kind>_<Index> nodes into the
//      Core's hardpoint lists (mesh = authoritative inventory + geometry; YAML binds weapon-ids +
//      overrides). Boots-errors on an unresolvable/duplicate/zero-dir hardpoint;
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
    // Assigned once at boot (Program.cs) after the host's ILoggerFactory exists, exactly like
    // HardpointGeometryMerge.Logger — these static pipeline helpers have no instance to inject
    // into. NullLogger keeps a pre-host content load (tests, --gen-schemas, tooling) a safe no-op.
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

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
                    + string.Join("\n  - ", vr.Errors)
            );

        // GLB-authoritative hardpoint inventory + geometry: the mesh HP_ nodes supply how many
        // mounts a hull/station has and where they sit; YAML binds weapon-ids + overrides geometry.
        // Mutates the core's Hull/Station hardpoint lists before the (dumb) projection reads them.
        HardpointGeometryMerge.Apply(core);

        var set = FactionsContentProjection.Project(core, WorldLoader.Load(worldPath));
        WarnDroppablesWithoutModel(set);
        return set;
    }

    // Wreck salvage draws each dropped item as its def's GLB; an empty model-name falls back to a
    // placeholder puff, which looks like a bug rather than authoring the operator forgot. WARN, do
    // not refuse: the item is still fully functional, and custom content shouldn't be unbootable
    // over cosmetics. Droppable = what DropSalvage can actually emit — Bolt guns (Kind 0 items) and
    // the launcher-less pure cargo, i.e. the fuel pods (FuelPerCharge > 0). A DISPENSER item's mesh
    // comes from its WeaponDef (already authored for the deployed mine/chaff/probe), so cargo rows
    // with no fuel value are deliberately not listed here.
    private static void WarnDroppablesWithoutModel(ContentSet set)
    {
        var missing = new List<string>();
        foreach (var w in set.Weapons)
        {
            if (w.Kind == WeaponKind.Bolt && string.IsNullOrEmpty(w.ModelName))
                missing.Add($"weapon {w.WeaponId} '{w.Name}'");
        }
        foreach (var c in set.CargoItems)
        {
            if (c.FuelPerCharge > 0f && string.IsNullOrEmpty(c.ModelName))
                missing.Add($"cargo {c.CargoId} '{c.Name}'");
        }
        if (missing.Count > 0)
            Log.SalvageDefsWithoutModel(Logger, missing.Count, string.Join(", ", missing));
    }
}
