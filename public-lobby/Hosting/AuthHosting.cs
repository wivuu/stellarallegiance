using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PublicLobby.Auth;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Hosting;

// Bearer-token auth for the API surface (plan §3.1): the opaque lobby access tokens of players
// and game servers. The web pages use Identity's cookie scheme (WebAuth.cs); both schemes coexist
// and a route picks one through its policy.
static class AuthHosting
{
    public static IHostApplicationBuilder AddLobbyBearerAuth(this IHostApplicationBuilder builder)
    {
        builder.Services.AddMemoryCache();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AccessTokenCache>();
        builder
            .Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, LobbyBearerHandler>(LobbyBearer.Scheme, displayName: null, _ => { });
        builder
            .Services.AddAuthorizationBuilder()
            .AddPolicy(
                LobbyBearer.PlayerPolicy,
                p =>
                    p.AddAuthenticationSchemes(LobbyBearer.Scheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim(LobbyBearer.KindClaim, LobbySubjectKind.Player)
            )
            .AddPolicy(
                LobbyBearer.ServerPolicy,
                p =>
                    p.AddAuthenticationSchemes(LobbyBearer.Scheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim(LobbyBearer.KindClaim, LobbySubjectKind.Server)
            )
            .AddPolicy(
                LobbyBearer.AnyPolicy,
                p => p.AddAuthenticationSchemes(LobbyBearer.Scheme).RequireAuthenticatedUser()
            );
        return builder;
    }
}
