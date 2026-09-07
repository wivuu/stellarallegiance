namespace PublicLobby;

// The anonymous-safe projection of the live Listings (public-lobby/CONTEXT.md "Listing"), shared by
// the public web pages ("/" and "/ladder") and the SSE stream that keeps them fresh
// (GET /servers/live). ONE type so the server-rendered first paint and every subsequent live update
// agree on both the contents and the ordering.
//
// What it deliberately drops from ServerEntry: sessionId, publicEndpoint, iceServers, gameServerId
// and the roster. GET /servers stays player-bearer gated (plan §1.5 — "anonymous sees no server
// list"); this strip is the liveness signal the public pages already showed, nothing more. The
// sessionId matters most: it's the address the unauthenticated signaling routes
// (POST /servers/{sessionId}/connect) take, so broadcasting it anonymously would widen that surface.

// One listing as the public strip renders it (Pages/Shared/_ServerRow.cshtml). StateLabel/StateCss
// are the badge mapping resolved once here — "playing or not" is exactly what these pages are
// live-updating, so it gets a single source shared with the admin pages.
public sealed record PublicServerRow(
    string Name,
    bool Verified,
    string? OperatorName,
    bool Protected,
    int Players,
    int MaxPlayers,
    string StateLabel,
    string StateCss
)
{
    public static PublicServerRow From(ServerEntry e)
    {
        var (label, css) = StateBadge(e.State);
        return new PublicServerRow(e.Name, e.Verified, e.OperatorName, e.Protected, e.Players, e.MaxPlayers, label, css);
    }

    // Listing state as ServerEntry.State reports it ("lobby" / "in-progress" / "ended"), mapped to
    // the label + palette token the strip renders.
    public static (string Label, string Css) StateBadge(string? state) =>
        state switch
        {
            "in-progress" => ("In progress", "text-ok"),
            "lobby" => ("Lobby", "text-accent"),
            "ended" => ("Ended", "text-text-dim"),
            _ => ("Idle", "text-text-dim"),
        };
}

// The whole strip: totals across EVERY active listing plus the busiest few, ordered the way both
// the Razor partial and the SSE snapshot must render them.
public sealed record PublicServerStrip(int ServersOnline, int PilotsOnline, IReadOnlyList<PublicServerRow> Servers)
{
    public static readonly PublicServerStrip Empty = new(0, 0, []);

    // How many listings the public strip renders (the rest collapse into the "N more listed" hint).
    // Lives here, not on the page model, because GET /servers/live has to cut the list identically.
    public const int Shown = 5;

    // Listings beyond the `take` shown — the page renders a "N more listed" hint.
    public int More => Math.Max(0, ServersOnline - Servers.Count);

    public static PublicServerStrip From(IReadOnlyCollection<ServerEntry> active, int take) =>
        new(
            active.Count,
            active.Sum(s => s.Players),
            [.. active.OrderByDescending(s => s.Players).ThenBy(s => s.Name).Take(take).Select(PublicServerRow.From)]
        );
}
