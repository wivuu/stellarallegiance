using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orleans;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Auth;

// Bearer scheme for the opaque lobby access tokens (players AND game servers). Validation goes
// to the token's SessionGrain (keyed by the lineage id embedded in the token) and is cached per
// silo for at most 60 s, so a revocation lands within a minute without a grain call per request.
public static class LobbyBearer
{
    public const string Scheme = "LobbyBearer";
    public const string KindClaim = "lobby:kind";

    // Authorization policies: `RequireAuthorization(LobbyBearer.PlayerPolicy)` on a route.
    public const string PlayerPolicy = "lobby-player";
    public const string ServerPolicy = "lobby-server";
    public const string AnyPolicy = "lobby-any";

    public static Guid SubjectId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static bool IsPlayer(ClaimsPrincipal user) => user.FindFirstValue(KindClaim) == LobbySubjectKind.Player;
}

public sealed class AccessTokenCache(IMemoryCache cache, IGrainFactory grains)
{
    static readonly TimeSpan MaxTtl = TimeSpan.FromSeconds(60);

    public async Task<SessionSubject?> Resolve(string accessToken, Guid lineageId, DateTimeOffset now)
    {
        var hash = OpaqueTokens.Hash(accessToken);
        if (cache.TryGetValue(hash, out SessionSubject? cached) && cached is not null && cached.AccessExpiresAt > now)
            return cached;
        var subject = await grains.GetGrain<ISessionGrain>(lineageId).ValidateAccess(hash, now);
        if (subject is null)
            return null;
        var ttl = subject.AccessExpiresAt - now;
        cache.Set(hash, subject, ttl < MaxTtl ? ttl : MaxTtl);
        return subject;
    }

    public void Evict(string accessToken) => cache.Remove(OpaqueTokens.Hash(accessToken));
}

sealed class LobbyBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccessTokenCache tokens,
    TimeProvider clock
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (!OpaqueTokens.TryParse(token, out var prefix, out var lineage) || prefix != OpaqueTokens.AccessPrefix)
            return AuthenticateResult.Fail("malformed access token");

        var subject = await tokens.Resolve(token, lineage, clock.GetUtcNow());
        if (subject is null)
            return AuthenticateResult.Fail("invalid or expired access token");

        var kind = subject.Kind == SubjectKind.Player ? LobbySubjectKind.Player : LobbySubjectKind.Server;
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject.Id.ToString()), new Claim(LobbyBearer.KindClaim, kind)],
            Scheme.Name
        );
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
