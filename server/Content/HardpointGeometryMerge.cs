using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Assets;
using StellarAllegiance.Shared;
using Factions = Allegiance.Factions.Model;

namespace SimServer.Content;

// =====================================================================
//  HardpointGeometryMerge.cs — GLB IS THE AUTHORITATIVE HARDPOINT INVENTORY + GEOMETRY.
//
//  Runs inside ContentLoader.Load, AFTER CoreValidator and BEFORE FactionsContentProjection, and
//  mutates the CORE model's Hull.Hardpoints / Station.Hardpoints in place (projection stays a dumb
//  field cast). For each hull/station carrying a model-name, it loads the GLB's HP_<Kind>_<Index>
//  nodes (in AUTHORED units, world-scaled by ws = ModelLength / LongestAxis for ships,
//  Radius*2 / LongestAxis for stations — the same scale World.LoadShipHull/LoadBase bake) and:
//
//    1. Each YAML hardpoint entry BINDS + OVERRIDES, keyed by (kind, index): it supplies weapon-id
//       (weapons) and — when its off-*/dir-* are authored — overrides the mesh node's pos/dir.
//       Unauthored geometry falls back to the matching mesh node; an entry with neither authored
//       geometry nor a mesh node is a boot error (named by id+kind+index).
//    2. Every mesh node NOT claimed by a YAML entry is APPENDED (deterministic order: kind byte,
//       then index). Appended Weapon mounts stay unbound (null WeaponId -> HardpointDef.NoWeapon
//       at projection) and, having no authored `mount:`, project to WeaponMountKind.NonMountable —
//       NOT a loadout slot: hidden in the hangar, rejected by ResolveLoadout. The mesh HP_ node
//       carries no gun/missile distinction, so exposing an empty mount as assignable requires a
//       YAML entry (`mount: any|gun|missile`, weapon-id omitted).
//
//  YAML weapon entries keep their YAML order at the head of the list, so the barrel spread-seed
//  indices (server Simulation / client DefRegistry.WeaponMounts) are unchanged; appended empty
//  mounts land at the end and are skipped by every armed-weapon consumer (NoWeapon never resolves).
//
//  No GLB / missing asset ⇒ nodes is empty, so every YAML entry must be FULLY authored (else boot
//  error) and nothing is appended. This is why boot now requires the assets dir for stock content.
// =====================================================================
public static class HardpointGeometryMerge
{
    // Assigned once at boot (Program.cs) after the host's ILoggerFactory exists; NullLogger keeps
    // any pre-host content load a safe no-op.
    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    public static void Apply(Factions.Core core)
    {
        foreach (var hull in core.Hulls)
        {
            SimModel? glb = string.IsNullOrEmpty(hull.ModelName)
                ? null
                : SimAssets.TryLoad($"ships/{hull.ModelName}.glb");
            float ws = ShipScale(hull, glb);
            Merge($"hull '{hull.Id}'", hull.Hardpoints, glb, ws);
        }

        foreach (var station in core.Stations)
        {
            // Bases are authored +90° off about X; bake the same orientation correction the sim hull
            // (World.LoadBase) and the visual (BaseModelLoader) apply, so the streamed hardpoints
            // (base turret muzzles, nav lights, dock markers) stay attached to the corrected mesh.
            SimModel? glb = string.IsNullOrEmpty(station.ModelName)
                ? null
                : SimAssets.TryLoad($"bases/{station.ModelName}.glb", CollisionConfig.BaseModelRotation);
            float ws = StationScale(station, glb);
            Merge($"station '{station.Id}'", station.Hardpoints, glb, ws);
        }
    }

    // Ships uniform-scale their GLB so its longest axis == ModelLength (World.LoadShipHull).
    private static float ShipScale(Factions.Hull hull, SimModel? glb)
    {
        if (glb is null)
            return 1f;
        if (hull.ModelLength <= 0)
            throw new InvalidDataException(
                $"hull '{hull.Id}' has model-name '{hull.ModelName}' but ModelLength {hull.ModelLength} <= 0 — cannot world-scale its hardpoints");
        if (glb.LongestAxis <= 1e-6f)
            throw new InvalidDataException($"hull '{hull.Id}' GLB '{hull.ModelName}' has a degenerate LongestAxis {glb.LongestAxis}");
        return (float)(hull.ModelLength / glb.LongestAxis);
    }

    // Stations scale so their GLB longest axis == Radius*2 (World.LoadBase).
    private static float StationScale(Factions.Station station, SimModel? glb)
    {
        if (glb is null)
            return 1f;
        if (station.Radius <= 0)
            throw new InvalidDataException(
                $"station '{station.Id}' has model-name '{station.ModelName}' but Radius {station.Radius} <= 0 — cannot world-scale its hardpoints");
        if (glb.LongestAxis <= 1e-6f)
            throw new InvalidDataException($"station '{station.Id}' GLB '{station.ModelName}' has a degenerate LongestAxis {glb.LongestAxis}");
        return (float)(station.Radius * 2.0 / glb.LongestAxis);
    }

    private static void Merge(string ctx, List<Factions.Hardpoint> hps, SimModel? glb, float ws)
    {
        // Parse the mesh nodes into a (kind,index) -> (world-scaled pos, unit forward) table.
        var nodes = new Dictionary<(Factions.RuntimeHardpointKind Kind, byte Index), (Vec3 Pos, Vec3 Fwd)>();
        if (glb is not null)
            foreach (var (name, pos, fwd) in glb.Hardpoints)
            {
                if (!TryParseNode(name, out var kind, out byte index))
                {
                    Log.UnparsableGlbNode(Logger, ctx, name);
                    continue;
                }
                var key = (kind, index);
                if (!nodes.TryAdd(key, (pos * ws, fwd)))
                    throw new InvalidDataException($"{ctx}: duplicate GLB hardpoint node kind={kind} index={index} ('{name}')");
            }

        // YAML entries bind + override (their YAML order is preserved at the head of the list).
        var claimed = new HashSet<(Factions.RuntimeHardpointKind, byte)>();
        foreach (var hp in hps)
        {
            var key = (hp.Kind, hp.Index);
            if (!claimed.Add(key))
                throw new InvalidDataException($"{ctx}: duplicate authored hardpoint kind={hp.Kind} index={hp.Index}");
            bool hasNode = nodes.TryGetValue(key, out var node);

            // Position: any authored off-* component wins; else inherit the mesh node; else boot error.
            bool posAuthored = hp.OffX.HasValue || hp.OffY.HasValue || hp.OffZ.HasValue;
            if (posAuthored)
            {
                hp.OffX ??= 0;
                hp.OffY ??= 0;
                hp.OffZ ??= 0;
            }
            else if (hasNode)
            {
                hp.OffX = node.Pos.X;
                hp.OffY = node.Pos.Y;
                hp.OffZ = node.Pos.Z;
            }
            else
                throw new InvalidDataException(
                    $"{ctx}: hardpoint kind={hp.Kind} index={hp.Index} has no position — author off-* or add an HP_{hp.Kind}_{hp.Index} mesh node");

            // Direction: any authored dir-* component wins (must be non-zero, normalized); else
            // inherit the mesh node's forward; else boot error.
            bool dirAuthored = hp.DirX.HasValue || hp.DirY.HasValue || hp.DirZ.HasValue;
            if (dirAuthored)
            {
                var d = new Vec3((float)(hp.DirX ?? 0), (float)(hp.DirY ?? 0), (float)(hp.DirZ ?? 0));
                if (d.LengthSquared() < 1e-12f)
                    throw new InvalidDataException($"{ctx}: hardpoint kind={hp.Kind} index={hp.Index} has a zero-length authored direction");
                d = Normalize(d);
                hp.DirX = d.X;
                hp.DirY = d.Y;
                hp.DirZ = d.Z;
            }
            else if (hasNode)
            {
                var d = Normalize(node.Fwd);
                hp.DirX = d.X;
                hp.DirY = d.Y;
                hp.DirZ = d.Z;
            }
            else
                throw new InvalidDataException(
                    $"{ctx}: hardpoint kind={hp.Kind} index={hp.Index} has no direction — author dir-* or add an HP_{hp.Kind}_{hp.Index} mesh node");
        }

        // Append every unclaimed mesh node, ordered by (kind byte, index) — deterministic.
        foreach (var kv in nodes
                     .Where(n => !claimed.Contains(n.Key))
                     .OrderBy(n => (byte)n.Key.Kind)
                     .ThenBy(n => n.Key.Index))
        {
            var (kind, index) = kv.Key;
            var (pos, fwd) = kv.Value;
            var d = Normalize(fwd);
            hps.Add(new Factions.Hardpoint
            {
                Kind = kind,
                Index = index,
                OffX = pos.X,
                OffY = pos.Y,
                OffZ = pos.Z,
                DirX = d.X,
                DirY = d.Y,
                DirZ = d.Z,
                // An unbound weapon mount is EMPTY (null WeaponId -> HardpointDef.NoWeapon at
                // projection); every other kind ignores WeaponId. With no YAML entry there is no
                // authored `mount:` either, so an appended weapon mount projects to
                // WeaponMountKind.NonMountable — hidden in the hangar, not a loadout slot. Author a
                // YAML entry with `mount:` (and no weapon-id) to expose it as an empty typed mount.
            });
        }
    }

    // "HP_<Kind>_<Index>" -> (RuntimeHardpointKind, byte). Kind uses the enum member spelling
    // (PascalCase: Weapon, MainEngine, DockingEntrance, ...). Returns false for a name that is not
    // an HP_ node, lacks a trailing _<int>, or names an unknown kind.
    private static bool TryParseNode(string name, out Factions.RuntimeHardpointKind kind, out byte index)
    {
        kind = default;
        index = 0;
        if (!name.StartsWith("HP_", StringComparison.Ordinal))
            return false;
        string body = name.Substring(3);
        int us = body.LastIndexOf('_');
        if (us <= 0 || us >= body.Length - 1)
            return false;
        string kindStr = body.Substring(0, us);
        string idxStr = body.Substring(us + 1);
        if (!Enum.TryParse(kindStr, ignoreCase: false, out kind))
            return false;
        if (!byte.TryParse(idxStr, out index))
            return false;
        return true;
    }

    private static Vec3 Normalize(Vec3 v)
    {
        float len = v.Length();
        return len < 1e-12f ? v : new Vec3(v.X / len, v.Y / len, v.Z / len);
    }
}
