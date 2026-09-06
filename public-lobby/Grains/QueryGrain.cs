using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;

namespace PublicLobby.Grains;

// Per-silo read side (ADR-0002): lookups by non-key columns and aggregates/lists over a
// no-tracking context. Never writes. Point lookups are uncached (they must observe a row a grain
// just wrote); ladders and profiles carry a 5 s cache so a page refresh storm costs one query.
public interface IQueryGrain : IGrainWithIntegerKey
{
    Task<string?> FindDeviceCodeByUserCode(string userCode);
    Task<Guid?> FindPlayerIdByDisplayName(string displayName);

    /// <summary>Global ladder: players ordered by points (ranked-counted aggregates on `players`).</summary>
    Task<LadderPage> LadderGlobal(int page, int pageSize);

    /// <summary>Per-server ladder over every ended match on that game server (plan §1.3).</summary>
    Task<LadderPage> LadderByServer(Guid gameServerId, int page, int pageSize);

    /// <summary>Public profile + the player's most recent matches, by display name (citext).</summary>
    Task<PlayerProfileView?> PlayerByName(string displayName);
}

[StatelessWorker(1)]
public sealed class QueryGrain(IDbContextFactory<LobbyDbContext> dbFactory, IMemoryCache cache) : Grain, IQueryGrain
{
    static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(5);
    public const int MaxPageSize = 100;
    const int RecentMatches = 20;

    public async Task<string?> FindDeviceCodeByUserCode(string userCode)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db
            .DeviceCodes.AsNoTracking()
            .Where(d => d.UserCode == userCode && d.Status == DeviceCodeStatus.Pending)
            .Select(d => d.Code)
            .SingleOrDefaultAsync();
    }

    public async Task<Guid?> FindPlayerIdByDisplayName(string displayName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // citext column: equality is case-insensitive on the database side.
        return await db
            .Players.AsNoTracking()
            .Where(p => p.DisplayName == displayName)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync();
    }

    public Task<LadderPage> LadderGlobal(int page, int pageSize)
    {
        (page, pageSize) = Clamp(page, pageSize);
        return cache.GetOrCreateAsync(
            $"ladder:global:{page}:{pageSize}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ListTtl;
                await using var db = await dbFactory.CreateDbContextAsync();
                var query = db.Players.AsNoTracking().Where(p => p.MatchesPlayed > 0);
                var total = await query.CountAsync();
                var rows = await query
                    .OrderByDescending(p => p.Points)
                    .ThenByDescending(p => p.Wins)
                    .ThenBy(p => p.DisplayName)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(p => new
                    {
                        p.Id,
                        p.DisplayName,
                        p.MatchesPlayed,
                        p.Wins,
                        p.Losses,
                        p.Kills,
                        p.Deaths,
                        p.Ejects,
                        p.Points,
                    })
                    .ToListAsync();
                var ranked = rows.Select(
                        (r, i) =>
                            new LadderRow(
                                (page - 1) * pageSize + i + 1,
                                r.Id,
                                r.DisplayName,
                                r.MatchesPlayed,
                                r.Wins,
                                r.Losses,
                                r.Kills,
                                r.Deaths,
                                r.Ejects,
                                r.Points
                            )
                    )
                    .ToArray();
                return new LadderPage(ranked, total, page, pageSize);
            }
        )!;
    }

    public Task<LadderPage> LadderByServer(Guid gameServerId, int page, int pageSize)
    {
        (page, pageSize) = Clamp(page, pageSize);
        return cache.GetOrCreateAsync(
            $"ladder:server:{gameServerId:N}:{page}:{pageSize}",
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ListTtl;
                await using var db = await dbFactory.CreateDbContextAsync();
                // Every ended match on this server counts here, ranked or not (plan §1.2 "per-server
                // history is recorded for every verified server regardless").
                var pilots =
                    from mp in db.MatchPilots.AsNoTracking()
                    join m in db.Matches.AsNoTracking() on mp.MatchId equals m.Id
                    where m.GameServerId == gameServerId && m.Status == MatchStatus.Ended
                    select new { mp, m };
                var grouped = pilots
                    .GroupBy(x => x.mp.PlayerId)
                    .Select(g => new
                    {
                        PlayerId = g.Key,
                        MatchesPlayed = g.Count(),
                        Wins = g.Count(x => x.mp.Won),
                        Losses = g.Count(x => !x.mp.Won),
                        Kills = g.Sum(x => x.mp.Kills),
                        Deaths = g.Sum(x => x.mp.Deaths),
                        Ejects = g.Sum(x => x.mp.Ejects),
                        Points = g.Sum(x => x.mp.Points),
                    });
                var total = await grouped.CountAsync();
                var rows = await grouped
                    .OrderByDescending(r => r.Points)
                    .ThenByDescending(r => r.Wins)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Join(db.Players.AsNoTracking(), r => r.PlayerId, p => p.Id, (r, p) => new { r, p.DisplayName })
                    .ToListAsync();
                var ranked = rows.Select(
                        (x, i) =>
                            new LadderRow(
                                (page - 1) * pageSize + i + 1,
                                x.r.PlayerId,
                                x.DisplayName,
                                x.r.MatchesPlayed,
                                x.r.Wins,
                                x.r.Losses,
                                x.r.Kills,
                                x.r.Deaths,
                                x.r.Ejects,
                                x.r.Points
                            )
                    )
                    .ToArray();
                return new LadderPage(ranked, total, page, pageSize);
            }
        )!;
    }

    public async Task<PlayerProfileView?> PlayerByName(string displayName)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var p = await db.Players.AsNoTracking().SingleOrDefaultAsync(x => x.DisplayName == displayName);
        if (p is null)
            return null;
        var recent = await (
            from mp in db.MatchPilots.AsNoTracking()
            join m in db.Matches.AsNoTracking() on mp.MatchId equals m.Id
            join gs in db.GameServers.AsNoTracking() on m.GameServerId equals gs.Id
            where mp.PlayerId == p.Id
            orderby m.StartedAt descending
            select new RecentMatchRow(
                m.Id,
                m.StartedAt,
                m.Map,
                gs.Name,
                mp.Team,
                mp.Won,
                mp.Kills,
                mp.Deaths,
                mp.Ejects,
                mp.Points,
                m.Counted,
                m.Ranked,
                m.Status
            )
        )
            .Take(RecentMatches)
            .ToArrayAsync();
        var snapshot = new PlayerSnapshot(
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
        return new PlayerProfileView(snapshot, recent);
    }

    static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
}
