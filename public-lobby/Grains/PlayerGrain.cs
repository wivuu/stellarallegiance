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

    /// <summary>
    /// Apply a Ban (CONTEXT.md), replacing any previous one. Bans are enforced at read time from
    /// the snapshot, so this write is all a ban is — the caller separately revokes live sessions.
    /// </summary>
    Task Ban(BanRecord ban);

    /// <summary>Lift a ban, clearing what was recorded about it.</summary>
    Task Unban();

    /// <summary>
    /// Erase this player completely: the account, its Identity rows (external logins, passkeys,
    /// roles, tokens — all cascaded from asp_net_users), its sessions, and its ladder standing.
    /// <paramref name="mode"/> decides what happens to the matches they already played. Returns
    /// false if there is no such player. There is no undo.
    /// </summary>
    Task<bool> Delete(PlayerDeleteMode mode, DateTimeOffset now);

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

    public async Task Ban(BanRecord ban)
    {
        if (_row is null && !await TryReload())
            return;
        await Persist(p =>
        {
            p.BannedAt = ban.At;
            p.BanExpiresAt = ban.Until;
            p.BanReason = ban.Reason;
            p.BannedByPlayerId = ban.ByPlayerId;
            p.BannedByDisplayName = ban.ByDisplayName;
        });
    }

    public async Task Unban()
    {
        if (_row is null && !await TryReload())
            return;
        if (_row!.BannedAt is null)
            return;
        await Persist(p =>
        {
            p.BannedAt = null;
            p.BanExpiresAt = null;
            p.BanReason = null;
            p.BannedByPlayerId = null;
            p.BannedByDisplayName = null;
        });
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

    public async Task<bool> Delete(PlayerDeleteMode mode, DateTimeOffset now)
    {
        if (_row is null && !await TryReload())
            return false;
        var id = _row!.Id;

        // Two things are owned by OTHER grains that keep their rows in memory, so they are done
        // through those grains first and are therefore NOT inside the transaction below. If the
        // transaction then failed, sessions would be revoked (they can sign in again) and servers
        // orphaned (an admin can reassign them) — both recoverable, unlike the reverse order, where
        // a deleted player would leave grains still serving their row from memory.
        var query = GrainFactory.GetGrain<IQueryGrain>(0);
        foreach (var lineage in await query.ListSessionLineages(SubjectKind.Player, id, now))
            await GrainFactory.GetGrain<ISessionGrain>(lineage).Revoke(now);

        Guid[] operated;
        await using (var probe = await dbFactory.CreateDbContextAsync())
        {
            operated = await probe
                .GameServers.AsNoTracking()
                .Where(g => g.OperatorPlayerId == id)
                .Select(g => g.Id)
                .ToArrayAsync();
        }
        foreach (var serverId in operated)
            await GrainFactory.GetGrain<IGameServerGrain>(serverId).ClearOperator();

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        // match_pilots is MatchGrain's table, but MatchGrain caches only the `matches` row — never
        // pilots — so nothing goes stale, and fanning this out over hundreds of match grains to
        // rewrite one column would be far worse than one statement.
        if (mode == PlayerDeleteMode.AnonymisePilots)
        {
            var tombstone = Tombstone(id);
            await db
                .MatchPilots.Where(p => p.PlayerId == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.DisplayNameAtMatch, tombstone));
        }
        else
        {
            await db.MatchPilots.Where(p => p.PlayerId == id).ExecuteDeleteAsync();
        }

        // join_tokens_issued rows are deliberately KEPT: they are the plausibility evidence every
        // pilot in a result needs (MatchGrain.Complete), and one missing row rejects the whole
        // result — deleting a player mid-match would cost everyone else in it the game.
        await db.Sessions.Where(x => x.SubjectKind == SubjectKind.Player && x.SubjectId == id).ExecuteDeleteAsync();
        // players before asp_net_users: players.id is a Restrict FK onto it. Identity's own child
        // tables (logins, passkeys, claims, roles, tokens) cascade from asp_net_users in the DB.
        await db.Players.Where(p => p.Id == id).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();

        _row = null;
        DeactivateOnIdle();
        return true;
    }

    /// <summary>
    /// The name an anonymised pilot line carries. The id fragment keeps two deleted players in the
    /// same match apart; it identifies nobody once the account and its logins are gone.
    /// </summary>
    internal static string Tombstone(Guid playerId) => $"Deleted pilot {playerId:N}"[..18];

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
            p.CurrentListingId,
            BanRecord.From(p.BannedAt, p.BanExpiresAt, p.BanReason, p.BannedByPlayerId, p.BannedByDisplayName)
        );
}
