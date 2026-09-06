using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using StellarAllegiance.Shared.Lobby;
using StellarAllegiance.Ui;
// Godot ships its own HttpClient (a low-level TCP node); the lobby's auth calls use the BCL one,
// same choice as ServerLobbyOverlay/UpdateChecker.
using HttpClient = System.Net.Http.HttpClient;

// Owns the player's public-lobby session end to end (plan .PLAN/LobbyRankingService.md WP3.1):
// persists ONLY a refresh token + display name + player id to user://auth.json (NEVER the access
// token), restores it on boot, runs the RFC 8628 device-code flow for a fresh sign-in, and keeps
// the in-memory access token fresh (refreshed 60s ahead of its 15-minute expiry, and on demand for
// WP3.2's bearer calls via GetAccessTokenAsync). A static Instance mirrors SfxManager so any
// script can reach it without a node lookup; register it in Main.tscn the same way.
//
// Threading: every HTTP call runs on a background continuation (Godot has no SynchronizationContext
// to hop back to the main thread after an `await`), so any state mutation that fires StateChanged or
// touches a Godot API (OS.ShellOpen, opening the sign-in modal) is marshalled back with CallDeferred
// — same discipline as GameNetClient/ServerLobbyOverlay. A parsed response is stashed in a private
// field right before the CallDeferred call and read back by the deferred method on the main thread;
// that's safe because each flow (refresh, device-code poll) is single-flight, so there's never a
// concurrent writer to race the field.
public partial class AuthSession : Node
{
    public static AuthSession? Instance { get; private set; }

    public enum State
    {
        SignedOut, // no session — no file, a mismatched lobby, revoked, or a failed sign-in attempt
        Restoring, // auth.json present; refreshing in the background before the first decision
        DeviceFlow, // device code issued, polling /auth/token for approval
        Expired, // the device code expired before it was approved
        Denied, // the operator denied the approval
        SignedIn, // valid session; DisplayName/PlayerId set, GetAccessTokenAsync() serves a token
    }

    public State CurrentState { get; private set; } = State.SignedOut;

    // Fired on every state transition, always on the main thread (see the threading note above).
    public event Action? StateChanged;

    public bool IsSignedIn => CurrentState == State.SignedIn;
    public string DisplayName { get; private set; } = "";
    public Guid? PlayerId { get; private set; }

    // Populated once StartDeviceFlowAsync gets a device code; read by SignInDialog for the big
    // code readout and the OPEN BROWSER / COPY CODE buttons.
    public string UserCode { get; private set; } = "";
    public string VerificationUriComplete { get; private set; } = "";

    // The last error surfaced to the sign-in modal (device-start / poll / refresh failures that
    // aren't one of the named RFC 8628 outcomes). Cleared at the start of every new attempt.
    public string LastError { get; private set; } = "";

    // "CONTINUE WITHOUT ACCOUNT" (or the --anonymous harness flag): session-only, never persisted.
    // While set, ServerLobbyOverlay's sign-in gate stays down for the rest of this process.
    public bool ContinueWithoutAccount { get; private set; }

    private string? _accessToken;
    private DateTimeOffset _accessExpiry;
    private string? _refreshToken;
    private string _lobbyBase = "";

    // Consumed the first time the boot-time auto-prompt would fire (a real SignedOut decision, a
    // harness flag, or reaching SignedIn) so a later drop back to SignedOut mid-session (e.g. the
    // 90-day sliding refresh window finally lapsing) never pops the modal — only launch does.
    private bool _autoPromptArmed = true;

    private CancellationTokenSource? _deviceCts;
    private Task<string?>? _refreshTask;
    private int _refreshGeneration;

    // Cross-thread handoff for the two async flows (refresh + device poll) — see the class doc.
    private TokenResponse? _pendingToken;
    private DeviceAuthResponse? _pendingDeviceAuth;

    private const string AuthFilePath = "user://auth.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // The ONLY thing ever written to disk — refresh token + display name/id for the UI + the lobby
    // base it was issued against (a persisted session for lobby A must never be replayed at lobby B).
    private sealed record AuthFile(
        [property: JsonPropertyName("refreshToken")] string RefreshToken,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("playerId")] Guid PlayerId,
        [property: JsonPropertyName("lobbyBase")] string LobbyBase
    );

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
        _deviceCts?.Cancel();
    }

    public override void _Ready()
    {
        _lobbyBase = ConnectionManager.ResolveLobbyBase();
        if (HasAnonymousFlag())
            ContinueWithoutAccount = true;

        var saved = LoadAuthFile();
        if (saved is null || saved.LobbyBase != _lobbyBase)
        {
            SetState(State.SignedOut);
        }
        else
        {
            _refreshToken = saved.RefreshToken;
            DisplayName = saved.DisplayName;
            PlayerId = saved.PlayerId;
            SetState(State.Restoring);
            _ = RefreshAsync();
        }

        // Launch-time-only decision: shown here (or once the background restore above settles —
        // see SetState) unless a harness flag / direct-join address / --anonymous already ruled it
        // out, or the player already chose anonymous mode.
        MaybeAutoPromptSignIn();
    }

    // ---- Public API --------------------------------------------------------

    // Returns a valid access token, refreshing first if it's missing/near expiry. Null when signed
    // out or the refresh failed (WP3.2's bearer calls fall back to anonymous in that case).
    public async Task<string?> GetAccessTokenAsync()
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessExpiry - TimeSpan.FromSeconds(5))
            return _accessToken;
        if (string.IsNullOrEmpty(_refreshToken))
            return null;
        return await RefreshAsync();
    }

    // RFC 8628 device flow: POST /auth/device, open the browser, then poll /auth/token at the
    // server's cadence (respecting slow_down) until approved/expired/denied. Cancellable — a new
    // call (GET A NEW CODE / TRY AGAIN) or ContinueAnonymously/SignOutAsync supersedes any flight
    // already in progress.
    public async Task StartDeviceFlowAsync()
    {
        _deviceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _deviceCts = cts;
        var ct = cts.Token;
        try
        {
            var resp = await Http.PostAsJsonAsync(
                $"{_lobbyBase}/auth/device",
                new DeviceAuthRequest(LobbyClientKind.Godot),
                JsonOpts,
                ct
            );
            if (!resp.IsSuccessStatusCode)
            {
                CallDeferred(nameof(ApplyFlowError), $"device start failed ({(int)resp.StatusCode})");
                return;
            }
            var dc = await resp.Content.ReadFromJsonAsync<DeviceAuthResponse>(JsonOpts, ct);
            if (dc is null)
            {
                CallDeferred(nameof(ApplyFlowError), "malformed device response");
                return;
            }
            _pendingDeviceAuth = dc;
            CallDeferred(nameof(ApplyDeviceStarted));
            await PollDeviceCodeAsync(dc, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by a newer flow / anonymous / sign-out — no state change.
        }
        catch (Exception e)
        {
            CallDeferred(nameof(ApplyFlowError), e.Message);
        }
    }

    public void CancelDeviceFlow() => _deviceCts?.Cancel();

    // "CONTINUE WITHOUT ACCOUNT" — session-only; consumed by ServerLobbyOverlay's sign-in gate and
    // SettingsDialog's callsign row (both read IsSignedIn / ContinueWithoutAccount).
    public void ContinueAnonymously()
    {
        _deviceCts?.Cancel();
        ContinueWithoutAccount = true;
        _autoPromptArmed = false;
    }

    // Revokes the session at the lobby (best effort) and deletes user://auth.json.
    public async Task SignOutAsync()
    {
        _deviceCts?.Cancel();
        string? bearer = _accessToken;
        _accessToken = null;
        _refreshToken = null;
        DisplayName = "";
        PlayerId = null;
        DeleteAuthFile();
        SetState(State.SignedOut);

        if (string.IsNullOrEmpty(bearer))
            return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_lobbyBase}/auth/revoke");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            await Http.SendAsync(req);
        }
        catch (Exception e)
        {
            Log.Err($"[AuthSession] revoke failed (best effort, already signed out locally): {e.Message}");
        }
    }

    // ---- Boot-time sign-in prompt (plan §1.5) ------------------------------

    private void MaybeAutoPromptSignIn()
    {
        if (!_autoPromptArmed)
            return;
        if (ContinueWithoutAccount || IsHarnessSuppressed())
        {
            _autoPromptArmed = false;
            return;
        }
        if (CurrentState == State.SignedOut)
            ShowSignInModalOnce();
        // Restoring: SetState calls this again once the background refresh settles either way.
    }

    private void ShowSignInModalOnce()
    {
        if (!_autoPromptArmed)
            return;
        _autoPromptArmed = false;
        // Deferred: this can be reached synchronously from AuthSession._Ready() itself (a fresh
        // SignedOut decision, no file to restore) while the engine is still adding Main's other
        // children — add_child on the tree root fails ("Parent node is busy setting up children")
        // if attempted synchronously from inside another node's _Ready.
        CallDeferred(nameof(OpenSignInDialogDeferred));
    }

    private void OpenSignInDialogDeferred() => SignInDialog.Open(this);

    // Harness flags / a direct-join address suppress the modal entirely (plan §1.5). Game flags
    // (--autofly, --host, --anonymous, --stress-*) arrive BEFORE `--` (GetCmdlineArgs); UI-harness
    // flags (--ui-shot, --ui-open, --ui-showcase, --hangar*) arrive AFTER it (GetCmdlineUserArgs) —
    // see client/scripts/ui/UiShowcase.cs and the "Client CLI flags split" convention.
    public static bool IsHarnessSuppressed()
    {
        foreach (string a in OS.GetCmdlineArgs())
        {
            if (
                a == "--autofly"
                || a == "--anonymous"
                || a == "--host"
                || a.StartsWith("--host=")
                || a.StartsWith("--stress-")
            )
                return true;
        }
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (
                a == "--ui-shot"
                || a.StartsWith("--ui-shot=")
                || a.StartsWith("--ui-open=")
                || a == "--ui-showcase"
                || a == "--hangar"
                || a.StartsWith("--hangar-demo=")
            )
                return true;
        }
        // SIM_URI is the other direct-join path (ConnectionManager._Ready) besides --host.
        return !string.IsNullOrEmpty(OS.GetEnvironment("SIM_URI"));
    }

    private static bool HasAnonymousFlag()
    {
        foreach (string a in OS.GetCmdlineArgs())
            if (a == "--anonymous")
                return true;
        return false;
    }

    // ---- Refresh (initial restore, proactive, and on-demand) ---------------

    // Single-flight: the proactive timer and an on-demand GetAccessTokenAsync share one in-flight
    // refresh instead of racing two rotations against the same refresh token.
    private Task<string?> RefreshAsync() => _refreshTask ??= DoRefreshAsync();

    private async Task<string?> DoRefreshAsync()
    {
        try
        {
            string? refreshToken = _refreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                CallDeferred(nameof(ApplyRefreshFailed), false);
                return null;
            }
            var resp = await Http.PostAsJsonAsync(
                $"{_lobbyBase}/auth/token",
                new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: refreshToken),
                JsonOpts
            );
            if (resp.IsSuccessStatusCode)
            {
                var tok = await resp.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts);
                if (tok is null)
                {
                    CallDeferred(nameof(ApplyRefreshFailed), false);
                    return null;
                }
                _pendingToken = tok;
                CallDeferred(nameof(ApplyTokenResult));
                return tok.AccessToken;
            }
            var err = await resp.Content.ReadFromJsonAsync<TokenErrorResponse>(JsonOpts);
            CallDeferred(nameof(ApplyRefreshFailed), err?.Error == LobbyTokenError.InvalidGrant);
            return null;
        }
        catch (Exception e)
        {
            Log.Err($"[AuthSession] refresh failed: {e.Message}");
            CallDeferred(nameof(ApplyRefreshFailed), false);
            return null;
        }
        finally
        {
            _refreshTask = null;
        }
    }

    private async Task PollDeviceCodeAsync(DeviceAuthResponse dc, CancellationToken ct)
    {
        int intervalSec = Math.Max(1, dc.Interval);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.PostAsJsonAsync(
                    $"{_lobbyBase}/auth/token",
                    new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: dc.DeviceCode),
                    JsonOpts,
                    ct
                );
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (resp.IsSuccessStatusCode)
            {
                var tok = await resp.Content.ReadFromJsonAsync<TokenResponse>(JsonOpts, ct);
                if (tok is null)
                {
                    CallDeferred(nameof(ApplyFlowError), "malformed token response");
                    return;
                }
                _pendingToken = tok;
                CallDeferred(nameof(ApplyTokenResult));
                return;
            }

            var err = await resp.Content.ReadFromJsonAsync<TokenErrorResponse>(JsonOpts, ct);
            switch (err?.Error)
            {
                case LobbyTokenError.AuthorizationPending:
                    continue;
                case LobbyTokenError.SlowDown:
                    intervalSec += 5;
                    continue;
                case LobbyTokenError.ExpiredToken:
                    CallDeferred(nameof(ApplyDeviceExpired));
                    return;
                case LobbyTokenError.AccessDenied:
                    CallDeferred(nameof(ApplyDeviceDenied));
                    return;
                default:
                    CallDeferred(nameof(ApplyFlowError), err?.Error ?? "unknown error");
                    return;
            }
        }
    }

    // ---- Main-thread appliers (CallDeferred targets only) ------------------

    private void ApplyDeviceStarted()
    {
        var dc = _pendingDeviceAuth;
        _pendingDeviceAuth = null;
        if (dc is null)
            return;
        LastError = "";
        UserCode = dc.UserCode;
        VerificationUriComplete = dc.VerificationUriComplete;
        SetState(State.DeviceFlow);
        Log.Print($"[AuthSession] device code {dc.UserCode} — approve at {dc.VerificationUriComplete}");
        OS.ShellOpen(dc.VerificationUriComplete);
    }

    // Shared by both the refresh path and a successful device-flow poll: persist + go SignedIn.
    private void ApplyTokenResult()
    {
        var tok = _pendingToken;
        _pendingToken = null;
        if (tok is null)
            return;
        _accessToken = tok.AccessToken;
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(tok.ExpiresIn);
        // ALWAYS persist the newest refresh token immediately — reusing an old one revokes the
        // whole session (plan §1.1).
        _refreshToken = tok.RefreshToken;
        DisplayName = tok.Subject.DisplayName;
        PlayerId = tok.Subject.Id;
        LastError = "";
        SaveAuthFile(new AuthFile(_refreshToken, DisplayName, PlayerId.Value, _lobbyBase));
        ScheduleProactiveRefresh(tok.ExpiresIn);
        Log.Print($"[AuthSession] signed in as {DisplayName}");
        SetState(State.SignedIn);
    }

    private void ApplyRefreshFailed(bool invalidGrant)
    {
        if (invalidGrant)
        {
            DeleteAuthFile();
            _refreshToken = null;
            _accessToken = null;
            DisplayName = "";
            PlayerId = null;
        }
        SetState(State.SignedOut);
    }

    private void ApplyDeviceExpired() => SetState(State.Expired);

    private void ApplyDeviceDenied() => SetState(State.Denied);

    private void ApplyFlowError(string message)
    {
        LastError = message;
        Log.Err($"[AuthSession] {message}");
        SetState(State.SignedOut);
    }

    // ---- State + proactive refresh scheduling ------------------------------

    private void SetState(State s)
    {
        bool changed = CurrentState != s;
        CurrentState = s;
        if (s == State.SignedIn)
            _autoPromptArmed = false;
        if (changed)
            StateChanged?.Invoke();
        if (s == State.SignedOut)
            MaybeAutoPromptSignIn(); // no-op once armed is false (already shown / suppressed / anon)
    }

    // Refreshes 60s ahead of the access token's expiry; a monotonic generation counter means only
    // the MOST RECENT schedule can fire (an earlier one made stale by a newer login/refresh is a
    // silent no-op instead of an extra redundant refresh).
    private void ScheduleProactiveRefresh(int expiresInSeconds)
    {
        int gen = ++_refreshGeneration;
        double delay = Math.Max(5, expiresInSeconds - 60);
        GetTree().CreateTimer(delay).Timeout += () =>
        {
            if (gen == _refreshGeneration && IsInstanceValid(this))
                _ = RefreshAsync();
        };
    }

    // ---- user://auth.json: refresh token + display name + player id ONLY — never the access token --

    private static AuthFile? LoadAuthFile()
    {
        string real = ProjectSettings.GlobalizePath(AuthFilePath);
        if (!File.Exists(real))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(real), JsonOpts);
        }
        catch (Exception e)
        {
            Log.Err($"[AuthSession] failed to read {AuthFilePath}: {e.Message}");
            return null;
        }
    }

    // Write-to-temp-then-rename so a crash/force-quit mid-write can never leave a truncated file.
    private static void SaveAuthFile(AuthFile file)
    {
        string real = ProjectSettings.GlobalizePath(AuthFilePath);
        string dir = Path.GetDirectoryName(real)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $".auth.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(file));
            File.Move(tmp, real, overwrite: true); // atomic rename (POSIX rename / Win32 ReplaceFile)
        }
        catch (Exception e)
        {
            Log.Err($"[AuthSession] failed to save {AuthFilePath}: {e.Message}");
            try
            {
                File.Delete(tmp);
            }
            catch
            { /* best-effort cleanup of the temp file */
            }
        }
    }

    private static void DeleteAuthFile()
    {
        string real = ProjectSettings.GlobalizePath(AuthFilePath);
        try
        {
            if (File.Exists(real))
                File.Delete(real);
        }
        catch (Exception e)
        {
            Log.Err($"[AuthSession] failed to delete {AuthFilePath}: {e.Message}");
        }
    }
}
