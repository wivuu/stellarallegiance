using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Net;
using StellarAllegiance.Shared.Lobby;

// The sim server's device-code + refresh-token boot flow (WP2.1, plan
// .PLAN/LobbyRankingService.md §4): LobbyCredentialStore's file I/O, then LobbyAuthSession's state
// machine driven through a stub HttpMessageHandler — no real lobby, no real sleeping (the `delay`
// seam returns immediately instead of actually waiting `interval`/backoff spans).
static class LobbyAuthTests
{
    const string LobbyBase = "https://lobby.example";

    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "PASS: " : "FAIL: ") + what);
            if (!cond)
                failures++;
        }

        RunCredentialStoreTests(Check);
        await RunMismatchedFileFallsThroughToDeviceFlow(Check);
        await RunMatchingFileResumesViaRefresh(Check);
        await RunDeviceFlowPendingSlowDownApproved(Check);
        await RunAccessDeniedStopsWithNoRetry(Check);

        return failures;
    }

    // ---- LobbyCredentialStore: write -> read, 0600 on Unix, malformed/empty ignored --------
    static void RunCredentialStoreTests(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            var cred = new LobbyCredential(LobbyBase, Guid.NewGuid(), "My Server", "r.abc123");
            LobbyCredentialStore.Save(path, cred);

            Check(LobbyCredentialStore.TryLoad(path) == cred, "credential file round-trips through Save/TryLoad");

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Check(
                    mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    $"credential file is 0600 (got {Convert.ToString((int)mode, 8)})"
                );
            }

            File.WriteAllText(path, "{ not valid json");
            Check(
                LobbyCredentialStore.TryLoad(path) is null,
                "malformed JSON ignored (TryLoad returns null, not an exception)"
            );

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new
                    {
                        lobbyBase = LobbyBase,
                        gameServerId = Guid.Empty,
                        serverName = "x",
                        refreshToken = "",
                    }
                )
            );
            Check(LobbyCredentialStore.TryLoad(path) is null, "empty refreshToken / Guid.Empty gameServerId ignored");

            File.Delete(path);
            Check(LobbyCredentialStore.TryLoad(path) is null, "missing file ignored");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Boot: a stored credential for a DIFFERENT lobby is never resumed ------------------
    static async Task RunMismatchedFileFallsThroughToDeviceFlow(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            LobbyCredentialStore.Save(
                path,
                new LobbyCredential("https://old-lobby.example", Guid.NewGuid(), "Srv", "r.stale")
            );

            int deviceRequests = 0,
                refreshGrantRequests = 0,
                deviceCodeGrantRequests = 0;
            var handler = new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/auth/device")
                {
                    deviceRequests++;
                    return JsonResponse(
                        new DeviceAuthResponse(
                            "dc1",
                            "ABCD-1234",
                            $"{LobbyBase}/device",
                            $"{LobbyBase}/device?user_code=ABCD-1234",
                            600,
                            1
                        )
                    );
                }
                if (req.RequestUri!.AbsolutePath == "/auth/token")
                {
                    if (GrantTypeOf(req) == LobbyGrantType.RefreshToken)
                        refreshGrantRequests++; // the mismatched file must never reach a refresh_token POST
                    else
                        deviceCodeGrantRequests++;
                    return JsonResponse(
                        new TokenResponse(
                            "access-1",
                            "refresh-1",
                            900,
                            new TokenSubject(LobbySubjectKind.Server, Guid.NewGuid(), "Srv")
                        )
                    );
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var session = NewSession(handler, path);
            var result = await session.AuthenticateAsync(CancellationToken.None);

            Check(
                result == LobbyAuthSession.BootResult.Approved,
                "mismatched stored lobbyBase still ends in approval (via the device flow)"
            );
            Check(refreshGrantRequests == 0, "the mismatched credential was never presented as a refresh_token");
            Check(deviceCodeGrantRequests == 1, "the device_code grant was redeemed exactly once");
            Check(deviceRequests == 1, "device flow ran exactly once");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Boot: a stored credential for the SAME lobby resumes via refresh_token ------------
    static async Task RunMatchingFileResumesViaRefresh(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            var gameServerId = Guid.NewGuid();
            LobbyCredentialStore.Save(path, new LobbyCredential(LobbyBase, gameServerId, "Srv", "r.old"));

            int deviceRequests = 0,
                refreshRequests = 0;
            var handler = new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/auth/device")
                {
                    deviceRequests++;
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                if (req.RequestUri!.AbsolutePath == "/auth/token")
                {
                    refreshRequests++;
                    return JsonResponse(
                        new TokenResponse(
                            "access-3",
                            "refresh-3",
                            900,
                            new TokenSubject(LobbySubjectKind.Server, gameServerId, "Srv")
                        )
                    );
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var session = NewSession(handler, path);
            var result = await session.AuthenticateAsync(CancellationToken.None);

            Check(result == LobbyAuthSession.BootResult.Approved, "matching stored credential resumes via refresh_token");
            Check(deviceRequests == 0, "no device code requested when the stored credential resumes");
            Check(refreshRequests == 1, "exactly one refresh_token POST");
            Check(session.AccessToken == "access-3", "resumed AccessToken adopted from the refresh response");

            var saved = LobbyCredentialStore.TryLoad(path);
            Check(
                saved is not null && saved.RefreshToken == "refresh-3",
                "the rotated refresh token was persisted immediately"
            );
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Device flow: authorization_pending -> slow_down -> approved -----------------------
    static async Task RunDeviceFlowPendingSlowDownApproved(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            var gameServerId = Guid.NewGuid();
            int tokenPolls = 0;
            var delaysRequested = new List<TimeSpan>();

            var handler = new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/auth/device")
                    return JsonResponse(
                        new DeviceAuthResponse(
                            "dc2",
                            "WXYZ-9876",
                            $"{LobbyBase}/device",
                            $"{LobbyBase}/device?user_code=WXYZ-9876",
                            600,
                            2
                        )
                    );
                if (req.RequestUri!.AbsolutePath == "/auth/token")
                {
                    tokenPolls++;
                    return tokenPolls switch
                    {
                        1 => ErrorResponse(LobbyTokenError.AuthorizationPending),
                        2 => ErrorResponse(LobbyTokenError.SlowDown),
                        _ => JsonResponse(
                            new TokenResponse(
                                "access-2",
                                "refresh-2",
                                900,
                                new TokenSubject(LobbySubjectKind.Server, gameServerId, "Srv")
                            )
                        ),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            Task<bool> RecordingDelay(TimeSpan span, CancellationToken ct)
            {
                delaysRequested.Add(span);
                return Task.FromResult(true);
            }

            var session = NewSession(handler, path, RecordingDelay);
            var result = await session.AuthenticateAsync(CancellationToken.None);

            Check(result == LobbyAuthSession.BootResult.Approved, "device flow settles on approval");
            Check(tokenPolls == 3, $"polled 3 times (pending, slow_down, approved) — got {tokenPolls}");
            Check(session.GameServerId == gameServerId, "GameServerId adopted from the approved token's subject");
            Check(session.AccessToken == "access-2", "AccessToken cached from the approved token");
            Check(
                delaysRequested.Count == 3 && delaysRequested[2] > delaysRequested[0],
                "slow_down widened the poll interval"
            );

            var saved = LobbyCredentialStore.TryLoad(path);
            Check(saved is not null && saved.RefreshToken == "refresh-2", "the newly-minted refresh token was persisted");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- access_denied: stop for good, no retry loop ---------------------------------------
    static async Task RunAccessDeniedStopsWithNoRetry(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            int deviceRequests = 0;
            var handler = new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/auth/device")
                {
                    deviceRequests++;
                    return JsonResponse(
                        new DeviceAuthResponse(
                            "dc3",
                            "DENY-0001",
                            $"{LobbyBase}/device",
                            $"{LobbyBase}/device?user_code=DENY-0001",
                            600,
                            1
                        )
                    );
                }
                if (req.RequestUri!.AbsolutePath == "/auth/token")
                    return ErrorResponse(LobbyTokenError.AccessDenied);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var session = NewSession(handler, path);
            var result = await session.AuthenticateAsync(CancellationToken.None);

            Check(result == LobbyAuthSession.BootResult.Denied, "access_denied yields Denied");
            Check(deviceRequests == 1, "access_denied requests exactly one device code (no retry loop)");
            Check(session.AccessToken is null, "no access token adopted on denial");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    static LobbyAuthSession NewSession(
        HttpMessageHandler handler,
        string authFilePath,
        Func<TimeSpan, CancellationToken, Task<bool>>? delay = null
    )
    {
        var http = new HttpClient(handler);
        var authClient = new LobbyAuthClient(http, LobbyBase);
        return new LobbyAuthSession(
            authClient,
            "Srv",
            LobbyBase,
            authFilePath,
            NullLogger.Instance,
            delay: delay ?? ((_, _) => Task.FromResult(true))
        );
    }

    // Distinguishes a POST /auth/token's grant_type (refresh_token vs the device_code poll) so a
    // stub can assert WHICH grant reached the lobby, not just that /auth/token was hit.
    static string? GrantTypeOf(HttpRequestMessage req)
    {
        var json = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("grant_type", out var g) ? g.GetString() : null;
    }

    static HttpResponseMessage JsonResponse<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    static HttpResponseMessage ErrorResponse(string error) =>
        new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new TokenErrorResponse(error)) };

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
