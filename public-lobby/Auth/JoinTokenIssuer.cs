using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orleans;
using PublicLobby.Grains;
using PublicLobby.Hosting;

namespace PublicLobby.Auth;

// Mints join tokens (plan §3.2): ES256 JWTs, `iss` = lobby public URL, `sub` = player id, `aud` =
// listing id, `name` = display name at issue time, `jti` single-use, 60 s. The signing key comes
// from SigningKeyGrain once per process (and again after a rotation, when the kid changes).
public sealed class JoinTokenIssuer(IGrainFactory grains, LobbyPublicUrl publicUrl) : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int LifetimeSeconds = 60;

    readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    readonly SemaphoreSlim _gate = new(1, 1);
    ECDsa? _ecdsa;
    ECDsaSecurityKey? _key;
    string? _kid;

    public async Task<string> Mint(Guid playerId, string displayName, string listingId, string jti, DateTimeOffset now)
    {
        var (key, kid) = await SigningKey(now);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = publicUrl.Value,
            Audience = listingId,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + Lifetime).UtcDateTime,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, playerId.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, displayName),
                new Claim(JwtRegisteredClaimNames.Jti, jti),
            ]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            },
            TokenType = "JWT",
        };
        return _handler.CreateToken(descriptor);
    }

    async Task<(ECDsaSecurityKey Key, string Kid)> SigningKey(DateTimeOffset now)
    {
        if (_key is not null && _kid is not null)
            return (_key, _kid);
        await _gate.WaitAsync();
        try
        {
            if (_key is null || _kid is null)
            {
                var material = await grains.GetGrain<ISigningKeyGrain>(0).GetActiveKey(now);
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(material.PrivatePem);
                _ecdsa = ecdsa;
                _key = new ECDsaSecurityKey(ecdsa) { KeyId = material.Kid };
                _kid = material.Kid;
            }
            return (_key, _kid);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _ecdsa?.Dispose();
}
