using System.Security.Claims;
using System.Text.Json.Serialization;
using AspNet.Security.OAuth.GitHub;
using AspNet.Security.OpenId.Steam;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Orleans;
using PublicLobby.Accounts;
using PublicLobby.Data;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Hosting;

/// <summary>One configured external login provider, for <see cref="AuthProviders"/> and the /login page.</summary>
public sealed record AuthProviderInfo(string Scheme, string DisplayName);

/// <summary>
/// The provider list <c>AddLobbyWeb</c> actually registered (env-gated — plan §1.1, §3.5). Singleton so
/// Razor Pages can read it without re-checking environment variables on every request.
/// </summary>
public sealed class AuthProviders(IReadOnlyList<AuthProviderInfo> configured)
{
    public IReadOnlyList<AuthProviderInfo> Configured { get; } = configured;
}

/// <summary>
/// The public URL the lobby is reachable at (<c>LOBBY_PUBLIC_URL</c>, default
/// <c>http://localhost:&lt;port&gt;</c> — plan §3.5). Used as the passkey relying-party domain when set,
/// and reused by later work packages (device-code <c>verification_uri</c>, join-token <c>iss</c>).
/// </summary>
public sealed class LobbyPublicUrl
{
    public string Value { get; }
    public Uri Uri { get; }
    public string Host => Uri.Host;

    public LobbyPublicUrl(string value)
    {
        Value = value.TrimEnd('/');
        Uri = new Uri(Value, UriKind.Absolute);
    }
}

// Web shell + auth providers (plan .PLAN/LobbyRankingService.md §1.1, §1.4, §1.5, §3.5, WP0.3): Razor
// Pages, cookie auth (ASP.NET Core Identity owns the `lobby` cookie), external logins registered only
// when their env vars are present, and the four passkey-ceremony JSON endpoints (WebAuthn — .NET 10
// Identity built-in, no third-party library). This is the ONLY file that touches `PublicLobby.cs`
// beyond the two documented one-line insertions (`builder.AddLobbyWeb();` / `app.UseLobbyWeb();`), so it
// stays in the `PublicLobby.Hosting` namespace alongside Persistence.cs — no new `using` needed there.
static class WebHosting
{
    public static IHostApplicationBuilder AddLobbyWeb(this IHostApplicationBuilder b)
    {
        b.Services.AddRazorPages();
        b.Services.AddHttpContextAccessor();

        // Duplicated from PublicLobby.cs's own PORT/SHARE_PORT/8091 fallback: the concurrency notice for
        // this work package restricts PublicLobby.cs edits to two fixed one-liners with no parameters,
        // so AddLobbyWeb can't receive the already-resolved `port` local — it re-derives the same default.
        var publicUrlRaw = Environment.GetEnvironmentVariable("LOBBY_PUBLIC_URL");
        var publicUrl = new LobbyPublicUrl(
            string.IsNullOrWhiteSpace(publicUrlRaw) ? $"http://localhost:{ResolveDefaultPort()}" : publicUrlRaw
        );
        b.Services.AddSingleton(publicUrl);

        b.Services.Configure<IdentityPasskeyOptions>(o =>
        {
            // Left null when LOBBY_PUBLIC_URL isn't set: IdentityPasskeyOptions.ServerDomain falls back
            // to the request host per-call (PasskeyHandler.GetServerDomain), which is exactly the
            // "else default = request host" behaviour WP0.3 asks for.
            if (!string.IsNullOrWhiteSpace(publicUrlRaw))
                o.ServerDomain = publicUrl.Host;
        });

        var authBuilder = b.Services.AddAuthentication(IdentityConstants.ApplicationScheme);
        authBuilder.AddIdentityCookies(cookies =>
        {
            cookies.ApplicationCookie!.Configure(o =>
            {
                o.Cookie.Name = "lobby";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath = "/login";
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                // A signed-in player who fails a policy (e.g. /admin without the role) gets a plain
                // 403, not a redirect to a non-existent access-denied page.
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        });

        var providers = new List<AuthProviderInfo>();

        var googleId = Environment.GetEnvironmentVariable("AUTH_GOOGLE_CLIENT_ID");
        var googleSecret = Environment.GetEnvironmentVariable("AUTH_GOOGLE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret))
        {
            authBuilder.AddGoogle(o =>
            {
                o.ClientId = googleId;
                o.ClientSecret = googleSecret;
                o.SignInScheme = IdentityConstants.ExternalScheme;
            });
            providers.Add(new AuthProviderInfo(GoogleDefaults.AuthenticationScheme, "Google"));
        }

        var githubId = Environment.GetEnvironmentVariable("AUTH_GITHUB_CLIENT_ID");
        var githubSecret = Environment.GetEnvironmentVariable("AUTH_GITHUB_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(githubId) && !string.IsNullOrWhiteSpace(githubSecret))
        {
            authBuilder.AddGitHub(o =>
            {
                o.ClientId = githubId;
                o.ClientSecret = githubSecret;
                o.SignInScheme = IdentityConstants.ExternalScheme;
            });
            providers.Add(new AuthProviderInfo(GitHubAuthenticationDefaults.AuthenticationScheme, "GitHub"));
        }

        // Steam OpenID 2.0 itself needs no key (ADR-0001); the Web API key only fetches the persona name
        // for GetPlayerSummaries, which SteamAuthenticationHandler adds as the ClaimTypes.Name claim.
        var steamKey = Environment.GetEnvironmentVariable("AUTH_STEAM_API_KEY");
        if (!string.IsNullOrWhiteSpace(steamKey))
        {
            authBuilder.AddSteam(o =>
            {
                o.ApplicationKey = steamKey;
                o.SignInScheme = IdentityConstants.ExternalScheme;
            });
            providers.Add(new AuthProviderInfo(SteamAuthenticationDefaults.AuthenticationScheme, "Steam"));
        }

        b.Services.AddSingleton(new AuthProviders(providers));

        b.Services.AddAuthorization(o => o.AddPolicy(LobbyRoles.Admin, p => p.RequireRole(LobbyRoles.Admin)));

        b.Services.AddScoped<AccountService>();

        return b;
    }

    public static WebApplication UseLobbyWeb(this WebApplication app)
    {
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();

        MapPasskeyEndpoints(app);

        app.MapRazorPages();
        return app;
    }

    static int ResolveDefaultPort() =>
        int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var pe) ? pe
        : int.TryParse(Environment.GetEnvironmentVariable("SHARE_PORT"), out var p) ? p
        : 8091;

    // ---- Passkey ceremony endpoints (WebAuthn via .NET 10 Identity's SignInManager/UserManager) -------
    //
    // Two-step, state-in-a-cookie flow: MakePasskeyCreationOptionsAsync/MakePasskeyRequestOptionsAsync
    // stash attestation/assertion state in the (already-registered) TwoFactorUserIdScheme cookie
    // themselves — see SignInManager.StorePasskeyAuthenticationInfoAsync — so these endpoints never see
    // or forward that state; the browser only round-trips the WebAuthn options/credential JSON. Mirrors
    // the pattern in dotnet/aspnetcore's own src/Identity/samples/IdentitySample.PasskeyUI.
    static void MapPasskeyEndpoints(WebApplication app)
    {
        // Anonymous: sign-up (fresh account) OR — when called by an authenticated caller — "add a
        // passkey" to the current account. Either way the client feeds the returned JSON straight into
        // navigator.credentials.create().
        app.MapPost(
            "/login/passkey/creation-options",
            async (
                HttpContext http,
                SignInManager<LobbyUser> signInManager,
                UserManager<LobbyUser> userManager,
                LobbyDbContext db,
                PasskeyCreationOptionsRequest? body
            ) =>
            {
                string userId;
                string name;
                if (http.User.Identity?.IsAuthenticated == true)
                {
                    var current = await userManager.GetUserAsync(http.User);
                    if (current is null)
                        return Results.Unauthorized();
                    var player = await db.Players.FindAsync([current.Id], http.RequestAborted);
                    userId = current.Id.ToString();
                    name = player?.DisplayName ?? current.UserName ?? "Pilot";
                }
                else
                {
                    var displayName = body?.DisplayName?.Trim();
                    if (
                        displayName is null
                        || displayName.Length < LobbyLimits.DisplayNameMin
                        || displayName.Length > LobbyLimits.DisplayNameMax
                    )
                        return Results.BadRequest(
                            new
                            {
                                error = $"displayName must be {LobbyLimits.DisplayNameMin}-{LobbyLimits.DisplayNameMax} characters",
                            }
                        );
                    // Fresh, not-yet-persisted id: only becomes a real account if /register succeeds
                    // (mirrors IdentitySample.PasskeyUI's `new PocoUser()` id-mint-before-create pattern).
                    userId = Guid.NewGuid().ToString();
                    name = displayName;
                }

                var userEntity = new PasskeyUserEntity
                {
                    Id = userId,
                    Name = name,
                    DisplayName = name,
                };
                var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(userEntity);
                return Results.Content(optionsJson, "application/json");
            }
        );

        app.MapPost(
            "/login/passkey/register",
            async (
                HttpContext http,
                SignInManager<LobbyUser> signInManager,
                UserManager<LobbyUser> userManager,
                AccountService accounts,
                PasskeyRegisterRequest body
            ) =>
            {
                if (string.IsNullOrWhiteSpace(body.Credential))
                    return Results.BadRequest(new { error = "missing credential" });

                var attestation = await signInManager.PerformPasskeyAttestationAsync(body.Credential);
                if (!attestation.Succeeded || attestation.UserEntity is null || attestation.Passkey is null)
                    return Results.BadRequest(new { error = attestation.Failure?.Message ?? "passkey registration failed" });

                var userEntity = attestation.UserEntity;
                var existing = await userManager.FindByIdAsync(userEntity.Id);
                if (existing is not null)
                {
                    // "Add a passkey" for an already-signed-in account (creation-options above used the
                    // caller's real id, not a fresh one) — only that same caller may attach it.
                    var current =
                        http.User.Identity?.IsAuthenticated == true ? await userManager.GetUserAsync(http.User) : null;
                    if (current is null || current.Id != existing.Id)
                        return Results.Forbid();

                    var addResult = await userManager.AddOrUpdatePasskeyAsync(existing, attestation.Passkey);
                    return addResult.Succeeded
                        ? Results.Ok(new { ok = true })
                        : Results.BadRequest(new { error = "could not save the passkey" });
                }

                if (string.IsNullOrWhiteSpace(body.DisplayName))
                    return Results.BadRequest(new { error = "displayName is required" });

                var result = await accounts.CreateWithPasskeyAsync(
                    body.DisplayName.Trim(),
                    userEntity,
                    attestation.Passkey,
                    http.RequestAborted
                );
                if (!result.Succeeded || result.User is null)
                    return Results.BadRequest(new { error = result.Error ?? "could not create account" });

                // Role before the cookie: role claims are baked into the principal at sign-in.
                await accounts.ApplyAdminPolicyAsync(
                    result.User,
                    provider: null,
                    providerPrincipal: null,
                    http.RequestAborted
                );
                await signInManager.SignInAsync(result.User, isPersistent: true);

                return Results.Ok(new { ok = true, redirect = SafeLocalPath(body.ReturnUrl) ?? "/me" });
            }
        );

        app.MapPost(
            "/login/passkey/request-options",
            async (SignInManager<LobbyUser> signInManager) =>
            {
                // user: null => discoverable-credential request (no server-side hint which account).
                var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(null);
                return Results.Content(optionsJson, "application/json");
            }
        );

        app.MapPost(
            "/login/passkey/assert",
            async (
                HttpContext http,
                SignInManager<LobbyUser> signInManager,
                UserManager<LobbyUser> userManager,
                AccountService accounts,
                IGrainFactory grains,
                TimeProvider clock,
                PasskeyAssertRequest body
            ) =>
            {
                if (string.IsNullOrWhiteSpace(body.Credential))
                    return Results.BadRequest(new { error = "missing credential" });

                var assertion = await signInManager.PerformPasskeyAssertionAsync(body.Credential);
                if (!assertion.Succeeded || assertion.User is null || assertion.Passkey is null)
                    return Results.BadRequest(new { error = assertion.Failure?.Message ?? "sign-in failed" });

                // No cookie for a banned player, same as the external-login callback.
                if (await LobbyBans.InForce(grains, assertion.User.Id, clock.GetUtcNow()) is { } ban)
                    return Results.Json(new { error = LobbyBans.SignInMessage(ban) }, statusCode: 403);

                // Keep sign-count/authenticator-data current, same as PasskeySignInAsync's internal
                // PasskeySignInCoreAsync — we don't call that convenience method directly because it
                // always signs in non-persistent, and we want the 30-day sliding cookie (plan §1.1).
                await userManager.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey);
                // Role before the cookie: role claims are baked into the principal at sign-in.
                await accounts.ApplyAdminPolicyAsync(
                    assertion.User,
                    provider: null,
                    providerPrincipal: null,
                    http.RequestAborted
                );
                await signInManager.SignInAsync(assertion.User, isPersistent: true);

                return Results.Ok(new { ok = true, redirect = SafeLocalPath(body.ReturnUrl) ?? "/me" });
            }
        );
    }

    // Local-only redirect targets (no protocol-relative "//evil" opens either) — mirrors
    // Microsoft.AspNetCore.Mvc.IUrlHelper.IsLocalUrl for these non-MVC minimal API responses.
    internal static string? SafeLocalPath(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : null;
}

sealed record PasskeyCreationOptionsRequest([property: JsonPropertyName("displayName")] string? DisplayName);

sealed record PasskeyRegisterRequest(
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("credential")] string Credential,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl
);

sealed record PasskeyAssertRequest(
    [property: JsonPropertyName("credential")] string Credential,
    [property: JsonPropertyName("returnUrl")] string? ReturnUrl
);
