using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Orleans;
using PublicLobby;
using PublicLobby.Auth;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

// WP1.3 (lobby side): SigningKeyGrain generates an ES256 key on first use and publishes it at
// /.well-known/jwks.json; JoinTokenIssuer mints tokens that validate against that JWKS with the
// same library the game server uses; PlayerGrain.RecordJoinToken writes the issuance ledger and
// presence. The game server's verifier itself is covered in tests/LobbyTest.
static partial class Suite
{
    static async Task RunJoinTokenTestsAsync()
    {
        Console.WriteLine("[join-token] signing keys, JWKS, issuance ledger");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping join-token section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();

        // ---- JWKS ----
        var jwksResp = await http.GetAsync("/.well-known/jwks.json");
        Eq(HttpStatusCode.OK, jwksResp.StatusCode, "GET /.well-known/jwks.json");
        Eq("application/json", jwksResp.Content.Headers.ContentType?.MediaType, "…is JSON");
        Check(jwksResp.Headers.CacheControl?.MaxAge == TimeSpan.FromSeconds(60), "…cacheable 60 s");
        var jwksJson = await jwksResp.Content.ReadAsStringAsync();
        var jwks = new JsonWebKeySet(jwksJson);
        Eq(1, jwks.Keys.Count, "one signing key generated on first use");
        var key = jwks.Keys[0];
        Eq("EC", key.Kty, "key type EC");
        Eq("P-256", key.Crv, "curve P-256");
        Eq("ES256", key.Alg, "alg ES256");
        Check(!string.IsNullOrEmpty(key.Kid), "key has a kid");
        Check(string.IsNullOrEmpty(key.D), "JWKS never carries the private scalar");
        Eq(jwksJson, await http.GetStringAsync("/.well-known/jwks.json"), "JWKS is stable across reads");

        // ---- mint + validate against the published key ----
        var vex = (await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Juno"))).Token!;
        var issuer = services.GetRequiredService<JoinTokenIssuer>();
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");
        var token = await issuer.Mint(vex.Subject.Id, "Juno", "listing-1", jti, now);
        var handler = new JsonWebTokenHandler();
        var jwt = handler.ReadJsonWebToken(token);
        Eq(key.Kid, jwt.Kid, "token header kid matches the JWKS");
        Eq("ES256", jwt.Alg, "token alg ES256");
        Eq("listing-1", jwt.Audiences.Single(), "aud = listing id");
        Eq(vex.Subject.Id.ToString(), jwt.Subject, "sub = player id");
        Eq("Juno", jwt.GetClaim("name").Value, "name claim");
        Eq(jti, jwt.Id, "jti claim");
        Check((jwt.ValidTo - jwt.IssuedAt).TotalSeconds is >= 59 and <= 61, "60 s lifetime");
        var validation = await handler.ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = jwt.Issuer,
                ValidAudience = "listing-1",
                IssuerSigningKeys = jwks.GetSigningKeys(),
                ValidAlgorithms = ["ES256"],
            }
        );
        Check(validation.IsValid, "token validates against the JWKS (" + validation.Exception?.GetType().Name + ")");
        Check(jwt.Issuer.StartsWith("http"), "iss is the lobby public URL (" + jwt.Issuer + ")");
        var tampered = token[..^4] + "AAAA";
        Check(
            !(
                await handler.ValidateTokenAsync(
                    tampered,
                    new TokenValidationParameters
                    {
                        ValidIssuer = jwt.Issuer,
                        ValidAudience = "listing-1",
                        IssuerSigningKeys = jwks.GetSigningKeys(),
                    }
                )
            ).IsValid,
            "tampered signature fails"
        );

        // ---- issuance ledger + presence ----
        var gsId = Guid.CreateVersion7();
        await grains.GetGrain<IGameServerGrain>(gsId).Create(vex.Subject.Id, "Juno's Box", now);
        await grains
            .GetGrain<IPlayerGrain>(vex.Subject.Id)
            .RecordJoinToken(jti, gsId, "listing-1", now, now + JoinTokenIssuer.Lifetime);
        var snap = await grains.GetGrain<IPlayerGrain>(vex.Subject.Id).Get();
        Eq("listing-1", snap?.CurrentListingId, "presence moves to the listing");
        var cs = await PostgresFixture.GetConnectionStringAsync();
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select player_id, game_server_id, listing_id from join_tokens_issued where jti = @jti",
                conn
            );
            cmd.Parameters.AddWithValue("jti", jti);
            await using var reader = await cmd.ExecuteReaderAsync();
            Check(await reader.ReadAsync(), "join_tokens_issued row written");
            Eq(vex.Subject.Id, reader.GetGuid(0), "…for the player");
            Eq(gsId, reader.GetGuid(1), "…and the game server");
            Eq("listing-1", reader.GetString(2), "…and the listing");
        }

        // ---- POST /servers/{listingId}/join: Verified listings only, player bearer ----
        var juno = vex;
        var (serverBearer, gameServerId) = await DevServerTokenAsync(http, grains, "Juno Ops", "Juno's Verified Box");
        var verified = await PostServerAsync(
            http,
            new RegisterRequest(Name: "Juno's Verified Box", Port: 19093, PublicEndpoint: null),
            serverBearer
        );
        Eq(HttpStatusCode.Created, verified.Status, "verified listing registered");
        var listingId = verified.Body!.Server.SessionId;
        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", "true");
        var unverified = await PostServerAsync(
            http,
            new RegisterRequest(Name: "Juno's Anon Box", Port: 19094, PublicEndpoint: null),
            null
        );
        Environment.SetEnvironmentVariable("ALLOW_UNVERIFIED_SERVERS", null);
        Eq(HttpStatusCode.Created, unverified.Status, "unverified listing registered (flag on)");

        Eq(
            HttpStatusCode.Unauthorized,
            (await http.PostAsync($"/servers/{listingId}/join", null)).StatusCode,
            "join without a bearer: 401"
        );
        Eq(HttpStatusCode.Forbidden, await JoinStatusAsync(http, listingId, serverBearer), "join with a SERVER bearer: 403");
        Eq(
            HttpStatusCode.NotFound,
            await JoinStatusAsync(http, "no-such-listing", juno.AccessToken),
            "join unknown listing: 404"
        );
        Eq(
            HttpStatusCode.NotFound,
            await JoinStatusAsync(http, unverified.Body!.Server.SessionId, juno.AccessToken),
            "join an Unverified listing: 404"
        );

        using (var req = new HttpRequestMessage(HttpMethod.Post, $"/servers/{listingId}/join"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", juno.AccessToken);
            var r = await http.SendAsync(req);
            Eq(HttpStatusCode.OK, r.StatusCode, "join a Verified listing: 200");
            var body = (await r.Content.ReadFromJsonAsync<JoinTokenResponse>())!;
            Eq(60, body.ExpiresIn, "join token expires_in 60");
            var joinJwt = handler.ReadJsonWebToken(body.JoinToken);
            Eq(listingId, joinJwt.Audiences.Single(), "join token aud = the listing id");
            Eq(juno.Subject.Id.ToString(), joinJwt.Subject, "join token sub = the player");
            Eq("Juno", joinJwt.GetClaim("name").Value, "join token name = display name");
            var v = await handler.ValidateTokenAsync(
                body.JoinToken,
                new TokenValidationParameters
                {
                    ValidIssuer = joinJwt.Issuer,
                    ValidAudience = listingId,
                    IssuerSigningKeys = jwks.GetSigningKeys(),
                    ValidAlgorithms = ["ES256"],
                }
            );
            Check(v.IsValid, "join token validates against the JWKS");
            var presence = await grains.GetGrain<IPlayerGrain>(juno.Subject.Id).Get();
            Eq(listingId, presence?.CurrentListingId, "presence moved to the joined listing");
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "select count(*) from join_tokens_issued where player_id = @p and game_server_id = @g and listing_id = @l",
                conn
            );
            cmd.Parameters.AddWithValue("p", juno.Subject.Id);
            cmd.Parameters.AddWithValue("g", gameServerId);
            cmd.Parameters.AddWithValue("l", listingId);
            Eq(1L, (long)(await cmd.ExecuteScalarAsync())!, "issuance recorded against the game server + listing");
        }
        await DeleteServerAsync(http, listingId, verified.Body.Secret);
        await DeleteServerAsync(http, unverified.Body.Server.SessionId, unverified.Body.Secret);

        // ---- rotation keeps the old key published for the grace window ----
        var newKid = await grains.GetGrain<ISigningKeyGrain>(0).Rotate(now);
        var after = new JsonWebKeySet(await http.GetStringAsync("/.well-known/jwks.json"));
        Eq(2, after.Keys.Count, "rotated: both keys published during the grace window");
        Check(after.Keys.Any(k => k.Kid == newKid), "…including the new kid");
        var later = await grains.GetGrain<ISigningKeyGrain>(0).GetJwks(now.AddMinutes(11));
        Eq(1, new JsonWebKeySet(later).Keys.Count, "retired key drops out after the grace window");
    }

    static async Task<HttpStatusCode> JoinStatusAsync(HttpClient http, string listingId, string bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/servers/{listingId}/join");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return (await http.SendAsync(req)).StatusCode;
    }
}
