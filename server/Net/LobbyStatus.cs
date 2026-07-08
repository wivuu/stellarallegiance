namespace SimServer.Net;

// Builders for the live per-team roster this server advertises to the public lobby's server
// browser. Pure shaping, no transport — LobbyRegistrar owns when/how these go over the wire. The
// lobby binds JSON case-insensitively, so these records serialize with default (PascalCase)
// options just fine. (Map layout is intentionally NOT advertised — the browser shows no preview.)
public static class LobbyStatus
{
    public sealed record RosterDto(string Name, int Team, bool Ready, bool Flying);

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

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
