using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TestKit;

// Lobby-style ES256 join tokens and the JWKS that verifies them, minted locally with a throwaway P-256
// key - so a suite can stand in for the public lobby without one. Shared by LobbyTest (the verifier
// itself) and ServerUpdateTest (a Hello parked inside the verifier while the server starts draining).
public static class JoinTokenKit
{
    public static string Mint(
        ECDsa key,
        string kid,
        Guid player,
        string name,
        string aud,
        DateTimeOffset iat,
        TimeSpan life,
        string issuer
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

    public static string Jwks(params (string Kid, ECDsa Key)[] keys)
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
}
