using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using PublicLobby.Auth;
using PublicLobby.Data;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

// WP1.1: device-code flow (Godot client + game server), opaque sessions, refresh rotation with
// reuse detection, revocation, bearer auth, the dev grant, and the /device page's login gate.
// Runs against the real host (LobbyHostFixture); expiry cases drive the grains with explicit
// clocks instead of waiting.
static partial class Suite
{
    sealed record TokenOutcome(TokenResponse? Token, TokenErrorResponse? Error);

    static async Task RunAuthTestsAsync()
    {
        Console.WriteLine("[auth] device codes, sessions, refresh rotation, bearer");
        var host = await LobbyHostFixture.GetAsync();
        if (host is null)
        {
            Console.WriteLine("  WARN Docker unavailable; skipping auth section");
            return;
        }
        var (http, services) = host.Value;
        var grains = services.GetRequiredService<IGrainFactory>();

        // ---- dev grant: gated, then creates/finds a player ----
        Environment.SetEnvironmentVariable("AUTH_DEV_LOGIN", null);
        var refused = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Vex"));
        Eq(LobbyTokenError.UnsupportedGrantType, refused.Error?.Error, "dev grant refused when AUTH_DEV_LOGIN is unset");
        Environment.SetEnvironmentVariable("AUTH_DEV_LOGIN", "true");

        var dev = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Vex"));
        Check(dev.Token is not null, "dev grant issues a session");
        var devTok = dev.Token!;
        Eq(LobbySubjectKind.Player, devTok.Subject.Kind, "dev grant subject kind is player");
        Eq("Vex", devTok.Subject.DisplayName, "dev grant carries the display name");
        Eq(900, devTok.ExpiresIn, "access token lifetime is 15 min");
        Eq("Bearer", devTok.TokenType, "token_type is Bearer");
        var vexId = devTok.Subject.Id;
        var devAgain = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "vex"));
        Eq(vexId, devAgain.Token?.Subject.Id, "dev grant finds the existing player case-insensitively");
        var tooShort = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.Dev, DisplayName: "Vx"));
        Eq("invalid_request", tooShort.Error?.Error, "dev grant validates display-name length");

        // ---- device flow: Godot client ----
        var start = await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest(LobbyClientKind.Godot));
        Eq(HttpStatusCode.OK, start.StatusCode, "POST /auth/device (godot)");
        var dc = (await start.Content.ReadFromJsonAsync<DeviceAuthResponse>())!;
        Eq(43, dc.DeviceCode.Length, "device_code is 32 random bytes base64url");
        Eq(9, dc.UserCode.Length, "user_code is shown as XXXX-XXXX");
        Check(dc.VerificationUri.EndsWith("/device"), "verification_uri points at /device");
        Check(
            dc.VerificationUriComplete.EndsWith("/device?user_code=" + dc.UserCode),
            "verification_uri_complete carries the code"
        );
        Eq(600, dc.ExpiresIn, "device code expires_in is 600");
        Eq(5, dc.Interval, "poll interval is 5");

        var pending = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: dc.DeviceCode));
        Eq(LobbyTokenError.AuthorizationPending, pending.Error?.Error, "first poll: authorization_pending");
        var slow = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: dc.DeviceCode));
        Eq(LobbyTokenError.SlowDown, slow.Error?.Error, "immediate re-poll: slow_down");
        var bogus = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: new string('A', 43)));
        Eq(LobbyTokenError.InvalidGrant, bogus.Error?.Error, "unknown device code: invalid_grant");
        var malformed = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: "nope"));
        Eq(LobbyTokenError.InvalidGrant, malformed.Error?.Error, "malformed device code: invalid_grant");

        // The /device page resolves the typed code through the query grain, then approves via the
        // device-code grain — exercised here at the grain seam (the page itself is cookie-gated).
        var typed = DeviceCodeGrain.NormalizeUserCode(dc.UserCode.ToLowerInvariant())!;
        var resolved = await grains.GetGrain<IQueryGrain>(0).FindDeviceCodeByUserCode(typed);
        Eq(dc.DeviceCode, resolved, "user code resolves to its device code (case/hyphen-insensitive)");
        var deviceGrain = grains.GetGrain<IDeviceCodeGrain>(dc.DeviceCode);
        var view = await deviceGrain.Describe(DateTimeOffset.UtcNow);
        Eq(SubjectKind.Player, view?.Kind, "device code describes a Godot-client approval");
        Eq(DeviceCodeStatus.Pending, view?.Status, "device code is pending");
        Check(await deviceGrain.Approve(vexId, DateTimeOffset.UtcNow), "approve as Vex");
        Check(!await deviceGrain.Approve(vexId, DateTimeOffset.UtcNow), "a second approve is refused");
        Check(
            await grains.GetGrain<IQueryGrain>(0).FindDeviceCodeByUserCode(typed) is null,
            "approved code no longer resolves as pending"
        );

        var approved = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: dc.DeviceCode));
        Check(approved.Token is not null, "poll after approval issues tokens");
        Eq(vexId, approved.Token?.Subject.Id, "device session belongs to the approving player");
        Eq("Vex", approved.Token?.Subject.DisplayName, "device session carries the display name");
        var spent = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: dc.DeviceCode));
        Eq(LobbyTokenError.InvalidGrant, spent.Error?.Error, "device code is single use");

        // ---- bearer + revoke ----
        Eq(HttpStatusCode.Unauthorized, await RevokeAsync(http, "a.garbage"), "malformed bearer: 401");
        Eq(
            HttpStatusCode.Unauthorized,
            await RevokeAsync(http, OpaqueTokens.Mint(OpaqueTokens.AccessPrefix, Guid.NewGuid())),
            "unknown lineage: 401"
        );
        Eq(
            HttpStatusCode.Unauthorized,
            await RevokeAsync(http, approved.Token!.RefreshToken),
            "refresh token used as bearer: 401"
        );
        Eq(HttpStatusCode.NoContent, await RevokeAsync(http, approved.Token.AccessToken), "valid bearer revokes: 204");
        Eq(
            HttpStatusCode.Unauthorized,
            await RevokeAsync(http, approved.Token.AccessToken),
            "revoked bearer: 401 (cache evicted)"
        );
        var afterRevoke = await PostTokenAsync(
            http,
            new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: approved.Token.RefreshToken)
        );
        Eq(LobbyTokenError.InvalidGrant, afterRevoke.Error?.Error, "refresh after revoke: invalid_grant");

        // ---- refresh rotation (form-encoded, the RFC shape) + reuse detection ----
        var r1 = await PostTokenFormAsync(
            http,
            ("grant_type", LobbyGrantType.RefreshToken),
            ("refresh_token", devTok.RefreshToken)
        );
        Check(r1.Token is not null, "refresh (form-encoded) rotates");
        Check(
            r1.Token!.RefreshToken != devTok.RefreshToken && r1.Token.AccessToken != devTok.AccessToken,
            "rotation mints new tokens"
        );
        Eq(vexId, r1.Token.Subject.Id, "rotated session keeps its subject");
        var reuse = await PostTokenAsync(
            http,
            new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: devTok.RefreshToken)
        );
        Eq(LobbyTokenError.InvalidGrant, reuse.Error?.Error, "reusing the rotated-away refresh token is refused");
        var lineageDead = await PostTokenAsync(
            http,
            new TokenRequest(LobbyGrantType.RefreshToken, RefreshToken: r1.Token.RefreshToken)
        );
        Eq(LobbyTokenError.InvalidGrant, lineageDead.Error?.Error, "...and the reuse revoked the whole lineage");

        // ---- expiry, driven by explicit clocks at the grain ----
        var t0 = DateTimeOffset.UtcNow;
        var sg = grains.GetGrain<ISessionGrain>(Guid.CreateVersion7());
        var issued = await sg.Create(SubjectKind.Player, vexId, t0);
        var accessHash = OpaqueTokens.Hash(issued.AccessToken);
        Check(
            await sg.ValidateAccess(accessHash, t0.AddMinutes(14)) is { } s14 && s14.Id == vexId,
            "access token valid at 14 min"
        );
        Check(await sg.ValidateAccess(accessHash, t0.AddMinutes(16)) is null, "access token expired at 16 min");
        Check(await sg.ValidateAccess(OpaqueTokens.Hash("x"), t0) is null, "wrong access hash rejected");
        var slid = await sg.Refresh(issued.RefreshToken, t0.AddDays(89));
        Check(slid is not null, "refresh at day 89 slides the window");
        Check(
            await sg.Refresh(slid!.RefreshToken, t0.AddDays(89 + 89)) is not null,
            "…so day 178 is still inside the slid window"
        );
        var sg2 = grains.GetGrain<ISessionGrain>(Guid.CreateVersion7());
        var issued2 = await sg2.Create(SubjectKind.Player, vexId, t0);
        Check(await sg2.Refresh(issued2.RefreshToken, t0.AddDays(91)) is null, "refresh after 90 idle days is refused");
        Check(
            await sg2.ValidateAccess(OpaqueTokens.Hash(issued2.AccessToken), t0.AddMinutes(1)) is not null,
            "…without revoking a still-valid access token"
        );

        // ---- device flow: game server → mints a GameServer owned by the approver ----
        var badName = await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest(LobbyClientKind.SimServer, "x"));
        Eq(HttpStatusCode.BadRequest, badName.StatusCode, "server device request needs a 3-50 char name");
        var badKind = await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest("toaster"));
        Eq(HttpStatusCode.BadRequest, badKind.StatusCode, "unknown client kind is refused");
        var sdc = (
            await (
                await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest(LobbyClientKind.SimServer, "Vex's Box"))
            ).Content.ReadFromJsonAsync<DeviceAuthResponse>()
        )!;
        var sGrain = grains.GetGrain<IDeviceCodeGrain>(sdc.DeviceCode);
        var sView = await sGrain.Describe(DateTimeOffset.UtcNow);
        Eq(SubjectKind.Server, sView?.Kind, "server device code describes a game-server approval");
        Eq("Vex's Box", sView?.ServerName, "…naming the server");
        Check(await sGrain.Approve(vexId, DateTimeOffset.UtcNow), "operator approves the server");
        var sTok = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: sdc.DeviceCode));
        Eq(LobbySubjectKind.Server, sTok.Token?.Subject.Kind, "server session kind is server");
        Eq("Vex's Box", sTok.Token?.Subject.DisplayName, "server session carries the server name");
        var gs = await grains.GetGrain<IGameServerGrain>(sTok.Token!.Subject.Id).Get();
        Eq(vexId, gs?.OperatorPlayerId, "game server row is owned by the approving player");
        Eq(false, gs?.Ranked, "new game servers are unranked");

        // ---- deny + expiry of device codes ----
        var ddc = (
            await (
                await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest(LobbyClientKind.Godot))
            ).Content.ReadFromJsonAsync<DeviceAuthResponse>()
        )!;
        Check(await grains.GetGrain<IDeviceCodeGrain>(ddc.DeviceCode).Deny(vexId, DateTimeOffset.UtcNow), "deny");
        var denied = await PostTokenAsync(http, new TokenRequest(LobbyGrantType.DeviceCode, DeviceCode: ddc.DeviceCode));
        Eq(LobbyTokenError.AccessDenied, denied.Error?.Error, "poll after deny: access_denied");
        var edc = (
            await (
                await http.PostAsJsonAsync("/auth/device", new DeviceAuthRequest(LobbyClientKind.Godot))
            ).Content.ReadFromJsonAsync<DeviceAuthResponse>()
        )!;
        var eGrain = grains.GetGrain<IDeviceCodeGrain>(edc.DeviceCode);
        var late = DateTimeOffset.UtcNow.AddMinutes(11);
        Check(!await eGrain.Approve(vexId, late), "approve after 10 min is refused");
        Eq(DevicePollOutcome.Expired, (await eGrain.Poll(late)).Outcome, "poll after 10 min: expired");

        // ---- /device page is cookie-gated ----
        var page = await http.GetAsync("/device?user_code=BCDF-GHJK");
        Eq("/login", page.RequestMessage?.RequestUri?.AbsolutePath, "anonymous /device redirects to /login");
        Check(page.RequestMessage?.RequestUri?.Query.Contains("user_code") == true, "…preserving the code in returnUrl");

        // ---- ALLOW_PASSKEY_SIGNUP=false: no new passkey-only accounts ----
        Environment.SetEnvironmentVariable("ALLOW_PASSKEY_SIGNUP", "false");
        try
        {
            var loginOff = await http.GetStringAsync("/login");
            Check(!loginOff.Contains("id=\"passkey-signup\""), "/login hides the create-a-passkey form when sign-up is off");
            Check(loginOff.Contains("id=\"passkey-signin\""), "/login still offers passkey sign-in when sign-up is off");
            var optionsOff = await http.PostAsJsonAsync("/login/passkey/creation-options", new { displayName = "Newbie" });
            Eq(HttpStatusCode.Forbidden, optionsOff.StatusCode, "anonymous creation-options refused when sign-up is off");
            var registerOff = await http.PostAsJsonAsync(
                "/login/passkey/register",
                new { displayName = "Newbie", credential = "{}" }
            );
            Check(
                HttpStatusCode.Forbidden == registerOff.StatusCode,
                "anonymous register never creates an account when sign-up is off"
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALLOW_PASSKEY_SIGNUP", null);
        }
        var loginOn = await http.GetStringAsync("/login");
        Check(loginOn.Contains("id=\"passkey-signup\""), "/login shows the create-a-passkey form by default");
        var optionsOn = await http.PostAsJsonAsync("/login/passkey/creation-options", new { displayName = "Newbie" });
        Eq(HttpStatusCode.OK, optionsOn.StatusCode, "anonymous creation-options served by default");
        Check(
            (await optionsOn.Content.ReadAsStringAsync()).Contains("\"challenge\""),
            "creation-options is a WebAuthn options document"
        );
    }

    static async Task<TokenOutcome> PostTokenAsync(HttpClient http, TokenRequest req)
    {
        var r = await http.PostAsJsonAsync("/auth/token", req);
        return await ReadTokenOutcome(r);
    }

    static async Task<TokenOutcome> PostTokenFormAsync(HttpClient http, params (string Key, string Value)[] fields)
    {
        var r = await http.PostAsync(
            "/auth/token",
            new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)))
        );
        return await ReadTokenOutcome(r);
    }

    static async Task<TokenOutcome> ReadTokenOutcome(HttpResponseMessage r) =>
        r.IsSuccessStatusCode
            ? new TokenOutcome(await r.Content.ReadFromJsonAsync<TokenResponse>(), null)
            : new TokenOutcome(null, await r.Content.ReadFromJsonAsync<TokenErrorResponse>());

    static async Task<HttpStatusCode> RevokeAsync(HttpClient http, string bearer)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/revoke");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return (await http.SendAsync(req)).StatusCode;
    }
}
