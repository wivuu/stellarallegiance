using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using PublicLobby.Grains;
using PublicLobby.Hosting;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Auth;

// Plan §3.1 identity routes: one RFC 8628 device-code flow for Godot clients AND game servers,
// opaque session tokens with rotating refresh (SessionGrain), and revocation. Error bodies follow
// RFC 8628/6749 ({"error": ...}, HTTP 400) so an off-the-shelf device-flow client would behave.
static class AuthEndpoints
{
    const string DevLoginEnvVar = "AUTH_DEV_LOGIN";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth");

        auth.MapPost(
            "/device",
            async (DeviceAuthRequest req, IGrainFactory grains, LobbyPublicUrl publicUrl, TimeProvider clock) =>
            {
                SubjectKind kind;
                switch (req.Client)
                {
                    case LobbyClientKind.Godot:
                        kind = SubjectKind.Player;
                        break;
                    case LobbyClientKind.SimServer:
                        if (InMemoryServerRegistry.NormalizeName(req.ServerName) is null)
                            return Results.BadRequest(
                                new TokenErrorResponse(
                                    "invalid_request",
                                    $"serverName must be {InMemoryServerRegistry.NameMin}-{InMemoryServerRegistry.NameMax} characters"
                                )
                            );
                        kind = SubjectKind.Server;
                        break;
                    default:
                        return Results.BadRequest(
                            new TokenErrorResponse("invalid_request", "client must be godot or sim-server")
                        );
                }

                var deviceCode = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
                var userCode = await grains
                    .GetGrain<IDeviceCodeGrain>(deviceCode)
                    .Start(kind, req.ServerName, clock.GetUtcNow());
                var pretty = DeviceCodeGrain.FormatUserCode(userCode);
                return Results.Ok(
                    new DeviceAuthResponse(
                        deviceCode,
                        pretty,
                        $"{publicUrl.Value}/device",
                        $"{publicUrl.Value}/device?user_code={pretty}",
                        (int)DeviceCodeGrain.Lifetime.TotalSeconds,
                        DeviceCodeGrain.PollIntervalSeconds
                    )
                );
            }
        );

        auth.MapPost(
            "/token",
            async (HttpContext http, IGrainFactory grains, AccountService accounts, TimeProvider clock) =>
            {
                var req = await ReadTokenRequest(http);
                if (req is null)
                    return TokenError("invalid_request", "grant_type required");
                var now = clock.GetUtcNow();

                switch (req.GrantType)
                {
                    case LobbyGrantType.DeviceCode:
                    {
                        if (!IsWellFormedDeviceCode(req.DeviceCode))
                            return TokenError(LobbyTokenError.InvalidGrant);
                        var poll = await grains.GetGrain<IDeviceCodeGrain>(req.DeviceCode!).Poll(now);
                        return poll.Outcome switch
                        {
                            DevicePollOutcome.Pending => TokenError(LobbyTokenError.AuthorizationPending),
                            DevicePollOutcome.SlowDown => TokenError(LobbyTokenError.SlowDown),
                            DevicePollOutcome.Expired => TokenError(LobbyTokenError.ExpiredToken),
                            DevicePollOutcome.Denied => TokenError(LobbyTokenError.AccessDenied),
                            DevicePollOutcome.Approved => await IssueNewLineage(
                                grains,
                                poll.Kind,
                                poll.SubjectId!.Value,
                                now
                            ),
                            _ => TokenError(LobbyTokenError.InvalidGrant),
                        };
                    }
                    case LobbyGrantType.RefreshToken:
                    {
                        if (
                            !OpaqueTokens.TryParse(req.RefreshToken, out var prefix, out var lineage)
                            || prefix != OpaqueTokens.RefreshPrefix
                        )
                            return TokenError(LobbyTokenError.InvalidGrant);
                        var issued = await grains.GetGrain<ISessionGrain>(lineage).Refresh(req.RefreshToken!, now);
                        return issued is null
                            ? TokenError(LobbyTokenError.InvalidGrant)
                            : await Respond(grains, issued, now);
                    }
                    case LobbyGrantType.Dev:
                    {
                        // Headless harnesses only (plan §6 item 1). Refused unless the operator opted in.
                        if (!DevLoginEnabled)
                            return TokenError(LobbyTokenError.UnsupportedGrantType);
                        var name = req.DisplayName?.Trim();
                        if (
                            name is null
                            || name.Length < LobbyLimits.DisplayNameMin
                            || name.Length > LobbyLimits.DisplayNameMax
                        )
                            return TokenError("invalid_request", "display_name must be 3-24 characters");
                        var player = await accounts.FindOrCreateForDevGrantAsync(name, http.RequestAborted);
                        return await IssueNewLineage(grains, SubjectKind.Player, player.Id, now);
                    }
                    default:
                        return TokenError(LobbyTokenError.UnsupportedGrantType);
                }
            }
        );

        // Revoke the lineage behind the presented access token (sign-out). Idempotent.
        auth.MapPost(
                "/revoke",
                async (HttpContext http, IGrainFactory grains, AccessTokenCache cache, TimeProvider clock) =>
                {
                    var token = http.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
                    if (!OpaqueTokens.TryParse(token, out _, out var lineage))
                        return Results.BadRequest();
                    await grains.GetGrain<ISessionGrain>(lineage).Revoke(clock.GetUtcNow());
                    cache.Evict(token);
                    return Results.NoContent();
                }
            )
            .RequireAuthorization(LobbyBearer.AnyPolicy);
    }

    public static bool DevLoginEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(DevLoginEnvVar), "true", StringComparison.OrdinalIgnoreCase);

    // Cookie counterpart of grant_type=dev, same gate: signs the browser in as a display name so
    // the web pages (/device, /me) can be driven without a passkey or provider. Dev boxes only.
    public static void MapDevWebLogin(this WebApplication app)
    {
        app.MapGet(
            "/login/dev",
            async (
                string? displayName,
                string? returnUrl,
                AccountService accounts,
                UserManager<LobbyUser> users,
                SignInManager<LobbyUser> signIn,
                IGrainFactory grains,
                TimeProvider clock,
                HttpContext http
            ) =>
            {
                if (!DevLoginEnabled)
                    return Results.NotFound();
                var name = displayName?.Trim();
                if (name is null || name.Length < LobbyLimits.DisplayNameMin || name.Length > LobbyLimits.DisplayNameMax)
                    return Results.BadRequest("displayName must be 3-24 characters");
                var player = await accounts.FindOrCreateForDevGrantAsync(name, http.RequestAborted);
                var user = await users.FindByIdAsync(player.Id.ToString());
                if (user is null)
                    return Results.NotFound();
                if (await LobbyBans.InForce(grains, player.Id, clock.GetUtcNow()) is { } devBan)
                    return Results.LocalRedirect("/login?error=" + Uri.EscapeDataString(LobbyBans.SignInMessage(devBan)));
                await accounts.ApplyAdminPolicyAsync(user, null, null, http.RequestAborted); // role before the cookie
                await signIn.SignInAsync(user, isPersistent: true);
                return Results.LocalRedirect(
                    string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') ? "/me" : returnUrl
                );
            }
        );
    }

    static async Task<IResult> IssueNewLineage(IGrainFactory grains, SubjectKind kind, Guid subjectId, DateTimeOffset now)
    {
        var issued = await grains.GetGrain<ISessionGrain>(Guid.CreateVersion7()).Create(kind, subjectId, now);
        return await Respond(grains, issued, now);
    }

    /// <summary>The one place a ban is put into words for a peer or a person.</summary>
    internal static string BanMessage(BanRecord ban)
    {
        var window = ban.Until is { } until ? $"until {until:yyyy-MM-dd HH:mm} UTC" : "permanently";
        var reason = string.IsNullOrWhiteSpace(ban.Reason) ? "No reason was recorded." : ban.Reason;
        return $"Banned {window}. {reason}";
    }

    static async Task<IResult> Respond(IGrainFactory grains, IssuedSession issued, DateTimeOffset now)
    {
        var subject = issued.Subject;
        string displayName;
        if (subject.Kind == SubjectKind.Player)
        {
            var player = grains.GetGrain<IPlayerGrain>(subject.Id);
            var snap = await player.Get();
            if (snap is null)
                return TokenError(LobbyTokenError.InvalidGrant, "player no longer exists");
            // access_denied, not invalid_grant: invalid_grant tells a paired client its credential
            // is dead and sends it back through the device flow, which a banned player cannot
            // complete anyway (they cannot sign in to approve it).
            if (snap.Ban.IsBanned(now))
                return TokenError(LobbyTokenError.AccessDenied, BanMessage(snap.Ban!));
            await player.Touch(now);
            displayName = snap.DisplayName;
        }
        else
        {
            var server = await grains.GetGrain<IGameServerGrain>(subject.Id).Get();
            if (server is null)
                return TokenError(LobbyTokenError.InvalidGrant, "game server no longer exists");
            // A banned game server is NOT refused here: a sim server deletes its credential and
            // re-pairs on any refusal to refresh (server/Net/LobbyAuthSession.cs:127), so this would
            // prompt its operator to approve it all over again rather than stop it. Its ban bites
            // with a 403 at POST /servers, /matches and the join seam instead.
            displayName = server.Name;
        }
        var kind = subject.Kind == SubjectKind.Player ? LobbySubjectKind.Player : LobbySubjectKind.Server;
        return Results.Ok(
            new TokenResponse(
                issued.AccessToken,
                issued.RefreshToken,
                (int)OpaqueTokens.AccessLifetime.TotalSeconds,
                new TokenSubject(kind, subject.Id, displayName)
            )
        );
    }

    static IResult TokenError(string error, string? description = null) =>
        Results.Json(new TokenErrorResponse(error, description), statusCode: StatusCodes.Status400BadRequest);

    // JSON from our own peers; application/x-www-form-urlencoded per the RFCs.
    static async Task<TokenRequest?> ReadTokenRequest(HttpContext http)
    {
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var grant = form["grant_type"].ToString();
            return grant.Length == 0
                ? null
                : new TokenRequest(grant, form["device_code"], form["refresh_token"], form["display_name"]);
        }
        try
        {
            var req = await http.Request.ReadFromJsonAsync<TokenRequest>(http.RequestAborted);
            return string.IsNullOrEmpty(req?.GrantType) ? null : req;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // 32 random bytes base64url = 43 chars; anything else never came from /auth/device and must
    // not activate a grain.
    static bool IsWellFormedDeviceCode(string? code) =>
        code is { Length: 43 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
