using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

namespace PublicLobby.Grains;

// A Game Server (CONTEXT.md): durable, operator-owned identity minted at first device-code
// approval. Single writer of its `game_servers` row (ADR-0002). Listings (the live registration)
// stay in InMemoryServerRegistry; this grain only remembers that it listed (last_listed_at).
public interface IGameServerGrain : IGrainWithGuidKey
{
    Task Create(Guid operatorPlayerId, string name, DateTimeOffset now);

    [ReadOnly]
    Task<GameServerSnapshot?> Get();

    /// <summary>Listing registered/heartbeat: bump last_listed_at (coarsely).</summary>
    Task OnListed(DateTimeOffset now);

    /// <summary>Admin toggle (CONTEXT.md "Ranked").</summary>
    Task SetRanked(bool ranked);

    /// <summary>A match result from this server was accepted.</summary>
    Task OnMatch(DateTimeOffset now);

    /// <summary>
    /// Apply a Ban (CONTEXT.md), replacing any previous one. A banned game server is refused
    /// listings, join tokens and results; its recorded matches are untouched.
    /// </summary>
    Task Ban(BanRecord ban);

    /// <summary>Lift a ban, clearing what was recorded about it.</summary>
    Task Unban();

    /// <summary>
    /// The operator's account was deleted: orphan this server. It keeps its id and its match record
    /// — every match it reported points at that id — but is refused a Listing until it is adopted.
    /// </summary>
    Task ClearOperator();

    /// <summary>Hand this game server to another player (an admin adopting an orphan).</summary>
    Task SetOperator(Guid operatorPlayerId);

    /// <summary>
    /// Erase this game server and everything the ledger keeps under its id: the matches it
    /// reported (with their team and pilot lines), the join tokens issued for it, and its own
    /// sessions. Returns false when there was nothing to delete.
    ///
    /// Irreversible, and NOT a rollback: the kills, points and wins those matches already added to
    /// each player are cumulative counters on `players` (PlayerGrain.OnMatch), not a projection of
    /// match_pilots, so the global ladder keeps them. Only the per-server ladder and the match
    /// history go. Ban instead if the record should stay readable.
    /// </summary>
    Task<bool> Delete(DateTimeOffset now);
}

public sealed class GameServerGrain(IDbContextFactory<LobbyDbContext> dbFactory) : Grain, IGameServerGrain
{
    GameServer? _row;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var id = this.GetPrimaryKey();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _row = await db.GameServers.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
    }

    public async Task Create(Guid operatorPlayerId, string name, DateTimeOffset now)
    {
        if (_row is not null)
            throw new InvalidOperationException($"game server {this.GetPrimaryKey():N} already exists");
        var row = new GameServer
        {
            Id = this.GetPrimaryKey(),
            OperatorPlayerId = operatorPlayerId,
            Name = name,
            Ranked = false,
            CreatedAt = now,
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.GameServers.Add(row);
        await db.SaveChangesAsync();
        _row = row;
    }

    public Task<GameServerSnapshot?> Get() =>
        Task.FromResult(
            _row is null
                ? null
                : new GameServerSnapshot(
                    _row.Id,
                    _row.OperatorPlayerId,
                    _row.Name,
                    _row.Ranked,
                    _row.CreatedAt,
                    _row.LastListedAt,
                    BanRecord.From(
                        _row.BannedAt,
                        _row.BanExpiresAt,
                        _row.BanReason,
                        _row.BannedByPlayerId,
                        _row.BannedByDisplayName
                    )
                )
        );

    public async Task OnListed(DateTimeOffset now)
    {
        if (_row is null)
            return;
        if (_row.LastListedAt is { } last && now - last < TimeSpan.FromMinutes(1))
            return;
        await Persist(g => g.LastListedAt = now);
    }

    // No per-server match counter column in v1 (history is queried); a result is proof of life.
    public Task OnMatch(DateTimeOffset now) => OnListed(now);

    public async Task SetRanked(bool ranked)
    {
        if (_row is null || _row.Ranked == ranked)
            return;
        await Persist(g => g.Ranked = ranked);
    }

    public async Task Ban(BanRecord ban)
    {
        if (_row is null)
            return;
        await Persist(g =>
        {
            g.BannedAt = ban.At;
            g.BanExpiresAt = ban.Until;
            g.BanReason = ban.Reason;
            g.BannedByPlayerId = ban.ByPlayerId;
            g.BannedByDisplayName = ban.ByDisplayName;
        });
    }

    public async Task Unban()
    {
        if (_row is null || _row.BannedAt is null)
            return;
        await Persist(g =>
        {
            g.BannedAt = null;
            g.BanExpiresAt = null;
            g.BanReason = null;
            g.BannedByPlayerId = null;
            g.BannedByDisplayName = null;
        });
    }

    public async Task ClearOperator()
    {
        if (_row is null || _row.OperatorPlayerId is null)
            return;
        await Persist(g => g.OperatorPlayerId = null);
    }

    public async Task SetOperator(Guid operatorPlayerId)
    {
        if (_row is null || _row.OperatorPlayerId == operatorPlayerId)
            return;
        await Persist(g => g.OperatorPlayerId = operatorPlayerId);
    }

    public async Task<bool> Delete(DateTimeOffset now)
    {
        if (_row is null)
            return false;
        var id = this.GetPrimaryKey();

        // Sessions and matches are owned by OTHER grains that hold their row in memory, so they are
        // told first and are therefore NOT inside the transaction below — the same ordering (and
        // reasoning) as PlayerGrain.Delete. If the transaction then failed, a revoked session and a
        // forgotten match grain are both recoverable; a grain still serving a row that has been
        // deleted underneath it is not.
        var query = GrainFactory.GetGrain<IQueryGrain>(0);
        foreach (var lineage in await query.ListSessionLineages(SubjectKind.Server, id, now))
            await GrainFactory.GetGrain<ISessionGrain>(lineage).Revoke(now);

        Guid[] matchIds;
        await using (var probe = await dbFactory.CreateDbContextAsync())
        {
            matchIds = await probe.Matches.AsNoTracking().Where(m => m.GameServerId == id).Select(m => m.Id).ToArrayAsync();
        }
        foreach (var matchId in matchIds)
            await GrainFactory.GetGrain<IMatchGrain>(matchId).Forget();

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        // Children first: every FK onto `matches` and `game_servers` is Restrict, so the order here
        // is the delete order the database will accept.
        await db.MatchPilots.Where(p => matchIds.Contains(p.MatchId)).ExecuteDeleteAsync();
        await db.MatchTeams.Where(t => matchIds.Contains(t.MatchId)).ExecuteDeleteAsync();
        await db.Matches.Where(m => m.GameServerId == id).ExecuteDeleteAsync();
        // Unlike a player deletion, these join_tokens_issued rows are safe to drop: they are the
        // plausibility evidence for results reported by THIS server, and it will never report
        // another one.
        await db.JoinTokensIssued.Where(j => j.GameServerId == id).ExecuteDeleteAsync();
        await db.Sessions.Where(x => x.SubjectKind == SubjectKind.Server && x.SubjectId == id).ExecuteDeleteAsync();
        await db.GameServers.Where(g => g.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();

        _row = null;
        DeactivateOnIdle();
        return true;
    }

    async Task Persist(Action<GameServer> mutate)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.GameServers.Attach(_row!);
        mutate(_row!);
        await db.SaveChangesAsync();
    }
}
