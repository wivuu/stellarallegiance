using Microsoft.Extensions.Logging;
using StellarAllegiance.Shared.Lobby;

namespace SimServer.Net;

// Owns the device-code / refresh-token boot flow and the resulting access-token cache — the whole
// state machine ILobbyIdentity exposes (plan .PLAN/LobbyRankingService.md §4 WP2.1) — isolated
// from LobbyRegistrar's socket/WS plumbing so it's unit-testable against a stub HttpMessageHandler
// (tests/LobbyTest, deliverable 7) with no real sleeping and no real lobby.
//
// Boot: if a saved credential file matches this lobby, refresh it; a refusal (invalid_grant) drops
// the file and falls through to the device-code flow (print the banner, poll `/auth/token` at
// `interval`, +5s after slow_down, request a fresh code on expired_token, give up with no retry on
// access_denied). Either path ends by persisting the (possibly rotated) refresh token BEFORE the
// access token it came with is used for anything.
public sealed class LobbyAuthSession
{
    public enum BootResult
    {
        Approved,
        Denied, // access_denied — caller must not retry
        Cancelled, // ct fired (shutdown) while waiting
    }

    static readonly TimeSpan AccessTokenRefreshMargin = TimeSpan.FromSeconds(60);
    static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(5);
    static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    readonly LobbyAuthClient _authClient;
    readonly string _serverName;
    readonly string _lobbyBase;
    readonly string _authFilePath;
    readonly ILogger _log;
    readonly TimeProvider _clock;
    readonly Func<TimeSpan, CancellationToken, Task<bool>> _delay; // false = cancelled; injectable for tests
    readonly SemaphoreSlim _tokenLock = new(1, 1);

    string? _refreshToken;
    DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    public Guid? GameServerId { get; private set; }
    public string? DisplayName { get; private set; }
    public string? AccessToken { get; private set; }

    public LobbyAuthSession(
        LobbyAuthClient authClient,
        string serverName,
        string lobbyBase,
        string authFilePath,
        ILogger log,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task<bool>>? delay = null
    )
    {
        _authClient = authClient;
        _serverName = serverName;
        _lobbyBase = lobbyBase;
        _authFilePath = authFilePath;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? DefaultDelay;
    }

    static async Task<bool> DefaultDelay(TimeSpan span, CancellationToken ct)
    {
        try
        {
            await Task.Delay(span, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // Boot entry point. Returns once verified (Approved), refused for good (Denied), or the
    // token cancels while waiting on the operator/network (Cancelled).
    public async Task<BootResult> AuthenticateAsync(CancellationToken ct)
    {
        var stored = LobbyCredentialStore.TryLoad(_authFilePath);
        if (stored is not null && string.Equals(stored.LobbyBase, _lobbyBase, StringComparison.Ordinal))
        {
            Log.LobbyAuthResuming(_log, _lobbyBase);
            var (refreshed, cancelled) = await TryRefreshStoredAsync(stored, ct);
            if (refreshed)
                return BootResult.Approved;
            if (cancelled)
                return BootResult.Cancelled;
            // Refused (invalid_grant) — the stored file is already gone; fall through below.
        }

        return await RunDeviceFlowAsync(ct);
    }

    // Keeps retrying the SAME stored refresh token through transient (network) failures — only an
    // explicit invalid_grant burns the credential and moves to the device flow, so a lobby blip
    // never forces the operator to re-approve.
    async Task<(bool Refreshed, bool Cancelled)> TryRefreshStoredAsync(LobbyCredential stored, CancellationToken ct)
    {
        var refreshToken = stored.RefreshToken;
        while (!ct.IsCancellationRequested)
        {
            LobbyTokenOutcome outcome;
            try
            {
                outcome = await _authClient.RefreshAsync(refreshToken, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.LobbyAuthRefreshUnreachable(_log, e.Message);
                if (!await _delay(TransientRetryDelay, ct))
                    return (false, true);
                continue;
            }

            if (outcome.Succeeded)
            {
                Adopt(outcome.Token!);
                return (true, false);
            }

            Log.LobbyAuthRefreshRefused(_log, outcome.Error ?? "unknown");
            LobbyCredentialStore.Delete(_authFilePath);
            return (false, false);
        }
        return (false, true);
    }

    async Task<BootResult> RunDeviceFlowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DeviceAuthResponse device;
            try
            {
                device = await _authClient.RequestDeviceCodeAsync(_serverName, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.LobbyDeviceCodePollError(_log, e.Message);
                if (!await _delay(TransientRetryDelay, ct))
                    return BootResult.Cancelled;
                continue;
            }

            Log.LobbyDeviceCodeBanner(_log, device.VerificationUriComplete, device.UserCode);

            var (result, expired) = await PollAsync(device, ct);
            if (!expired)
                return result;
            Log.LobbyDeviceCodeExpired(_log);
        }
        return BootResult.Cancelled;
    }

    async Task<(BootResult Result, bool Expired)> PollAsync(DeviceAuthResponse device, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval));
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, device.ExpiresIn));

        while (!ct.IsCancellationRequested && _clock.GetUtcNow() < deadline)
        {
            if (!await _delay(interval, ct))
                return (BootResult.Cancelled, false);

            LobbyTokenOutcome outcome;
            try
            {
                outcome = await _authClient.PollDeviceTokenAsync(device.DeviceCode, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.LobbyDeviceCodePollError(_log, e.Message);
                continue; // transient network error — keep polling at the same interval
            }

            if (outcome.Succeeded)
            {
                Adopt(outcome.Token!);
                Log.LobbyDeviceCodeApproved(_log, GameServerId!.Value, DisplayName ?? _serverName, _authFilePath);
                return (BootResult.Approved, false);
            }

            switch (outcome.Error)
            {
                case LobbyTokenError.AuthorizationPending:
                    continue;
                case LobbyTokenError.SlowDown:
                    interval += SlowDownStep;
                    continue;
                case LobbyTokenError.ExpiredToken:
                    return (BootResult.Cancelled, true); // Result unused when Expired
                case LobbyTokenError.AccessDenied:
                    Log.LobbyDeviceCodeDenied(_log);
                    return (BootResult.Denied, false);
                default:
                    Log.LobbyDeviceCodePollError(_log, outcome.Error ?? "unknown_error");
                    continue;
            }
        }
        // Deadline passed with no explicit expired_token from the lobby — request a fresh code.
        return (BootResult.Cancelled, true);
    }

    // Persists the (possibly rotated) refresh token to disk BEFORE it's usable as the in-memory
    // access token for anything, per plan §4 WP2.1 ("always persist the NEWEST refresh token
    // immediately"). GameServerId/DisplayName come from the token's subject (kind=server).
    void Adopt(TokenResponse token)
    {
        GameServerId = token.Subject.Id;
        DisplayName = token.Subject.DisplayName;
        _refreshToken = token.RefreshToken;
        LobbyCredentialStore.Save(
            _authFilePath,
            new LobbyCredential(_lobbyBase, GameServerId.Value, _serverName, _refreshToken)
        );
        AccessToken = token.AccessToken;
        _accessTokenExpiresAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(token.ExpiresIn);
    }

    // The cached access token, refreshing first when within 60 s of expiry or forceRefresh is set.
    // Null when we've never authenticated, or the lobby refuses the refresh outright — a caller
    // seeing null after a prior success should treat it as "re-run AuthenticateAsync".
    public async ValueTask<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (AccessToken is null && _refreshToken is null)
            return null;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (
                !forceRefresh
                && AccessToken is not null
                && _clock.GetUtcNow() < _accessTokenExpiresAt - AccessTokenRefreshMargin
            )
                return AccessToken;
            if (_refreshToken is null)
                return null;

            LobbyTokenOutcome outcome;
            try
            {
                outcome = await _authClient.RefreshAsync(_refreshToken, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.LobbyAuthRefreshUnreachable(_log, e.Message);
                return null;
            }

            if (!outcome.Succeeded)
            {
                Log.LobbyAuthRefreshRefused(_log, outcome.Error ?? "unknown");
                if (outcome.Error == LobbyTokenError.InvalidGrant)
                {
                    LobbyCredentialStore.Delete(_authFilePath);
                    GameServerId = null;
                    DisplayName = null;
                    AccessToken = null;
                    _refreshToken = null;
                }
                return null;
            }

            Adopt(outcome.Token!);
            return AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
