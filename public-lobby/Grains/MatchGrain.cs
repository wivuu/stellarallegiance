using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Grains;

// A Match (CONTEXT.md): one game on one game server, keyed by the GUID the game server mints at
// StartMatch. Single writer of `matches`, `match_teams`, `match_pilots` (ADR-0002). Lifecycle
// (plan §1.3): Start (idempotent) → Complete (plausibility-checked, counted/ranked snapshot,
// fan-out to PlayerGrain/GameServerGrain) or Abandon (reminder: no result 10 min after the
// listing vanished). Completing a match that was never Started is accepted — the report carries
// everything and the spool may deliver out of order.
public interface IMatchGrain : IGrainWithGuidKey
{
    Task<MatchStartOutcome> Start(
        Guid gameServerId,
        string listingId,
        string map,
        DateTimeOffset startedAt,
        DateTimeOffset now
    );

    Task<MatchCompleteResult> Complete(MatchResultInput result, DateTimeOffset now);

    [ReadOnly]
    Task<MatchSnapshot?> Get();

    /// <summary>
    /// The abandonment rule, exposed so the suite can drive it with a clock: if the match is still
    /// active and its listing has been gone for 10 minutes, mark it abandoned. Returns true when it
    /// abandoned the match on this call.
    /// </summary>
    Task<bool> CheckAbandonment(DateTimeOffset now);
}

public sealed class MatchGrain(IDbContextFactory<LobbyDbContext> dbFactory, IServerRegistry registry)
    : Grain,
        IMatchGrain,
        IRemindable
{
    public static readonly TimeSpan AbandonAfterListingGone = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(5);
    static readonly TimeSpan PlausibilitySkew = TimeSpan.FromMinutes(5);
    const string AbandonReminder = "abandon";

    Match? _row;

    // Last time the listing was observed alive (memory only; on reactivation the first check
    // starts the clock afresh — a restart delays abandonment by at most one window).
    DateTimeOffset? _listingSeenAt;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var id = this.GetPrimaryKey();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _row = await db.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    public async Task<MatchStartOutcome> Start(
        Guid gameServerId,
        string listingId,
        string map,
        DateTimeOffset startedAt,
        DateTimeOffset now
    )
    {
        if (_row is not null)
            return _row.GameServerId == gameServerId ? MatchStartOutcome.AlreadyStarted : MatchStartOutcome.Conflict;
        var row = new Match
        {
            Id = this.GetPrimaryKey(),
            GameServerId = gameServerId,
            ListingId = listingId,
            Map = map,
            StartedAt = startedAt,
            Status = MatchStatus.Active,
        };
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Matches.Add(row);
            await db.SaveChangesAsync();
        }
        _row = row;
        _listingSeenAt = now;
        await this.RegisterOrUpdateReminder(AbandonReminder, ReminderPeriod, ReminderPeriod);
        return MatchStartOutcome.Started;
    }

    public async Task<MatchCompleteResult> Complete(MatchResultInput result, DateTimeOffset now)
    {
        if (_row is not null && _row.GameServerId != result.GameServerId)
            return new(MatchCompleteOutcome.Conflict, "match belongs to another game server");
        if (_row is not null && _row.Status != MatchStatus.Active)
            return new(MatchCompleteOutcome.AlreadyFinal, $"match is {_row.Status}");

        var endReason = result.EndReason switch
        {
            MatchEndReason.WinCondition => MatchEndReasonKind.WinCondition,
            MatchEndReason.Reset => MatchEndReasonKind.Reset,
            MatchEndReason.Shutdown => MatchEndReasonKind.Shutdown,
            _ => (MatchEndReasonKind?)null,
        };
        if (endReason is null)
            return new(MatchCompleteOutcome.Invalid, "unknown endReason");
        if (result.Pilots.Select(p => p.PlayerId).Distinct().Count() != result.Pilots.Length)
            return new(MatchCompleteOutcome.Invalid, "duplicate pilot");
        if (result.Teams.Select(t => t.Team).Distinct().Count() != result.Teams.Length)
            return new(MatchCompleteOutcome.Invalid, "duplicate team");

        // Plausibility (plan §1.2): every pilot must hold a player id that was issued a join token
        // for THIS game server before the match ended (5 min slack for clock skew between the game
        // server's endedAt and the lobby's issued_at). A single violation rejects the whole result.
        var anonymous = result.Pilots.FirstOrDefault(p => p.PlayerId is null);
        if (anonymous is not null)
            return new(MatchCompleteOutcome.Implausible, $"pilot '{anonymous.DisplayName}' has no player id");
        await using var db = await dbFactory.CreateDbContextAsync();
        var pilotIds = result.Pilots.Select(p => p.PlayerId!.Value).ToArray();
        var endedLimit = result.EndedAt + PlausibilitySkew;
        if (pilotIds.Length > 0)
        {
            var issuedTo = await db
                .JoinTokensIssued.AsNoTracking()
                .Where(j =>
                    j.GameServerId == result.GameServerId && pilotIds.Contains(j.PlayerId) && j.IssuedAt <= endedLimit
                )
                .Select(j => j.PlayerId)
                .Distinct()
                .ToListAsync();
            var missing = pilotIds.Except(issuedTo).ToArray();
            if (missing.Length > 0)
            {
                var name = result.Pilots.First(p => p.PlayerId == missing[0]).DisplayName;
                return new(
                    MatchCompleteOutcome.Implausible,
                    $"pilot '{name}' was never issued a join token for this game server"
                );
            }
        }

        // Counted only for win-condition endings with a winner; ranked standing snapshotted NOW from
        // the trust level and the server's flag (plan §1.3).
        var counted = endReason == MatchEndReasonKind.WinCondition && result.WinnerTeam is not null;
        var server = await GrainFactory.GetGrain<IGameServerGrain>(result.GameServerId).Get();
        var ranked = counted && (LobbyPolicy.RankedResultsAuthenticated || (server?.Ranked ?? false));

        var row = _row;
        if (row is null)
        {
            row = new Match
            {
                Id = this.GetPrimaryKey(),
                GameServerId = result.GameServerId,
                ListingId = result.ListingId,
                Map = result.Map,
                StartedAt = result.StartedAt,
            };
            db.Matches.Add(row);
        }
        else
            db.Matches.Attach(row);
        row.EndedAt = result.EndedAt;
        row.WinnerTeam = result.WinnerTeam;
        row.EndReason = endReason;
        row.Status = MatchStatus.Ended;
        row.Counted = counted;
        row.Ranked = ranked;
        foreach (var t in result.Teams)
            db.MatchTeams.Add(
                new MatchTeam
                {
                    MatchId = row.Id,
                    Team = t.Team,
                    GarrisonsDestroyed = t.GarrisonsDestroyed,
                    OutpostsDestroyed = t.OutpostsDestroyed,
                    Score = t.Score,
                }
            );
        foreach (var p in result.Pilots)
            db.MatchPilots.Add(
                new MatchPilot
                {
                    MatchId = row.Id,
                    PlayerId = p.PlayerId!.Value,
                    DisplayNameAtMatch = p.DisplayName,
                    Team = p.Team,
                    Kills = p.Kills,
                    Deaths = p.Deaths,
                    Ejects = p.Ejects,
                    Points = p.Points,
                    ConnectedAtEnd = p.ConnectedAtEnd,
                    Won = result.WinnerTeam is { } w && p.Team == w,
                }
            );
        await db.SaveChangesAsync();
        _row = row;

        // Fan-out: every pilot gets the match with their team's outcome (leavers included).
        foreach (var p in result.Pilots)
            await GrainFactory
                .GetGrain<IPlayerGrain>(p.PlayerId!.Value)
                .ApplyMatch(
                    new PlayerMatchDelta(
                        row.Id,
                        result.ListingId,
                        result.WinnerTeam is { } ww && p.Team == ww,
                        p.Kills,
                        p.Deaths,
                        p.Ejects,
                        p.Points,
                        counted,
                        ranked
                    ),
                    now
                );
        await GrainFactory.GetGrain<IGameServerGrain>(result.GameServerId).OnMatch(now);
        await UnregisterAbandonReminder();
        return new(MatchCompleteOutcome.Accepted, null);
    }

    public Task<MatchSnapshot?> Get() =>
        Task.FromResult(
            _row is null
                ? null
                : new MatchSnapshot(
                    _row.Id,
                    _row.GameServerId,
                    _row.ListingId,
                    _row.Map,
                    _row.StartedAt,
                    _row.EndedAt,
                    _row.WinnerTeam,
                    _row.EndReason switch
                    {
                        MatchEndReasonKind.WinCondition => MatchEndReason.WinCondition,
                        MatchEndReasonKind.Reset => MatchEndReason.Reset,
                        MatchEndReasonKind.Shutdown => MatchEndReason.Shutdown,
                        _ => null,
                    },
                    _row.Status,
                    _row.Counted,
                    _row.Ranked
                )
        );

    public async Task<bool> CheckAbandonment(DateTimeOffset now)
    {
        if (_row is null || _row.Status != MatchStatus.Active)
        {
            await UnregisterAbandonReminder();
            return false;
        }
        if (registry.Exists(_row.ListingId))
        {
            _listingSeenAt = now; // heartbeat re-arms the window
            return false;
        }
        var since = _listingSeenAt ?? now;
        _listingSeenAt ??= now;
        if (now - since < AbandonAfterListingGone)
            return false;

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Matches.Attach(_row);
            _row.Status = MatchStatus.Abandoned;
            _row.EndedAt = now;
            await db.SaveChangesAsync();
        }
        await UnregisterAbandonReminder();
        return true;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status) =>
        reminderName == AbandonReminder ? CheckAbandonment(DateTimeOffset.UtcNow) : Task.CompletedTask;

    async Task UnregisterAbandonReminder()
    {
        var reminder = await this.GetReminder(AbandonReminder);
        if (reminder is not null)
            await this.UnregisterReminder(reminder);
    }
}

/// <summary>What one match contributes to a player (PlayerGrain.ApplyMatch).</summary>
[GenerateSerializer]
public sealed record PlayerMatchDelta(
    [property: Id(0)] Guid MatchId,
    [property: Id(1)] string ListingId,
    [property: Id(2)] bool Won,
    [property: Id(3)] int Kills,
    [property: Id(4)] int Deaths,
    [property: Id(5)] int Ejects,
    [property: Id(6)] long Points,
    [property: Id(7)] bool Counted,
    [property: Id(8)] bool Ranked
);

// Trust level (plan §1.2): RANKED_RESULTS=flagged (default) | authenticated. Read per call so the
// suite can flip it; production sets it once.
static class LobbyPolicy
{
    public static bool RankedResultsAuthenticated =>
        string.Equals(
            Environment.GetEnvironmentVariable("RANKED_RESULTS"),
            "authenticated",
            StringComparison.OrdinalIgnoreCase
        );
}
