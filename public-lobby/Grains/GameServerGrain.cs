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

    async Task Persist(Action<GameServer> mutate)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.GameServers.Attach(_row!);
        mutate(_row!);
        await db.SaveChangesAsync();
    }
}
