using Orleans;
using PublicLobby.Auth;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Api;

// Match ingestion from game servers (plan §1.3/§3.1/§3.3), server bearer only. The game server
// id is ALWAYS the bearer's subject — a body that names another server is refused. Idempotent
// by match id; the grain answers 202 accepted / 409 already-final / 422 plausibility.
static class MatchEndpoints
{
    public static void MapMatchApi(this WebApplication app)
    {
        var matches = app.MapGroup("/matches").RequireAuthorization(LobbyBearer.ServerPolicy);

        matches.MapPost(
            "",
            async (
                MatchStartRequest req,
                HttpContext http,
                IServerRegistry registry,
                IGrainFactory grains,
                TimeProvider clock
            ) =>
            {
                var gameServerId = LobbyBearer.SubjectId(http.User);
                if (
                    req.MatchId == Guid.Empty
                    || string.IsNullOrWhiteSpace(req.ListingId)
                    || string.IsNullOrWhiteSpace(req.Map)
                )
                    return Results.BadRequest(new { error = "matchId, listingId and map are required" });
                // The listing, if still alive, must be this server's.
                var listing = registry.Get(req.ListingId);
                if (listing is not null && listing.GameServerId != gameServerId)
                    return Results.Json(
                        new { error = "listing belongs to another game server" },
                        statusCode: StatusCodes.Status403Forbidden
                    );
                var outcome = await grains
                    .GetGrain<IMatchGrain>(req.MatchId)
                    .Start(gameServerId, req.ListingId.Trim(), req.Map.Trim(), req.StartedAt, clock.GetUtcNow());
                return outcome switch
                {
                    MatchStartOutcome.Started => Results.Accepted($"/matches/{req.MatchId}"),
                    MatchStartOutcome.AlreadyStarted => Results.Ok(),
                    _ => Results.Json(
                        new { error = "match id already used by another game server" },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                };
            }
        );

        matches.MapPost(
            "/{matchId:guid}/result",
            async (Guid matchId, MatchResultReport report, HttpContext http, IGrainFactory grains, TimeProvider clock) =>
            {
                var gameServerId = LobbyBearer.SubjectId(http.User);
                if (report.MatchId != matchId)
                    return Results.BadRequest(new { error = "matchId in body does not match the path" });
                if (report.GameServerId != gameServerId)
                    return Results.Json(
                        new { error = "gameServerId is not the bearer's" },
                        statusCode: StatusCodes.Status403Forbidden
                    );
                if (report.Teams is null || report.Pilots is null || string.IsNullOrWhiteSpace(report.EndReason))
                    return Results.BadRequest(new { error = "teams, pilots and endReason are required" });

                var input = new MatchResultInput(
                    gameServerId,
                    report.ListingId,
                    report.Map,
                    report.StartedAt,
                    report.EndedAt,
                    report.WinnerTeam,
                    report.EndReason,
                    report
                        .Teams.Select(t => new MatchTeamLine(t.Team, t.GarrisonsDestroyed, t.OutpostsDestroyed, t.Score))
                        .ToArray(),
                    report
                        .Pilots.Select(p => new MatchPilotLine(
                            p.PlayerId,
                            p.DisplayName,
                            p.Team,
                            p.Kills,
                            p.Deaths,
                            p.Ejects,
                            p.Points,
                            p.ConnectedAtEnd
                        ))
                        .ToArray()
                );
                var result = await grains.GetGrain<IMatchGrain>(matchId).Complete(input, clock.GetUtcNow());
                return result.Outcome switch
                {
                    MatchCompleteOutcome.Accepted => Results.Accepted($"/matches/{matchId}"),
                    MatchCompleteOutcome.AlreadyFinal => Results.Json(
                        new { error = result.Reason },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                    MatchCompleteOutcome.Implausible => Results.Json(
                        new { error = result.Reason },
                        statusCode: StatusCodes.Status422UnprocessableEntity
                    ),
                    MatchCompleteOutcome.Conflict => Results.Json(
                        new { error = result.Reason },
                        statusCode: StatusCodes.Status403Forbidden
                    ),
                    _ => Results.BadRequest(new { error = result.Reason }),
                };
            }
        );
    }
}
