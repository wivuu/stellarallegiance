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
        await RunGatewayFailuresKeepTheCredential(Check);
        await RunInvalidGrantBurnsTheCredential(Check);
        await RunSteadyStateRefreshSurvivesAGateway(Check);
        RunRetryDelayBackoffIsJitteredAndCapped(Check);

        return failures;
    }

    // What a PaaS edge answers while a lobby redeploy cuts over: Railway's own 502 JSON (no OAuth
    // `error`), an HTML 503, a bare 429, a 500 that happens to carry an OAuth-looking body, and a
    // 400 with an OAuth error other than invalid_grant. None of them is a verdict on the grant; on
    // 2026-10-24 the first one burned a production server's credential (EventId 1216 -> device code).
    static readonly Func<HttpResponseMessage>[] GatewayFailures =
    [
        () =>
            new(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(
                    """{"status":"error","code":502,"message":"Application failed to respond","request_id":"x"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"
                ),
            },
        () =>
            new(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("<html><body>503</body></html>", System.Text.Encoding.UTF8, "text/html"),
            },
        () => new(HttpStatusCode.TooManyRequests),
        () =>
            new(HttpStatusCode.InternalServerError) { Content = JsonContent.Create(new TokenErrorResponse("server_error")) },
        () => ErrorResponse("temporarily_unavailable"),
    ];

    // ---- Boot refresh: every gateway failure is retried with the SAME token, then it resumes --
    static async Task RunGatewayFailuresKeepTheCredential(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            var gameServerId = Guid.NewGuid();
            LobbyCredentialStore.Save(path, new LobbyCredential(LobbyBase, gameServerId, "Srv", "r.kept"));

            int deviceRequests = 0,
                refreshRequests = 0;
            var presented = new List<string?>();
            var handler = new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/auth/device")
                {
                    deviceRequests++;
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                presented.Add(doc.RootElement.GetProperty("refresh_token").GetString());
                if (refreshRequests++ < GatewayFailures.Length)
                {
                    // The credential must still be on disk while the lobby is failing.
                    Check(File.Exists(path), $"credential still on disk during gateway failure {refreshRequests}");
                    return GatewayFailures[refreshRequests - 1]();
                }
                return JsonResponse(
                    new TokenResponse(
                        "access-ok",
                        "refresh-rotated",
                        900,
                        new TokenSubject(LobbySubjectKind.Server, gameServerId, "Srv")
                    )
                );
            });

            var delays = new List<TimeSpan>();
            var session = NewSession(
                handler,
                path,
                (span, _) =>
                {
                    delays.Add(span);
                    // Once the session has fallen into the device flow the credential is already
                    // lost: stop it there, so a regression FAILS the checks below instead of hanging.
                    return Task.FromResult(deviceRequests == 0);
                }
            );
            var result = await session.AuthenticateAsync(CancellationToken.None);

            Check(result == LobbyAuthSession.BootResult.Approved, "boot refresh rides out every gateway failure");
            Check(deviceRequests == 0, "no device code was requested: the operator never has to re-approve");
            Check(refreshRequests == GatewayFailures.Length + 1, "one refresh POST per failure, then the success");
            Check(presented.TrueForAll(t => t == "r.kept"), "every retry presented the SAME stored refresh token");
            Check(delays.Count == GatewayFailures.Length, "a backoff wait before every retry");
            Check(
                LobbyCredentialStore.TryLoad(path)?.RefreshToken == "refresh-rotated",
                "the rotated refresh token was persisted once the lobby answered"
            );
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Boot refresh: an explicit invalid_grant (and only that) burns the credential ---------
    static async Task RunInvalidGrantBurnsTheCredential(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            LobbyCredentialStore.Save(path, new LobbyCredential(LobbyBase, Guid.NewGuid(), "Srv", "r.revoked"));

            int deviceRequests = 0;
            bool fileGoneBeforeDeviceFlow = false;
            var handler = new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/auth/device")
                {
                    deviceRequests++;
                    fileGoneBeforeDeviceFlow = !File.Exists(path);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                return ErrorResponse(LobbyTokenError.InvalidGrant);
            });

            // Stop at the device flow's first retry wait: reaching it is what this test is about.
            var session = NewSession(handler, path, (_, _) => Task.FromResult(false));
            var result = await session.AuthenticateAsync(CancellationToken.None);

            Check(deviceRequests == 1, "invalid_grant moves straight on to the device flow");
            Check(fileGoneBeforeDeviceFlow, "invalid_grant deleted the stored credential first");
            Check(result == LobbyAuthSession.BootResult.Cancelled, "the device flow was stopped by the test");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Steady state: a gateway failure on refresh keeps the in-memory and on-disk token --------
    static async Task RunSteadyStateRefreshSurvivesAGateway(Action<bool, string> Check)
    {
        var dir = Directory.CreateTempSubdirectory("lobby-auth-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "lobby-auth.json");
            var gameServerId = Guid.NewGuid();
            LobbyCredentialStore.Save(path, new LobbyCredential(LobbyBase, gameServerId, "Srv", "r.0"));

            int refreshes = 0;
            bool failNext = false;
            var handler = new StubHandler(req =>
            {
                refreshes++;
                if (failNext)
                {
                    failNext = false;
                    return GatewayFailures[0]();
                }
                return JsonResponse(
                    new TokenResponse(
                        $"access-{refreshes}",
                        $"refresh-{refreshes}",
                        900,
                        new TokenSubject(LobbySubjectKind.Server, gameServerId, "Srv")
                    )
                );
            });

            var session = NewSession(handler, path);
            await session.AuthenticateAsync(CancellationToken.None);

            failNext = true;
            var during = await session.GetAccessTokenAsync(forceRefresh: true, CancellationToken.None);
            Check(during is null, "a forced refresh through a 502 yields no token this time");
            Check(LobbyCredentialStore.TryLoad(path) is not null, "…but the credential file survives it");

            var after = await session.GetAccessTokenAsync(forceRefresh: true, CancellationToken.None);
            Check(after is not null && after.StartsWith("access-"), "the next refresh succeeds with the kept token");
            Check(session.GameServerId == gameServerId, "the server keeps its Game Server identity throughout");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- Backoff: grows, is capped, and is jittered so a fleet does not retry in lockstep -------
    static void RunRetryDelayBackoffIsJitteredAndCapped(Action<bool, string> Check)
    {
        var first = Enumerable.Range(0, 200).Select(_ => LobbyAuthSession.RetryDelay(0)).ToList();
        Check(
            first.TrueForAll(d => d >= TimeSpan.FromSeconds(2.5) && d <= TimeSpan.FromSeconds(5)),
            "first retry waits 2.5-5 s"
        );
        Check(first.Distinct().Count() > 1, "first retry delays are jittered");
        Check(
            Enumerable.Range(0, 200).All(_ => LobbyAuthSession.RetryDelay(50) <= TimeSpan.FromSeconds(60)),
            "the backoff is capped at 60 s"
        );
        Check(LobbyAuthSession.RetryDelay(3) >= TimeSpan.FromSeconds(20), "the backoff grows (attempt 3 >= 20 s)");
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
