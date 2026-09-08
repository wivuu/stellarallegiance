using Orleans;
using PublicLobby.Grains;

namespace PublicLobby.Accounts;

// One place for "is this player banned right now, and what do I tell them" — used by every cookie
// sign-in path and by the cookie-authorized pages that can still do damage with a cookie issued
// before the ban (approving a device code, renaming).
//
// The bearer side does not go through here: it checks the snapshot it already holds
// (Auth/LobbyBearerAuthentication.cs) so it costs one grain hop, not two.
public static class LobbyBans
{
    /// <summary>The ban in force on this player, or null.</summary>
    public static async Task<BanRecord?> InForce(IGrainFactory grains, Guid playerId, DateTimeOffset now)
    {
        var snapshot = await grains.GetGrain<IPlayerGrain>(playerId).Get();
        return snapshot is not null && snapshot.Ban.IsBanned(now) ? snapshot.Ban : null;
    }

    /// <summary>What /login shows a banned player in its existing error callout.</summary>
    public static string SignInMessage(BanRecord ban)
    {
        var window = ban.Until is { } until ? $"until {until:yyyy-MM-dd HH:mm} UTC" : "permanently";
        var reason = string.IsNullOrWhiteSpace(ban.Reason) ? "No reason was recorded." : ban.Reason;
        return $"You are banned {window}. {reason} You can still read the ladder and your match history.";
    }
}
