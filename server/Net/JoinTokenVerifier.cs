using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SimServer.Net;

// Offline verification of lobby-issued join tokens (plan .PLAN/LobbyRankingService.md §3.2;
// language public-lobby/CONTEXT.md "Join Token"). A joiner on a Verified listing presents an
// ES256 JWT in Hello; we check signature (against the lobby's JWKS, fetched at registration and
// again on an unknown `kid`, at most once per 30 s), issuer, audience (= OUR listing id), expiry
// (60 s, 5 s skew) and single use (`jti` remembered until it expires). On success the token's
// (`sub`, `name`) become the pilot's identity and the Hello's typed name is ignored.
public sealed class JoinTokenVerifier
{
    public const string Algorithm = SecurityAlgorithms.EcdsaSha256; // "ES256"
    static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(5);
    static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    readonly string _issuer;
    readonly Func<CancellationToken, Task<string>> _fetchJwks;
    readonly TimeProvider _clock;
    readonly ILogger? _log;
    readonly JsonWebTokenHandler _handler = new();
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    readonly ConcurrentDictionary<string, DateTimeOffset> _seenJti = new();

    IList<SecurityKey> _keys = [];
    DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
    public int FetchCount { get; private set; }

    public JoinTokenVerifier(
        string issuer,
        Func<CancellationToken, Task<string>> fetchJwks,
        TimeProvider? clock = null,
        ILogger? log = null
    )
    {
        _issuer = issuer.TrimEnd('/');
        _fetchJwks = fetchJwks;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public static Func<CancellationToken, Task<string>> HttpJwksFetcher(HttpClient http, string lobbyBase) =>
        ct => http.GetStringAsync($"{lobbyBase.TrimEnd('/')}/.well-known/jwks.json", ct);

    /// <summary>Fetch (or re-fetch) the JWKS now — call at registration so the first join is offline.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var json = await _fetchJwks(ct);
            _keys = new JsonWebKeySet(json).GetSigningKeys();
            _lastFetch = _clock.GetUtcNow();
            FetchCount++;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async ValueTask<JoinTokenResult> VerifyAsync(string token, string expectedAudience, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 4096 || !_handler.CanReadToken(token))
            return JoinTokenResult.Fail(JoinTokenFailure.Malformed);

        JsonWebToken jwt;
        try
        {
            jwt = _handler.ReadJsonWebToken(token);
        }
        catch (Exception)
        {
            return JoinTokenResult.Fail(JoinTokenFailure.Malformed);
        }

        // Unknown kid → one refresh (rate-limited) before giving up: the lobby may have rotated.
        if (!HasKey(jwt.Kid) && _clock.GetUtcNow() - _lastFetch >= MinRefreshInterval)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (Exception e)
            {
                _log?.LogWarning(e, "join token: JWKS refresh failed");
            }
        }
        if (!HasKey(jwt.Kid))
            return JoinTokenResult.Fail(JoinTokenFailure.UnknownKey);

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = _issuer,
            ValidAudience = expectedAudience,
            IssuerSigningKeys = _keys,
            ValidAlgorithms = [Algorithm],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = false,
            ClockSkew = ClockSkew,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                return (notBefore is null || notBefore.Value <= now + ClockSkew)
                    && expires is not null
                    && expires.Value + ClockSkew > now;
            },
        };
        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
            return JoinTokenResult.Fail(
                result.Exception switch
                {
                    SecurityTokenExpiredException
                    or SecurityTokenNotYetValidException
                    or SecurityTokenNoExpirationException
                    or SecurityTokenInvalidLifetimeException => JoinTokenFailure.Expired,
                    SecurityTokenInvalidAudienceException => JoinTokenFailure.WrongAudience,
                    SecurityTokenInvalidIssuerException => JoinTokenFailure.WrongIssuer,
                    SecurityTokenSignatureKeyNotFoundException => JoinTokenFailure.UnknownKey,
                    SecurityTokenInvalidSignatureException or SecurityTokenInvalidAlgorithmException =>
                        JoinTokenFailure.BadSignature,
                    _ => JoinTokenFailure.Other,
                }
            );

        if (!Guid.TryParse(jwt.Subject, out var playerId) || string.IsNullOrEmpty(jwt.Id))
            return JoinTokenResult.Fail(JoinTokenFailure.Malformed);
        var name = jwt.TryGetClaim(JwtRegisteredClaimNames.Name, out var nameClaim) ? nameClaim.Value : "";
        if (string.IsNullOrWhiteSpace(name))
            return JoinTokenResult.Fail(JoinTokenFailure.Malformed);

        // Single use: remember the jti until its expiry (+ skew); sweep opportunistically.
        var expiresAt = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero) + ClockSkew;
        if (!_seenJti.TryAdd(jwt.Id, expiresAt))
            return JoinTokenResult.Fail(JoinTokenFailure.Replayed);
        SweepSeen();
        return JoinTokenResult.Ok(new JoinTokenIdentity(playerId, name, jwt.Id));
    }

    bool HasKey(string? kid) => !string.IsNullOrEmpty(kid) && _keys.Any(k => k.KeyId == kid);

    void SweepSeen()
    {
        if (_seenJti.Count < 256)
            return;
        var now = _clock.GetUtcNow();
        foreach (var (jti, exp) in _seenJti)
            if (exp <= now)
                _seenJti.TryRemove(jti, out _);
    }
}

public enum JoinTokenFailure
{
    Malformed,
    UnknownKey,
    BadSignature,
    WrongIssuer,
    WrongAudience,
    Expired,
    Replayed,
    Other,
}

/// <summary>The pilot identity a verified join token carries: (`sub`, `name`), plus its `jti`.</summary>
public sealed record JoinTokenIdentity(Guid PlayerId, string DisplayName, string Jti);

public readonly record struct JoinTokenResult(JoinTokenIdentity? Identity, JoinTokenFailure? Failure)
{
    public bool IsValid => Identity is not null;

    public static JoinTokenResult Ok(JoinTokenIdentity identity) => new(identity, null);

    public static JoinTokenResult Fail(JoinTokenFailure failure) => new(null, failure);
}
