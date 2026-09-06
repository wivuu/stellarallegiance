using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

namespace PublicLobby.Grains;

// The lobby's join-token signing keys (plan §1.1/§3.2): ES256 (P-256) keypairs stored ONLY in
// `signing_keys` (private half as PKCS#8 PEM, public half as a JWK), generated on first use and
// published at /.well-known/jwks.json. Singleton grain (key 0) = single writer of that table.
// Retired keys stay in the JWKS for a grace window so already-issued (60 s) tokens still verify.
public interface ISigningKeyGrain : IGrainWithIntegerKey
{
    /// <summary>The key new join tokens are signed with (generated on first call).</summary>
    [ReadOnly]
    Task<SigningKeyMaterial> GetActiveKey(DateTimeOffset now);

    /// <summary>RFC 7517 JWK Set JSON of every key that may still have live tokens.</summary>
    [ReadOnly]
    Task<string> GetJwks(DateTimeOffset now);

    /// <summary>Retire the active key and generate a fresh one (admin/ops; not exposed yet).</summary>
    Task<string> Rotate(DateTimeOffset now);
}

// Private material never leaves the process: the grain and the JoinTokenIssuer that caches this
// share the single silo (plan §1.4). Orleans serializes it in-memory only.
[GenerateSerializer]
public sealed record SigningKeyMaterial([property: Id(0)] string Kid, [property: Id(1)] string PrivatePem);

public sealed class SigningKeyGrain(IDbContextFactory<LobbyDbContext> dbFactory) : Grain, ISigningKeyGrain
{
    // Join tokens live 60 s (plan §3.2); keep retired keys published well past that.
    static readonly TimeSpan RetiredGrace = TimeSpan.FromMinutes(10);

    readonly List<SigningKey> _keys = [];

    SigningKey? Active => _keys.Find(k => k.RetiredAt is null);

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _keys.AddRange(await db.SigningKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync(cancellationToken));
    }

    public async Task<SigningKeyMaterial> GetActiveKey(DateTimeOffset now)
    {
        var key = Active ?? await Generate(now);
        return new SigningKeyMaterial(key.Kid, key.PrivatePem);
    }

    public async Task<string> GetJwks(DateTimeOffset now)
    {
        if (Active is null)
            await Generate(now);
        var live = _keys
            .Where(k => k.RetiredAt is null || k.RetiredAt.Value + RetiredGrace > now)
            .Select(k => JsonSerializer.Deserialize<JsonElement>(k.PublicJwk));
        return JsonSerializer.Serialize(new { keys = live.ToArray() });
    }

    public async Task<string> Rotate(DateTimeOffset now)
    {
        var active = Active;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (active is not null)
        {
            db.SigningKeys.Attach(active);
            active.RetiredAt = now;
            await db.SaveChangesAsync();
        }
        return (await Generate(now)).Kid;
    }

    async Task<SigningKey> Generate(DateTimeOffset now)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = ecdsa.ExportParameters(includePrivateParameters: false);
        var kid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var jwk = new
        {
            kty = "EC",
            crv = "P-256",
            x = Base64UrlEncode(pub.Q.X!),
            y = Base64UrlEncode(pub.Q.Y!),
            kid,
            use = "sig",
            alg = "ES256",
        };
        var key = new SigningKey
        {
            Kid = kid,
            PrivatePem = ecdsa.ExportPkcs8PrivateKeyPem(),
            PublicJwk = JsonSerializer.Serialize(jwk),
            CreatedAt = now,
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.SigningKeys.Add(key);
        await db.SaveChangesAsync();
        _keys.Add(key);
        return key;
    }

    static string Base64UrlEncode(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
