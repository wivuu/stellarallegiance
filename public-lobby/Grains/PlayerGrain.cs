using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

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
