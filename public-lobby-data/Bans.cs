namespace PublicLobby.Data;

// A Ban (public-lobby/CONTEXT.md): a reversible bar on a player or a game server. Both `players`
// and `game_servers` carry the same five columns rather than a shared bans table — the current ban
// is the only thing anything asks about, both rows are already cached in memory by their grain, and
// the user ruled out an admin action log, so there is no history to keep.
//
// Expiry is evaluated at read time by InForce below: a lapsed ban needs no reminder, no sweeper and
// no write, and the columns stay as the record of what was done until an admin lifts it.
public static class Bans
{
    /// <summary>
    /// Whether a ban is in force at <paramref name="now"/>. A null <paramref name="bannedAt"/> means
    /// never banned; a null <paramref name="expiresAt"/> alongside a set bannedAt means permanent.
    /// </summary>
    public static bool InForce(DateTimeOffset? bannedAt, DateTimeOffset? expiresAt, DateTimeOffset now) =>
        bannedAt is not null && (expiresAt is null || expiresAt > now);
}
