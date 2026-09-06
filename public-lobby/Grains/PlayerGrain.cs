using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Grains;

// A Player (CONTEXT.md), keyed by player id = Identity user id. Single writer of the `players`
// row after account creation (ADR-0002; the row itself is inserted by AccountService at sign-up,
// which is the one write outside a grain). Loads the row on activation and serves reads from
// memory — the second read of a profile never touches Postgres (WP1.2 acceptance).
public interface IPlayerGrain : IGrainWithGuidKey
{
    [ReadOnly]
    Task<PlayerSnapshot?> Get();

    /// <summary>Record activity (last_seen_at). Cheap; called at token issue/refresh.</summary>
    Task Touch(DateTimeOffset now);

    Task SetAdmin(bool isAdmin);

    /// <summary>Change the display name (plan §1.1: 3–24 chars, unique case-insensitively).</summary>
    Task<RenameOutcome> Rename(string newDisplayName);

    /// <summary>
    /// Record that a join token was issued (plan §1.1: "the lobby records every issuance", the
    /// plausibility check for results) and move the player's presence to that listing (§1.2).
    /// </summary>
    Task RecordJoinToken(string jti, Guid gameServerId, string listingId, DateTimeOffset issuedAt, DateTimeOffset expiresAt);

    /// <summary>
    /// A match result was accepted (MatchGrain fan-out): fold RANKED-counted matches into the
    /// aggregates (the global Ladder) and clear presence on that listing.
    /// </summary>
    Task ApplyMatch(PlayerMatchDelta delta, DateTimeOffset now);
}

public sealed class PlayerGrain(IDbContextFactory<LobbyDbContext> dbFactory) : Grain, IPlayerGrain
{
    Player? _row;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await Reload(cancellationToken);
    }

    public Task<PlayerSnapshot?> Get() => Task.FromResult(_row is null ? null : Snapshot(_row));

    public async Task Touch(DateTimeOffset now)
    {
        if (_row is null && !await TryReload())
            return;
        // Coarse: one write per minute of activity is plenty for a "last seen" column.
        if (now - _row!.LastSeenAt < TimeSpan.FromMinutes(1))
            return;
        await Persist(p => p.LastSeenAt = now);
    }

    public async Task SetAdmin(bool isAdmin)
    {
        if (_row is null && !await TryReload())
            return;
        if (_row!.IsAdmin == isAdmin)
            return;
        await Persist(p => p.IsAdmin = isAdmin);
    }

    public async Task<RenameOutcome> Rename(string newDisplayName)
    {
        if (_row is null && !await TryReload())
            return RenameOutcome.Invalid;
        var name = newDisplayName?.Trim() ?? "";
        if (name.Length < LobbyLimits.DisplayNameMin || name.Length > LobbyLimits.DisplayNameMax || name.Any(char.IsControl))
            return RenameOutcome.Invalid;
        if (name == _row!.DisplayName)
            return RenameOutcome.Ok;
        var previous = _row.DisplayName;
        try
        {
            await Persist(p => p.DisplayName = name);
            return RenameOutcome.Ok;
        }
        catch (DbUpdateException e) when (PostgresErrors.IsUniqueViolation(e))
        {
            _row.DisplayName = previous;
            return RenameOutcome.Taken;
        }
    }

    public async Task RecordJoinToken(
        string jti,
        Guid gameServerId,
        string listingId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt
    )
    {
        if (_row is null && !await TryReload())
            throw new InvalidOperationException($"player {this.GetPrimaryKey():N} does not exist");
        await using var db = await dbFactory.CreateDbContextAsync();
        db.JoinTokensIssued.Add(
            new JoinTokenIssued
            {
                Jti = jti,
                PlayerId = _row!.Id,
                GameServerId = gameServerId,
                ListingId = listingId,
                IssuedAt = issuedAt,
                ExpiresAt = expiresAt,
            }
        );
        db.Players.Attach(_row);
        _row.CurrentListingId = listingId;
        _row.LastSeenAt = issuedAt;
        await db.SaveChangesAsync();
    }

    public async Task ApplyMatch(PlayerMatchDelta delta, DateTimeOffset now)
    {
        if (_row is null && !await TryReload())
            return;
        await Persist(p =>
        {
            if (delta.Ranked)
            {
                p.MatchesPlayed++;
                if (delta.Won)
                    p.Wins++;
                else
                    p.Losses++;
                p.Kills += delta.Kills;
                p.Deaths += delta.Deaths;
                p.Ejects += delta.Ejects;
                p.Points += delta.Points;
            }
            if (p.CurrentListingId == delta.ListingId)
                p.CurrentListingId = null;
            p.LastSeenAt = now;
        });
    }

    // A grain can be activated by a Get() racing account creation; re-read before giving up.
    async Task<bool> TryReload()
    {
        await Reload(CancellationToken.None);
        return _row is not null;
    }

    async Task Reload(CancellationToken cancellationToken)
    {
        var id = this.GetPrimaryKey();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _row = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    async Task Persist(Action<Player> mutate)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Players.Attach(_row!);
        mutate(_row!);
        await db.SaveChangesAsync();
    }

    static PlayerSnapshot Snapshot(Player p) =>
        new(
            p.Id,
            p.DisplayName,
            p.IsAdmin,
            p.MatchesPlayed,
            p.Wins,
            p.Losses,
            p.Kills,
            p.Deaths,
            p.Ejects,
            p.Points,
            p.CreatedAt,
            p.LastSeenAt,
            p.CurrentListingId
        );
}
