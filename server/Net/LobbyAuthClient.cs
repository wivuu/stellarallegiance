using System.Net.Http.Json;
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
            ct
        );
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeviceAuthResponse>(cancellationToken: ct);
        return body ?? throw new InvalidOperationException("POST /auth/device returned an empty body");
    }

    public Task<LobbyTokenOutcome> PollDeviceTokenAsync(string deviceCode, CancellationToken ct) =>
        PostTokenAsync(new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: deviceCode), ct);

    public Task<LobbyTokenOutcome> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: refreshToken), ct);

    async Task<LobbyTokenOutcome> PostTokenAsync(TokenRequest req, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{_lobbyBase}/auth/token", req, ct);
        if (resp.IsSuccessStatusCode)
        {
            var token = await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
            return token is null ? LobbyTokenOutcome.Failed(LobbyTokenError.InvalidGrant) : LobbyTokenOutcome.Ok(token);
        }

        var err = await resp.Content.ReadFromJsonAsync<TokenErrorResponse>(cancellationToken: ct);
        return LobbyTokenOutcome.Failed(err?.Error ?? "unknown_error");
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
