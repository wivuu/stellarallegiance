using Microsoft.AspNetCore.Identity;
using Orleans;
using PublicLobby.Data;
using PublicLobby.Grains;

namespace PublicLobby.Pages;

// Shared by /admin and the two detail pages: turning the ban dialog's inputs into a BanRecord, and
// the two side effects a ban has beyond the row itself — revoking the subject's live sessions, and
// dropping the Listing it is holding right now so it leaves the browser at once instead of after
// the registry's 30 s TTL.
//
// Enforcement itself is NOT here: a ban is a fact on the row, read back through the snapshot at
// every seam (see public-lobby/README.md). This class only applies one.
static class AdminModeration
{
    /// <summary>The duration chips on the ban dialog, in the order they are drawn.</summary>
    public static readonly (string Id, string Label)[] Durations =
    [
        ("24h", "24 hours"),
        ("7d", "7 days"),
        ("30d", "30 days"),
        ("perm", "Permanent"),
    ];

    public const string DefaultDuration = "7d";

    /// <summary>Null = permanent, matching Bans.InForce's reading of a null expiry.</summary>
    public static DateTimeOffset? Expiry(string? duration, DateTimeOffset now) =>
        duration switch
        {
            "24h" => now.AddHours(24),
            "7d" => now.AddDays(7),
            "30d" => now.AddDays(30),
            _ => null,
        };

    public static BanRecord Record(
        string? reason,
        string? duration,
        Guid? byPlayerId,
        string byDisplayName,
        DateTimeOffset now
    ) => new(now, Expiry(duration, now), (reason ?? "").Trim(), byPlayerId, byDisplayName);

    /// <summary>
    /// Ban a player: the row, then every live session lineage, then the listings of any game server
    /// they operate (the operator check at POST /servers keeps them off once they are down).
    /// </summary>
    public static async Task BanPlayer(
        IGrainFactory grains,
        IServerRegistry registry,
        UserManager<LobbyUser> users,
        Guid playerId,
        BanRecord ban,
        DateTimeOffset now
    )
    {
        await grains.GetGrain<IPlayerGrain>(playerId).Ban(ban);
        await RevokeSessions(grains, SubjectKind.Player, playerId, now);
        // Bearer tokens die with their lineage above, but the 30-day browser cookie carries its
        // claims until Identity revalidates the security stamp; bumping it caps that at the
        // SecurityStampValidator interval instead of a month. The cookie-authed pages that can do
        // damage (/device approve, /me rename) check the ban directly on top of this.
        if (await users.FindByIdAsync(playerId.ToString()) is { } user)
            await users.UpdateSecurityStampAsync(user);
        var view = await grains.GetGrain<IQueryGrain>(0).PlayerForAdmin(playerId, now);
        foreach (var server in view?.Servers ?? [])
            DropListing(registry, server.Id);
    }

    /// <summary>
    /// Ban a game server: the row, optionally its Ranked flag, its own sessions, and its listing.
    /// Its recorded matches are left exactly as they are.
    /// </summary>
    public static async Task BanGameServer(
        IGrainFactory grains,
        IServerRegistry registry,
        Guid gameServerId,
        BanRecord ban,
        bool alsoUnrank,
        DateTimeOffset now
    )
    {
        var server = grains.GetGrain<IGameServerGrain>(gameServerId);
        await server.Ban(ban);
        if (alsoUnrank)
            await server.SetRanked(false);
        // Its sessions are deliberately NOT revoked. A sim server deletes its stored credential and
        // re-enters the device flow on ANY refusal to refresh (server/Net/LobbyAuthSession.cs:127),
        // and reads a 401 from POST /servers as "refresh and retry" — so breaking its token would
        // put it in an approval loop instead of stopping it, and would prompt its operator to
        // re-pair. Leaving the token valid is what lets the 403 at the listing, join and match seams
        // do the work, and lets the server pick itself up when the ban is lifted.
        DropListing(registry, gameServerId);
    }

    /// <summary>Remove whatever Listing this game server is holding, if any.</summary>
    public static void DropListing(IServerRegistry registry, Guid gameServerId)
    {
        foreach (var entry in registry.ListActive())
        {
            if (entry.GameServerId == gameServerId)
                registry.Remove(entry.SessionId);
        }
    }

    static async Task RevokeSessions(IGrainFactory grains, SubjectKind kind, Guid subjectId, DateTimeOffset now)
    {
        var lineages = await grains.GetGrain<IQueryGrain>(0).ListSessionLineages(kind, subjectId, now);
        foreach (var lineage in lineages)
            await grains.GetGrain<ISessionGrain>(lineage).Revoke(now);
    }
}
