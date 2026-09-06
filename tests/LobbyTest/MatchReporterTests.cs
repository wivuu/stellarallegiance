using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SimServer.Net;
using SimServer.Sim;
using StellarAllegiance.Shared.Lobby;

// WP2.3: MatchReportBuilder (ledger + pilot memo → result) and LobbyMatchReporter (disk spool +
// bearer POSTs with terminal/retry semantics), driven through a stub HttpMessageHandler.
static class MatchReporterTests
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "PASS: " : "FAIL: ") + what);
            if (!cond)
                failures++;
        }

        // ---- builder ----
        var ada = Guid.NewGuid();
        var ledger = new Dictionary<int, Simulation.PilotStats>
        {
            [1] = new()
            {
                Kills = 3,
                Deaths = 1,
                Ejects = 2,
                Points = 300,
            },
            [2] = new()
            {
                Kills = 1,
                Deaths = 0,
                Ejects = 0,
                Points = 120,
            },
        };
        var pilots = new List<ClientHub.PilotRecord>
        {
            new(1, "Ada", 0, ada, true),
            new(2, "Bosun", 1, Guid.NewGuid(), false), // leaver
            new(3, "Ghost", 0, null, true), // anonymous, never scored
        };
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ended = DateTimeOffset.UtcNow;
        var built = MatchReportBuilder.Build(
            Guid.NewGuid(),
            "Brimstone Gambit",
            started,
            ended,
            0,
            MatchEndReason.WinCondition,
            ledger,
            t => t == 0 ? 1 : 0,
            t => t == 0 ? 2 : 1,
            pilots,
            "listing-x"
        );
        Check(built.WinnerTeam == 0 && built.EndReason == MatchEndReason.WinCondition, "builder maps the winner byte");
        Check(built.Pilots.Length == 3, "every pilot in the memo is included (leaver + anonymous)");
        Check(
            built.Pilots[0].PlayerId == ada
                && built.Pilots[0].Kills == 3
                && built.Pilots[0].Points == 300
                && built.Pilots[0].ConnectedAtEnd,
            "pilot row joins the ledger"
        );
        Check(!built.Pilots[1].ConnectedAtEnd, "leaver is flagged not connected");
        Check(built.Pilots[2].PlayerId is null && built.Pilots[2].Kills == 0, "anonymous pilot kept with a zero row");
        Check(built.Teams[0].Score == 300 && built.Teams[1].Score == 120, "team score sums its pilots' points");
        Check(
            built.Teams[0].GarrisonsDestroyed == 1
                && built.Teams[0].OutpostsDestroyed == 2
                && built.Teams[1].OutpostsDestroyed == 1,
            "team destruction tallies"
        );
        var noWinner = MatchReportBuilder.Build(
            Guid.NewGuid(),
            "m",
            started,
            ended,
            Simulation.NoWinner,
            MatchEndReason.Reset,
            ledger,
            _ => 0,
            _ => 0,
            pilots,
            null
        );
        Check(noWinner.WinnerTeam is null && noWinner.ListingId is null, "NoWinner → null winner; unlisted stays null");

        // ---- reporter: happy path ----
        var spool = Path.Combine(Path.GetTempPath(), "sa-spool-" + Guid.NewGuid().ToString("N"));
        var identity = new FakeIdentity(Guid.NewGuid(), "http://lobby.test");
        var calls = new List<(string Path, string Bearer, string Body)>();
        var handler = new StubHandler(async req =>
        {
            calls.Add(
                (
                    req.RequestUri!.PathAndQuery,
                    req.Headers.Authorization?.Parameter ?? "",
                    req.Content is null ? "" : await req.Content.ReadAsStringAsync()
                )
            );
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        await using (var reporter = NewReporter(handler, spool, identity))
        {
            var matchId = Guid.NewGuid();
            reporter.OnMatchStarted(new MatchStartInfo(matchId, "Brimstone Gambit", started, "listing-x"));
            reporter.ReportResult(built with { MatchId = matchId });
            Check(await reporter.DrainAsync(TimeSpan.FromSeconds(5)), "start + result delivered");
            Check(
                calls.Count == 2 && calls[0].Path == "/matches" && calls[1].Path == $"/matches/{matchId}/result",
                "POST /matches then POST /matches/{id}/result"
            );
            Check(calls.All(c => c.Bearer == "access-1"), "requests carry the server bearer");
            using var doc = JsonDocument.Parse(calls[1].Body);
            Check(
                doc.RootElement.GetProperty("gameServerId").GetGuid() == identity.GameServerId,
                "result carries the identity's game server id"
            );
            Check(
                doc.RootElement.GetProperty("pilots").GetArrayLength() == 3
                    && doc.RootElement.GetProperty("endReason").GetString() == "win-condition",
                "result body is the plan §3.3 shape"
            );
            Check(Directory.GetFiles(spool).Length == 0, "spool empty after delivery");

            // unlisted → logged only, never spooled
            reporter.OnMatchStarted(new MatchStartInfo(Guid.NewGuid(), "m", started, null));
            reporter.ReportResult(noWinner);
            await Task.Delay(50);
            Check(
                calls.Count == 2 && Directory.GetFiles(spool).Length == 0,
                "unlisted start/result are not spooled or sent"
            );
        }

        // ---- reporter: retry on 503 then 202; 422 dropped; 409 done; 401 refreshes once ----
        calls.Clear();
        int attempt = 0;
        identity = new FakeIdentity(Guid.NewGuid(), "http://lobby.test");
        handler = new StubHandler(req =>
        {
            calls.Add((req.RequestUri!.PathAndQuery, req.Headers.Authorization?.Parameter ?? "", ""));
            attempt++;
            var code = attempt switch
            {
                1 => HttpStatusCode.ServiceUnavailable,
                2 => HttpStatusCode.ServiceUnavailable,
                3 => HttpStatusCode.Accepted, // start delivered on the 3rd try
                4 => HttpStatusCode.Unauthorized, // result: stale token
                5 => HttpStatusCode.UnprocessableEntity, // after refresh: implausible → dropped
                _ => HttpStatusCode.Conflict,
            };
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("{\"error\":\"x\"}") });
        });
        await using (var reporter = NewReporter(handler, spool, identity))
        {
            var m = Guid.NewGuid();
            reporter.OnMatchStarted(new MatchStartInfo(m, "m", started, "L"));
            reporter.ReportResult(built with { MatchId = m, ListingId = "L" });
            reporter.ReportResult(built with { MatchId = Guid.NewGuid(), ListingId = "L" });
            Check(await reporter.DrainAsync(TimeSpan.FromSeconds(10)), "spool drains through 503/401/422/409");
            Check(calls.Take(3).All(c => c.Path == "/matches"), "503s are retried until the start lands");
            Check(
                identity.ForceRefreshes == 1 && calls[4].Bearer == "access-2",
                "401 forces one token refresh and retries with the new bearer"
            );
            Check(calls.Count == 6, "422 and 409 are terminal (no further retries)");
            Check(Directory.GetFiles(spool).Length == 0, "dropped and final items leave the spool");
        }

        // ---- reporter: recovery — a spooled item from a previous run is sent on construction ----
        calls.Clear();
        Directory.CreateDirectory(spool);
        var stale = new LobbyMatchReporter.SpoolItem(
            "result",
            null,
            built with
            {
                MatchId = Guid.NewGuid(),
                ListingId = "L",
            }
        );
        File.WriteAllText(
            Path.Combine(spool, "20260101000000000-stale.result.json"),
            JsonSerializer.Serialize(stale, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        handler = new StubHandler(req =>
        {
            calls.Add((req.RequestUri!.PathAndQuery, "", ""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });
        await using (var reporter = NewReporter(handler, spool, identity))
        {
            Check(
                await reporter.DrainAsync(TimeSpan.FromSeconds(5)) && calls.Count == 1 && calls[0].Path.EndsWith("/result"),
                "pre-existing spool file is sent on boot"
            );
        }

        // ---- reporter: waits (does not drop) while the server is not yet verified ----
        calls.Clear();
        var unverified = new FakeIdentity(Guid.NewGuid(), "http://lobby.test") { Verified = false };
        handler = new StubHandler(req =>
        {
            calls.Add((req.RequestUri!.PathAndQuery, "", ""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        });
        await using (var reporter = NewReporter(handler, spool, unverified))
        {
            reporter.OnMatchStarted(new MatchStartInfo(Guid.NewGuid(), "m", started, "L"));
            await Task.Delay(150);
            Check(
                calls.Count == 0 && Directory.GetFiles(spool).Length == 1,
                "unverified identity: item stays spooled, nothing sent"
            );
            unverified.Verified = true;
            Check(
                await reporter.DrainAsync(TimeSpan.FromSeconds(5)) && calls.Count == 1,
                "…and is sent once the identity becomes verified"
            );
        }

        Directory.Delete(spool, recursive: true);
        return failures;
    }

    static LobbyMatchReporter NewReporter(HttpMessageHandler handler, string spool, ILobbyIdentity identity) =>
        new(
            new HttpClient(handler),
            spool,
            NullLogger.Instance,
            initialBackoff: TimeSpan.FromMilliseconds(10),
            maxBackoff: TimeSpan.FromMilliseconds(40)
        )
        {
            Identity = identity,
        };

    sealed class FakeIdentity(Guid gameServerId, string lobbyBase) : ILobbyIdentity
    {
        public bool Verified { get; set; } = true;
        public int ForceRefreshes { get; private set; }
        int _serial = 1;

        public bool IsVerified => Verified;
        public Guid? GameServerId => gameServerId;
        public string? ListingId => "L";
        public string? LobbyBase => lobbyBase;
        public JoinTokenVerifier? Verifier => null;

        public ValueTask<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
        {
            if (forceRefresh)
            {
                ForceRefreshes++;
                _serial++;
            }
            return ValueTask.FromResult<string?>($"access-{_serial}");
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
