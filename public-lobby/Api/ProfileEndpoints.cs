using Microsoft.AspNetCore.Identity;
using Orleans;
using PublicLobby.Auth;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Api;

// Plan §3.1 profile API for the Godot client's account page: GET/PATCH /api/me under a player
// bearer. Reads come from PlayerGrain (in-memory after activation); the rename goes through the
// grain, which owns the row and answers uniqueness with 409.
static class ProfileEndpoints
{
    public static void MapProfileApi(this WebApplication app)
    {
        var me = app.MapGroup("/api/me").RequireAuthorization(LobbyBearer.PlayerPolicy);

        me.MapGet(
            "",
            async (HttpContext http, IGrainFactory grains, UserManager<LobbyUser> users) =>
                await Profile(grains, users, LobbyBearer.SubjectId(http.User))
        );

        me.MapPatch(
            "",
            async (UpdateProfileRequest req, HttpContext http, IGrainFactory grains, UserManager<LobbyUser> users) =>
            {
                var id = LobbyBearer.SubjectId(http.User);
                var outcome = await grains.GetGrain<IPlayerGrain>(id).Rename(req.DisplayName ?? "");
                return outcome switch
                {
                    RenameOutcome.Ok => await Profile(grains, users, id),
                    RenameOutcome.Taken => Results.Json(
                        new { error = "That display name is taken." },
                        statusCode: StatusCodes.Status409Conflict
                    ),
                    _ => Results.BadRequest(
                        new
                        {
                            error = $"Display name must be {LobbyLimits.DisplayNameMin}-{LobbyLimits.DisplayNameMax} characters.",
                        }
                    ),
                };
            }
        );
    }

    static async Task<IResult> Profile(IGrainFactory grains, UserManager<LobbyUser> users, Guid id)
    {
        var p = await grains.GetGrain<IPlayerGrain>(id).Get();
        if (p is null)
            return Results.NotFound();
        var user = await users.FindByIdAsync(id.ToString());
        var logins = user is null
            ? []
            : (await users.GetLoginsAsync(user))
                .Select(l => new LinkedLoginDto(l.LoginProvider, l.ProviderDisplayName))
                .ToArray();
        return Results.Ok(
            new PlayerProfileDto(
                p.Id,
                p.DisplayName,
                p.IsAdmin,
                logins,
                p.MatchesPlayed,
                p.Wins,
                p.Losses,
                p.Kills,
                p.Deaths,
                p.Ejects,
                p.Points
            )
        );
    }
}
