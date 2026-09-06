using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SimServer.Net;

// JoinTokenVerifier (WP1.3): the game server's OFFLINE check of a lobby-issued ES256 join token
// (plan §3.2). Mints tokens locally with a throwaway P-256 key and a hand-built JWKS, so no lobby
// is needed; the lobby side (SigningKeyGrain / JoinTokenIssuer) is covered by
// tests/PublicLobbyTest, which validates real lobby tokens against the lobby's /.well-known/jwks.json.
static class JoinTokenVerifierTests
{
    const string Issuer = "https://lobby.example";
    const string Listing = "listing-abc";

    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "PASS: " : "FAIL: ") + what);
            if (!cond)
                failures++;
        }

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jwks = Jwks(("k1", signer));
        int fetches = 0;
        var clock = new FakeClock(DateTimeOffset.Parse("2026-09-06T12:00:00Z"));
        var verifier = new JoinTokenVerifier(
            Issuer,
            _ =>
            {
                fetches++;
                return Task.FromResult(jwks);
            },
            clock
        );
        await verifier.RefreshAsync(CancellationToken.None);
        Check(fetches == 1, "RefreshAsync fetches the JWKS once");

        var player = Guid.NewGuid();
        var good = Mint(signer, "k1", player, "Vex", Listing, clock.Now, TimeSpan.FromSeconds(60));
        var r = await verifier.VerifyAsync(good, Listing, CancellationToken.None);
        Check(
            r.IsValid && r.Identity!.PlayerId == player && r.Identity.DisplayName == "Vex",
            "valid token yields (sub, name)"
        );
        Check(
            (await verifier.VerifyAsync(good, Listing, CancellationToken.None)).Failure == JoinTokenFailure.Replayed,
            "same jti again is Replayed"
        );

        var wrongAud = Mint(signer, "k1", player, "Vex", "other-listing", clock.Now, TimeSpan.FromSeconds(60));
        Check(
            (await verifier.VerifyAsync(wrongAud, Listing, CancellationToken.None)).Failure
                == JoinTokenFailure.WrongAudience,
            "wrong aud is WrongAudience"
        );

        var expired = Mint(
            signer,
            "k1",
            player,
            "Vex",
            Listing,
            clock.Now - TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(60)
        );
        Check(
            (await verifier.VerifyAsync(expired, Listing, CancellationToken.None)).Failure == JoinTokenFailure.Expired,
            "expired token is Expired"
        );

        var skewOk = Mint(
            signer,
            "k1",
            player,
            "Vex",
            Listing,
            clock.Now - TimeSpan.FromSeconds(63),
            TimeSpan.FromSeconds(60)
        );
        Check(
            (await verifier.VerifyAsync(skewOk, Listing, CancellationToken.None)).IsValid,
            "3 s past expiry is inside the 5 s skew"
        );

        var future = Mint(signer, "k1", player, "Vex", Listing, clock.Now, TimeSpan.FromSeconds(60));
        clock.Now += TimeSpan.FromSeconds(70);
        Check(
            (await verifier.VerifyAsync(future, Listing, CancellationToken.None)).Failure == JoinTokenFailure.Expired,
            "token expires by the verifier's own clock"
        );
        clock.Now -= TimeSpan.FromSeconds(70);

        var forged = Mint(rogue, "k1", player, "Vex", Listing, clock.Now, TimeSpan.FromSeconds(60));
        Check(
            (await verifier.VerifyAsync(forged, Listing, CancellationToken.None)).Failure == JoinTokenFailure.BadSignature,
            "signature by another key under a known kid is BadSignature"
        );

        var badIssuer = Mint(
            signer,
            "k1",
            player,
            "Vex",
            Listing,
            clock.Now,
            TimeSpan.FromSeconds(60),
            issuer: "https://evil.example"
        );
        Check(
            (await verifier.VerifyAsync(badIssuer, Listing, CancellationToken.None)).Failure == JoinTokenFailure.WrongIssuer,
            "wrong iss is WrongIssuer"
        );

        var unsigned = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Listing,
                Expires = (clock.Now + TimeSpan.FromSeconds(60)).UtcDateTime,
                Subject = new ClaimsIdentity([
                    new Claim("sub", player.ToString()),
                    new Claim("name", "Vex"),
                    new Claim("jti", "u1"),
                ]),
            }
        );
        var unsignedResult = await verifier.VerifyAsync(unsigned, Listing, CancellationToken.None);
        Check(!unsignedResult.IsValid, "alg=none token is rejected (" + unsignedResult.Failure + ")");

        Check(
            (await verifier.VerifyAsync("not.a.jwt", Listing, CancellationToken.None)).Failure == JoinTokenFailure.Malformed,
            "garbage is Malformed"
        );
        Check(
            (await verifier.VerifyAsync("", Listing, CancellationToken.None)).Failure == JoinTokenFailure.Malformed,
            "empty is Malformed"
        );

        // Rotation: a token under a NEW kid triggers one refresh; a second unknown kid inside the
        // 30 s window does not fetch again.
        using var signer2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rotated = Mint(signer2, "k2", player, "Vex", Listing, clock.Now, TimeSpan.FromSeconds(60));
        var fetchesBefore = fetches;
        Check(
            (await verifier.VerifyAsync(rotated, Listing, CancellationToken.None)).Failure == JoinTokenFailure.UnknownKey,
            "unknown kid while the JWKS is fresh: UnknownKey"
        );
        Check(fetches == fetchesBefore, "…no refresh inside the 30 s window");
        clock.Now += TimeSpan.FromSeconds(31);
        jwks = Jwks(("k1", signer), ("k2", signer2));
        var rotated2 = Mint(signer2, "k2", player, "Vex", Listing, clock.Now, TimeSpan.FromSeconds(60));
        Check(
            (await verifier.VerifyAsync(rotated2, Listing, CancellationToken.None)).IsValid,
            "unknown kid after the window refreshes the JWKS and verifies"
        );
        Check(fetches == fetchesBefore + 1, "…exactly one refresh");

        return failures;
    }

    static string Mint(
        ECDsa key,
        string kid,
        Guid player,
        string name,
        string aud,
        DateTimeOffset iat,
        TimeSpan life,
        string issuer = Issuer
    )
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = aud,
                IssuedAt = iat.UtcDateTime,
                NotBefore = iat.UtcDateTime,
                Expires = (iat + life).UtcDateTime,
                Subject = new ClaimsIdentity([
                    new Claim("sub", player.ToString()),
                    new Claim("name", name),
                    new Claim("jti", Guid.NewGuid().ToString("N")),
                ]),
                SigningCredentials = new SigningCredentials(
                    new ECDsaSecurityKey(key) { KeyId = kid },
                    SecurityAlgorithms.EcdsaSha256
                )
                {
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
                },
            }
        );
    }

    static string Jwks(params (string Kid, ECDsa Key)[] keys)
    {
        var list = keys.Select(k =>
        {
            var p = k.Key.ExportParameters(false);
            return new
            {
                kty = "EC",
                crv = "P-256",
                x = Base64Url.EncodeToString(p.Q.X!),
                y = Base64Url.EncodeToString(p.Q.Y!),
                kid = k.Kid,
                use = "sig",
                alg = "ES256",
            };
        });
        return JsonSerializer.Serialize(new { keys = list.ToArray() });
    }

    sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
