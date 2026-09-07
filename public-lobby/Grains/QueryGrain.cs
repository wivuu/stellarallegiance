using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

namespace PublicLobby.Grains;

// Per-silo read side (ADR-0002): lookups by non-key columns and aggregates/lists over a
// no-tracking context. Never writes. Point lookups are uncached (they must observe a row a grain
// just wrote); ladders and profiles carry a 5 s cache so a page refresh storm costs one query.
public interface IQueryGrain : IGrainWithIntegerKey
{
    [ReadOnly]
    Task<string?> FindDeviceCodeByUserCode(string userCode);

    [ReadOnly]
    Task<Guid?> FindPlayerIdByDisplayName(string displayName);

    /// <summary>Global ladder: players ordered by points (ranked-counted aggregates on `players`).</summary>
    [ReadOnly]
    Task<LadderPage> LadderGlobal(int page, int pageSize);

    /// <summary>Per-server ladder over every ended match on that game server (plan §1.3).</summary>
    [ReadOnly]
    Task<LadderPage> LadderByServer(Guid gameServerId, int page, int pageSize);

    /// <summary>Public profile + the player's most recent matches, by display name (citext).</summary>
    [ReadOnly]
    Task<PlayerProfileView?> PlayerByName(string displayName);

    /// <summary>A game server's page: identity, operator, recent matches, per-server ladder.</summary>
    [ReadOnly]
    Task<ServerHistoryView?> ServerHistory(Guid gameServerId);

    /// <summary>
    /// Game servers for the admin console (uncached — a toggle or a ban must show at once).
    /// <paramref name="query"/> matches the server or operator name; empty means all.
    /// </summary>
    [ReadOnly]
    Task<GameServerAdminRow[]> SearchGameServers(string? query, ServerFilter filter, DateTimeOffset now);

    /// <summary>
    /// Players for the admin console. <paramref name="query"/> matches the display name, an exact
    /// player id, or a linked external login; empty means "most recently seen".
    /// </summary>
    [ReadOnly]
    Task<PlayerAdminRow[]> SearchPlayers(string? query, PlayerFilter filter, DateTimeOffset now, int limit);

    /// <summary>
    /// Matches for the admin console, newest first. <paramref name="query"/> matches the map, the
    /// game server's name, or an exact match id.
    /// </summary>
    [ReadOnly]
    Task<MatchAdminRow[]> ListMatches(string? query, MatchFilter filter, int limit);

    /// <summary>Tab badges on /admin.</summary>
    [ReadOnly]
    Task<AdminCounts> AdminTabCounts();

    /// <summary>One match with its teams and pilot lines (empty while the match is still Active).</summary>
    [ReadOnly]
    Task<MatchDetailView?> MatchDetail(Guid matchId);

    /// <summary>/admin/players/{id}: everything the console shows except Identity's own logins.</summary>
    [ReadOnly]
    Task<PlayerAdminView?> PlayerForAdmin(Guid playerId, DateTimeOffset now);

    /// <summary>/admin/servers/{id}: identity, operator, match record. The listing comes from the registry.</summary>
    [ReadOnly]
    Task<GameServerAdminView?> GameServerForAdmin(Guid gameServerId);

    /// <summary>Live session lineages of one subject, so a ban can revoke every one of them.</summary>
    [ReadOnly]
    Task<Guid[]> ListSessionLineages(SubjectKind kind, Guid subjectId, DateTimeOffset now);
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
                        // Fallback name for a pilot line that outlived its player row (a deleted
                        // account): every surviving line of theirs carries the same tombstone name.
                        FrozenName = g.Max(x => x.mp.DisplayNameAtMatch),
                    });
                var total = await grouped.CountAsync();
                // LEFT join, deliberately: an inner join here would drop a deleted player's line
                // AFTER Skip/Take, so the page would come back short, `total` would over-count, and
                // every rank below them would be wrong.
                var rows = await grouped
                    .OrderByDescending(r => r.Points)
                    .ThenByDescending(r => r.Wins)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .GroupJoin(db.Players.AsNoTracking(), r => r.PlayerId, p => p.Id, (r, ps) => new { r, ps })
                    .SelectMany(
                        x => x.ps.DefaultIfEmpty(),
                        (x, p) => new { x.r, DisplayName = p == null ? x.r.FrozenName : p.DisplayName }
                    )
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
            p.CurrentListingId,
            BanRecord.From(p.BannedAt, p.BanExpiresAt, p.BanReason, p.BannedByPlayerId, p.BannedByDisplayName)
        );
        return new PlayerProfileView(snapshot, recent);
    }

    public async Task<ServerHistoryView?> ServerHistory(Guid gameServerId)
    {
        var server = await GrainFactory.GetGrain<IGameServerGrain>(gameServerId).Get();
        if (server is null)
            return null;
        var operatorName = server.OperatorPlayerId is { } opId
            ? (await GrainFactory.GetGrain<IPlayerGrain>(opId).Get())?.DisplayName
            : null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var recent = await db
            .Matches.AsNoTracking()
            .Where(m => m.GameServerId == gameServerId)
            .OrderByDescending(m => m.StartedAt)
            .Take(50)
            .Select(m => new MatchSummaryRow(
                m.Id,
                m.Map,
                m.StartedAt,
                m.EndedAt,
                m.WinnerTeam,
                m.Status,
                m.Counted,
                m.Ranked,
                db.MatchPilots.Count(p => p.MatchId == m.Id)
            ))
            .ToArrayAsync();
        var ladder = await LadderByServer(gameServerId, 1, 50);
        return new ServerHistoryView(server, operatorName, recent, ladder);
    }

    public async Task<GameServerAdminRow[]> SearchGameServers(string? query, ServerFilter filter, DateTimeOffset now)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = db.GameServers.AsNoTracking();
        servers = filter switch
        {
            ServerFilter.Ranked => servers.Where(g => g.Ranked),
            ServerFilter.Banned => servers.Where(g =>
                g.BannedAt != null && (g.BanExpiresAt == null || g.BanExpiresAt > now)
            ),
            _ => servers,
        };
        // LEFT join: a server whose operator was deleted has no players row, and dropping it from
        // /admin would hide the very rows an admin needs in order to reassign them.
        var rows =
            from gs in servers
            join p in db.Players.AsNoTracking() on gs.OperatorPlayerId equals p.Id into operators
            from op in operators.DefaultIfEmpty()
            select new
            {
                Server = gs,
                Operator = op == null ? null : op.DisplayName,
                Matches = db.Matches.Count(m => m.GameServerId == gs.Id),
            };
        if (Like(query) is { } pattern)
        {
            rows = rows.Where(x =>
                EF.Functions.ILike(x.Server.Name, pattern) || (x.Operator != null && EF.Functions.ILike(x.Operator, pattern))
            );
        }
        var found = await rows.OrderByDescending(x => x.Server.LastListedAt)
            .ThenByDescending(x => x.Server.CreatedAt)
            .ToArrayAsync();
        return
        [
            .. found.Select(x => new GameServerAdminRow(
                x.Server.Id,
                x.Server.Name,
                x.Server.OperatorPlayerId,
                x.Operator,
                x.Server.Ranked,
                x.Server.CreatedAt,
                x.Server.LastListedAt,
                x.Matches,
                Ban(x.Server)
            )),
        ];
    }

    public async Task<PlayerAdminRow[]> SearchPlayers(string? query, PlayerFilter filter, DateTimeOffset now, int limit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var players = db.Players.AsNoTracking();
        players = filter switch
        {
            PlayerFilter.Banned => players.Where(p =>
                p.BannedAt != null && (p.BanExpiresAt == null || p.BanExpiresAt > now)
            ),
            PlayerFilter.Admins => players.Where(p => p.IsAdmin),
            _ => players,
        };
        if (Like(query) is { } pattern)
        {
            // A player id pasted whole, the display name, or any linked external login (the
            // provider key is the Google sub / Steam id / GitHub login an admin would be given).
            var id = Guid.TryParse(query!.Trim(), out var parsed) ? parsed : (Guid?)null;
            players = players.Where(p =>
                EF.Functions.ILike(p.DisplayName, pattern)
                || p.Id == id
                // ProviderKey only: ProviderDisplayName is the scheme's name ("GitHub", "Google"),
                // identical for every user of that provider (AccountService.cs:82-85).
                || db.UserLogins.Any(l => l.UserId == p.Id && EF.Functions.ILike(l.ProviderKey, pattern))
            );
        }
        var rows = await players.OrderByDescending(p => p.LastSeenAt).Take(Math.Clamp(limit, 1, MaxPageSize)).ToArrayAsync();
        return
        [
            .. rows.Select(p => new PlayerAdminRow(
                p.Id,
                p.DisplayName,
                p.IsAdmin,
                p.Points,
                p.MatchesPlayed,
                p.LastSeenAt,
                Ban(p)
            )),
        ];
    }

    public async Task<MatchAdminRow[]> ListMatches(string? query, MatchFilter filter, int limit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var matches = db.Matches.AsNoTracking();
        matches = filter switch
        {
            MatchFilter.Live => matches.Where(m => m.Status == MatchStatus.Active),
            // "Not counted" means a match that finished without reaching the ladder — an Active one
            // has simply not had its chance yet.
            MatchFilter.Uncounted => matches.Where(m => m.Status != MatchStatus.Active && !m.Counted),
            _ => matches,
        };
        // Filter and order over the ENTITIES, then project: ordering by a property of a record the
        // query itself constructs is not translatable, and fails only at run time.
        var rows =
            from m in matches
            join gs in db.GameServers.AsNoTracking() on m.GameServerId equals gs.Id
            select new
            {
                Match = m,
                ServerId = gs.Id,
                ServerName = gs.Name,
                Pilots = db.MatchPilots.Count(p => p.MatchId == m.Id),
            };
        if (Like(query) is { } pattern)
        {
            var id = Guid.TryParse(query!.Trim(), out var parsed) ? parsed : (Guid?)null;
            rows = rows.Where(x =>
                EF.Functions.ILike(x.Match.Map, pattern) || EF.Functions.ILike(x.ServerName, pattern) || x.Match.Id == id
            );
        }
        var found = await rows.OrderByDescending(x => x.Match.StartedAt)
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .ToArrayAsync();
        return
        [
            .. found.Select(x => new MatchAdminRow(
                x.Match.Id,
                x.Match.StartedAt,
                x.Match.EndedAt,
                x.Match.Map,
                x.ServerId,
                x.ServerName,
                x.Match.ListingId,
                x.Match.Status,
                x.Match.WinnerTeam,
                x.Match.Counted,
                x.Match.Ranked,
                x.Pilots
            )),
        ];
    }

    // Uncached, like the lists below it: a tab badge that lagged a ban by 5 s while the row beneath
    // it already read "Banned" would look like a bug.
    public async Task<AdminCounts> AdminTabCounts()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return new AdminCounts(
            await db.GameServers.CountAsync(),
            await db.Players.CountAsync(),
            await db.Matches.CountAsync(),
            await db.Matches.CountAsync(m => m.Status == MatchStatus.Active)
        );
    }

    public async Task<MatchDetailView?> MatchDetail(Guid matchId)
    {
        // The snapshot comes from the grain so the end-reason literal is mapped in exactly one place.
        var match = await GrainFactory.GetGrain<IMatchGrain>(matchId).Get();
        if (match is null)
            return null;
        var server = await GrainFactory.GetGrain<IGameServerGrain>(match.GameServerId).Get();
        var operatorName = server?.OperatorPlayerId is { } operatorId
            ? (await GrainFactory.GetGrain<IPlayerGrain>(operatorId).Get())?.DisplayName
            : null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var teams = await db
            .MatchTeams.AsNoTracking()
            .Where(t => t.MatchId == matchId)
            .OrderBy(t => t.Team)
            .Select(t => new MatchTeamLine(t.Team, t.GarrisonsDestroyed, t.OutpostsDestroyed, t.Score))
            .ToArrayAsync();
        var pilots = await db
            .MatchPilots.AsNoTracking()
            .Where(p => p.MatchId == matchId)
            .OrderBy(p => p.Team)
            .ThenByDescending(p => p.Points)
            .Select(p => new MatchPilotLine(
                p.PlayerId,
                p.DisplayNameAtMatch,
                p.Team,
                p.Kills,
                p.Deaths,
                p.Ejects,
                p.Points,
                p.ConnectedAtEnd
            ))
            .ToArrayAsync();
        return new MatchDetailView(match, server?.Name ?? "?", server?.OperatorPlayerId, operatorName, teams, pilots);
    }

    public async Task<PlayerAdminView?> PlayerForAdmin(Guid playerId, DateTimeOffset now)
    {
        var player = await GrainFactory.GetGrain<IPlayerGrain>(playerId).Get();
        if (player is null)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var operated = await db
            .GameServers.AsNoTracking()
            .Where(g => g.OperatorPlayerId == playerId)
            .OrderByDescending(g => g.LastListedAt)
            .Select(g => new { Server = g, Matches = db.Matches.Count(m => m.GameServerId == g.Id) })
            .ToArrayAsync();
        var recent = await (
            from mp in db.MatchPilots.AsNoTracking()
            join m in db.Matches.AsNoTracking() on mp.MatchId equals m.Id
            join gs in db.GameServers.AsNoTracking() on m.GameServerId equals gs.Id
            where mp.PlayerId == playerId
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
        var sessions = await db
            .Sessions.AsNoTracking()
            .CountAsync(x =>
                x.SubjectKind == SubjectKind.Player && x.SubjectId == playerId && x.RevokedAt == null && x.ExpiresAt > now
            );
        var lastToken = await (
            from j in db.JoinTokensIssued.AsNoTracking()
            join gs in db.GameServers.AsNoTracking() on j.GameServerId equals gs.Id
            where j.PlayerId == playerId
            orderby j.IssuedAt descending
            select new { j.IssuedAt, gs.Name }
        ).FirstOrDefaultAsync();
        return new PlayerAdminView(
            player,
            [
                .. operated.Select(x => new OperatedServerRow(
                    x.Server.Id,
                    x.Server.Name,
                    x.Server.Ranked,
                    x.Server.LastListedAt,
                    x.Matches,
                    Ban(x.Server)
                )),
            ],
            recent,
            sessions,
            lastToken?.IssuedAt,
            lastToken?.Name
        );
    }

    public async Task<GameServerAdminView?> GameServerForAdmin(Guid gameServerId)
    {
        var server = await GrainFactory.GetGrain<IGameServerGrain>(gameServerId).Get();
        if (server is null)
            return null;
        var op = server.OperatorPlayerId is { } opId ? await GrainFactory.GetGrain<IPlayerGrain>(opId).Get() : null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var total = await db.Matches.CountAsync(m => m.GameServerId == gameServerId);
        var recent = await db
            .Matches.AsNoTracking()
            .Where(m => m.GameServerId == gameServerId)
            .OrderByDescending(m => m.StartedAt)
            .Take(RecentMatches)
            .Select(m => new MatchSummaryRow(
                m.Id,
                m.Map,
                m.StartedAt,
                m.EndedAt,
                m.WinnerTeam,
                m.Status,
                m.Counted,
                m.Ranked,
                db.MatchPilots.Count(p => p.MatchId == m.Id)
            ))
            .ToArrayAsync();
        return new GameServerAdminView(server, server.OperatorPlayerId, op?.DisplayName, op?.Ban, total, recent);
    }

    public async Task<Guid[]> ListSessionLineages(SubjectKind kind, Guid subjectId, DateTimeOffset now)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db
            .Sessions.AsNoTracking()
            .Where(s => s.SubjectKind == kind && s.SubjectId == subjectId && s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => s.LineageId)
            .Distinct()
            .ToArrayAsync();
    }

    static BanRecord? Ban(Player p) =>
        BanRecord.From(p.BannedAt, p.BanExpiresAt, p.BanReason, p.BannedByPlayerId, p.BannedByDisplayName);

    static BanRecord? Ban(GameServer g) =>
        BanRecord.From(g.BannedAt, g.BanExpiresAt, g.BanReason, g.BannedByPlayerId, g.BannedByDisplayName);

    // LIKE wildcards in what an admin typed must be literal; backslash is Postgres LIKE's default
    // escape character. Returns null for an empty search so callers can skip the predicate entirely.
    static string? Like(string? query)
    {
        var text = query?.Trim() ?? "";
        if (text.Length == 0)
            return null;
        return "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));
}
