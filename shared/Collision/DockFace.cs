using System;
using System.Collections.Generic;

namespace StellarAllegiance.Shared;

// =====================================================================
//  DockFace.cs — the GROUPED docking-door geometry shared by the server sim and the client
//  prediction. `HP_DockingEntrance_*` markers no longer each mean an independent docking point:
//  they come in GROUPS OF FIVE (in ascending index order), each group defining ONE bounded
//  rectangular docking face ("door"):
//
//    • the FIRST marker of a group is the face plane — its position is the face reference point
//      and its forward (+Z node axis = the parsed `Forward`) is the INWARD normal, the direction
//      a ship travels when entering. A ship docks by intersecting this bounded virtual plane.
//    • the NEXT FOUR markers mark the door boundary — nominally bottom/top/left/right side
//      midpoints of the rectangle. Parsed ORDER-AGNOSTICALLY: each is projected onto the face
//      plane and their projections define the two in-plane axes + half-extents (we never assume
//      which of the four is which side).
//
//  ONE parser (DockFaceParser.Build) builds these for BOTH peers from the SAME GLB hardpoints, so
//  client prediction and the server agree bit-for-bit at the bay mouth (no rubber-banding — the
//  repo memory explicitly warns dock geometry must stay bit-identical). If the entrance-marker
//  count isn't a multiple of 5, the leftover markers fall back to legacy single-point discs (old
//  behaviour) with a load warning, so a mis-authored asset never hard-crashes.
// =====================================================================

// One rectangular docking face, in the base's local (already world-scaled) frame — the SAME frame
// as `shipPos - basePos`. Ship test lives in Collide.IntersectsDockFace.
public readonly struct DockFace
{
    public readonly Vec3 Center; // face-marker position (base-local, world-scaled)
    public readonly Vec3 Normal; // inward unit normal (direction a ship travels entering)
    public readonly Vec3 U; // in-plane axis 1 (unit)
    public readonly Vec3 V; // in-plane axis 2 (unit)
    public readonly float Eu; // half-extent along U (world units)
    public readonly float Ev; // half-extent along V (world units)

    public DockFace(Vec3 center, Vec3 normal, Vec3 u, Vec3 v, float eu, float ev)
    {
        Center = center;
        Normal = normal;
        U = u;
        V = v;
        Eu = eu;
        Ev = ev;
    }
}

public static class DockFaceParser
{
    public const string EntrancePrefix = "HP_DockingEntrance";

    // Half-extent (world units) used when a leftover entrance marker can't form a full 5-marker
    // door and falls back to a legacy single-point square. Matches the old DockDiscRadius (7).
    private const float LegacyDiscRadius = 7f;

    // Parse all HP_DockingEntrance_* markers (from `hardpoints`, in AUTHORED units) into world-scaled
    // rectangular docking faces. `worldScale` maps authored → world (the base's uniform bake scale).
    // `warn` (optional) is invoked with a human-readable message when the marker count isn't a
    // multiple of 5 so each caller logs through its own logger. Deterministic + engine-agnostic so
    // both peers produce identical DockFace[].
    public static DockFace[] Build(
        IReadOnlyList<(string Name, Vec3 Pos, Vec3 Forward)> hardpoints,
        float worldScale,
        Action<string>? warn = null
    )
    {
        // Collect + sort entrance markers by their trailing index (indices need not restart at
        // multiples of 5 — just sort all and chunk into consecutive fives).
        var ent = new List<(int Idx, Vec3 Pos, Vec3 Fwd)>();
        foreach (var hp in hardpoints)
            if (hp.Name.StartsWith(EntrancePrefix, StringComparison.Ordinal))
                ent.Add((ParseIndex(hp.Name), hp.Pos, hp.Forward));
        ent.Sort((a, b) => a.Idx.CompareTo(b.Idx));

        var faces = new List<DockFace>();
        int full = ent.Count / 5;
        for (int g = 0; g < full; g++)
            faces.Add(BuildFace(ent, g * 5, worldScale));

        int rem = ent.Count - full * 5;
        if (rem != 0)
        {
            warn?.Invoke(
                $"[DockFaceParser] {ent.Count} HP_DockingEntrance markers is not a multiple of 5 "
                    + $"(the grouped-door convention needs 5 per door: 1 face + 4 boundary); "
                    + $"treating the {rem} leftover marker(s) as legacy single-point discs."
            );
            for (int i = full * 5; i < ent.Count; i++)
                faces.Add(LegacyDisc(ent[i].Pos, worldScale));
        }
        return faces.ToArray();
    }

    // Build one door from a 5-marker group: [start] = face plane, [start+1 .. start+4] = boundary.
    private static DockFace BuildFace(List<(int Idx, Vec3 Pos, Vec3 Fwd)> ent, int start, float ws)
    {
        Vec3 center = ent[start].Pos * ws;
        Vec3 n = Normalize(ent[start].Fwd);

        // Project the 4 boundary points onto the face plane (don't require exact coplanarity —
        // in base.glb they sit slightly off the face marker's plane).
        var proj = new Vec3[4];
        for (int i = 0; i < 4; i++)
        {
            Vec3 rel = ent[start + 1 + i].Pos * ws - center;
            proj[i] = rel - n * Dot(rel, n);
        }

        // U = the first non-degenerate in-plane projection; V = N × U (right-handed, unit).
        Vec3 u = default;
        for (int i = 0; i < 4; i++)
            if (proj[i].LengthSquared() > 1e-8f)
            {
                u = Normalize(proj[i]);
                break;
            }
        if (u.LengthSquared() < 0.5f)
            u = AnyPerp(n); // all boundary points coincide with the face marker — degenerate door

        Vec3 v = Normalize(Vec3.Cross(n, u));
        // Re-orthogonalize U from the exact {V,N} pair so {U,V,N} is a clean orthonormal triad.
        u = Normalize(Vec3.Cross(v, n));

        float eu = 0f,
            ev = 0f;
        for (int i = 0; i < 4; i++)
        {
            eu = MathF.Max(eu, MathF.Abs(Dot(proj[i], u)));
            ev = MathF.Max(ev, MathF.Abs(Dot(proj[i], v)));
        }
        return new DockFace(center, n, u, v, eu, ev);
    }

    // Legacy fallback: a leftover marker becomes a small square "door" facing inward (−radial), so
    // a mis-authored asset still docks somewhere sane instead of crashing.
    private static DockFace LegacyDisc(Vec3 posAuthored, float ws)
    {
        Vec3 center = posAuthored * ws;
        Vec3 n = Normalize(posAuthored * -1f); // inward = toward the base centre
        if (n.LengthSquared() < 0.5f)
            n = new Vec3(0f, 1f, 0f);
        Vec3 u = AnyPerp(n);
        Vec3 v = Normalize(Vec3.Cross(n, u));
        u = Normalize(Vec3.Cross(v, n));
        return new DockFace(center, n, u, v, LegacyDiscRadius, LegacyDiscRadius);
    }

    // Trailing integer index of an "HP_..._<Index>" node name; 0 if absent (keeps a stable order).
    private static int ParseIndex(string name)
    {
        int us = name.LastIndexOf('_');
        if (us >= 0 && us + 1 < name.Length && int.TryParse(name.AsSpan(us + 1), out int idx))
            return idx;
        return 0;
    }

    private static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vec3 Normalize(Vec3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v * (1f / l) : default;
    }

    // Some unit vector perpendicular to n (n assumed unit-ish). Pick the world axis least aligned.
    private static Vec3 AnyPerp(Vec3 n)
    {
        Vec3 a = MathF.Abs(n.Y) < 0.9f ? new Vec3(0f, 1f, 0f) : new Vec3(1f, 0f, 0f);
        return Normalize(Vec3.Cross(n, a));
    }
}
