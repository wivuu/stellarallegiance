using SimServer.Sim;

namespace SimServer.Net;

// Builders for the richer data this server advertises to the public lobby's server browser:
// the map's sector-graph layout and the live per-team roster. Pure shaping, no transport —
// LobbyRegistrar owns when/how these go over the wire. The lobby binds JSON case-insensitively,
// so these records serialize with default (PascalCase) options just fine.
public static class LobbyStatus
{
    public sealed record MapBaseDto(int Team, float X, float Z);

    public sealed record MapGateDto(uint ToSector, float X, float Z);

    public sealed record MapSectorDto(uint Id, float Radius, string? Name, MapBaseDto[] Bases, MapGateDto[] Gates);

    public sealed record MapLayoutDto(MapSectorDto[] Sectors);

    public sealed record RosterDto(string Name, int Team, bool Ready, bool Flying);

    // Sector circles + team base positions + aleph gates — deliberately no asteroids or other
    // bulk geometry. The world is built once at boot and never mutates, so this is built once
    // and reused for every (re)registration.
    public static MapLayoutDto BuildMap(World world) =>
        new(
            world
                .Sectors.Select(s => new MapSectorDto(
                    s.Id,
                    MathF.Round(s.Radius),
                    string.IsNullOrWhiteSpace(s.Name) ? null : s.Name,
                    world
                        .Bases.Where(b => b.SectorId == s.Id)
                        .Select(b => new MapBaseDto(b.Team, Round1(b.Pos.X), Round1(b.Pos.Z)))
                        .ToArray(),
                    world
                        .Alephs.Where(g => g.SectorId == s.Id)
                        .Select(g => new MapGateDto(g.DestSectorId, Round1(g.Pos.X), Round1(g.Pos.Z)))
                        .ToArray()
                ))
                .ToArray()
        );

    // Sorted (Team, Name, Id) so equal lobby states always produce the same list — both the
    // registrar's change signature and the lobby's SequenceEqual dedup depend on stable order
    // (the underlying lobby dictionary iterates in no particular order).
    public static List<RosterDto> BuildRoster(List<LobbyEntry> snapshot) =>
        snapshot
            .OrderBy(e => e.Team)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Id)
            .Select(e => new RosterDto(Truncate(e.Name, 24), e.Team, e.Ready, e.HasShip))
            .ToList();

    // Compact change-detection key for WsSendLoop — cheaper to compare than the list itself.
    public static string RosterSignature(List<RosterDto> roster) =>
        string.Join("\u001f", roster.Select(r => $"{r.Team}:{r.Name}:{r.Ready}:{r.Flying}"));

    static float Round1(float v) => MathF.Round(v, 1);

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
