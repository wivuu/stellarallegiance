using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StellarAllegiance.Shared.Lobby;

namespace SimServer.Net;

// Thin HTTP wrapper over the lobby's auth surface (plan .PLAN/LobbyRankingService.md §3.1):
// POST /auth/device and POST /auth/token. Deliberately isolated from LobbyRegistrar's
// socket/WS plumbing so the device-code/refresh boot flow can be unit-tested against a stub
// HttpMessageHandler (tests/LobbyTest) without opening a real WebSocket.
public sealed class LobbyAuthClient
{
    readonly HttpClient _http;
    readonly string _lobbyBase;

    public LobbyAuthClient(HttpClient http, string lobbyBase)
    {
        _http = http;
        _lobbyBase = lobbyBase.TrimEnd('/');
    }

    public async Task<DeviceAuthResponse> RequestDeviceCodeAsync(string serverName, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"{_lobbyBase}/auth/device",
            new DeviceAuthRequest(LobbyClientKind.SimServer, serverName),
            LobbyHttpJson.Default.DeviceAuthRequest,
            ct
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync(LobbyHttpJson.Default.DeviceAuthResponse, ct);
        return body ?? throw new InvalidOperationException("POST /auth/device returned an empty body");
    }

    public Task<LobbyTokenOutcome> PollDeviceTokenAsync(string deviceCode, CancellationToken ct) =>
        PostTokenAsync(new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: deviceCode), ct);

    public Task<LobbyTokenOutcome> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: refreshToken), ct);

    // Only the lobby's own OAuth answer (a 400/401 carrying {"error": ...}) says anything about the
    // grant. Anything else is the lobby or the path to it failing, and is THROWN so callers treat it
    // as transient and retry the same refresh token: a PaaS edge's 502 with its own JSON body
    // (Railway's, during a deploy cutover), a 429, an HTML error page, a 200 with no token. On
    // 2026-10-24 that 502 body parsed as an error with no code and burned a production server's
    // credential.
    async Task<LobbyTokenOutcome> PostTokenAsync(TokenRequest req, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"{_lobbyBase}/auth/token",
            req,
            LobbyHttpJson.Default.TokenRequest,
            ct
        );
        if (resp.IsSuccessStatusCode)
        {
            var token = await resp.Content.ReadFromJsonAsync(LobbyHttpJson.Default.TokenResponse, ct);
            return token is null
                ? throw new HttpRequestException("POST /auth/token answered 200 with no token")
                : LobbyTokenOutcome.Ok(token);
        }

        if (
            resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
            && await TryReadOAuthErrorAsync(resp, ct) is { } error
        )
            return LobbyTokenOutcome.Failed(error);

        throw new HttpRequestException(
            $"POST /auth/token answered {(int)resp.StatusCode} {resp.ReasonPhrase} with no OAuth error",
            inner: null,
            resp.StatusCode
        );
    }

    static async Task<string?> TryReadOAuthErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync(LobbyHttpJson.Default.TokenErrorResponse, ct);
            return string.IsNullOrEmpty(err?.Error) ? null : err.Error;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null; // not JSON at all (an HTML error page, an empty body)
        }
    }
}

// Either a minted TokenResponse or a LobbyTokenError.* code — never both. Wraps the /auth/token
// 200-vs-400 branch as a value so callers don't juggle a nullable tuple.
public readonly record struct LobbyTokenOutcome(TokenResponse? Token, string? Error)
{
    public bool Succeeded => Token is not null;

    public static LobbyTokenOutcome Ok(TokenResponse token) => new(token, null);

    public static LobbyTokenOutcome Failed(string error) => new(null, error);
}
