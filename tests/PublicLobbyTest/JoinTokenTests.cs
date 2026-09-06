using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Orleans;
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

        // ---- rotation keeps the old key published for the grace window ----
        var newKid = await grains.GetGrain<ISigningKeyGrain>(0).Rotate(now);
        var after = new JsonWebKeySet(await http.GetStringAsync("/.well-known/jwks.json"));
        Eq(2, after.Keys.Count, "rotated: both keys published during the grace window");
        Check(after.Keys.Any(k => k.Kid == newKid), "…including the new kid");
        var later = await grains.GetGrain<ISigningKeyGrain>(0).GetJwks(now.AddMinutes(11));
        Eq(1, new JsonWebKeySet(later).Keys.Count, "retired key drops out after the grace window");
    }
}
