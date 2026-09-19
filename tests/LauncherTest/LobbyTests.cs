using System.Net;
using StellarAllegiance.Launcher.Lobby;

// The launcher's "N SERVERS · M PILOTS ONLINE" status strip data source: the SSE wire parser, the
// snapshot JSON, the reconnect ladder, the --lobby/PUBLIC_LOBBY precedence, and the background
// reconnect loop that ties them together against a fake HttpMessageHandler (never a real socket).
static class LobbyTests
{
    public static void Run()
    {
        SseParserTests();
        SnapshotTests();
        BackoffTests();
        AddressTests();
        ClientTests().GetAwaiter().GetResult(); // Run() is sync (Program.cs calls it bare); bridge here.
    }

    private static void SseParserTests()
    {
        T.Section("SseLineParser");

        // Both helpers give a definite (non-null) string back, so a mismatch reads as a clear
        // expected/actual diff instead of a nullable-reference build warning at every call site.
        static string Ev(SseEvent? e) => e is { } v ? v.Event : "<none>";
        static string Data(SseEvent? e) => e is { } v ? v.Data : "<none>";

        {
            var p = new SseLineParser();
            T.Check(p.Feed(": this is a comment") is null, "comment line ignored");
            T.Check(p.Feed("data: hello") is null, "a data line alone does not dispatch");
            var evt = p.Feed("");
            T.Eq("message", Ev(evt), "no event: field → default event name 'message'");
            T.Eq("hello", Data(evt), "…with the accumulated data");
        }
        {
            var p = new SseLineParser();
            p.Feed("event: snapshot");
            p.Feed("data: line1");
            p.Feed("data: line2");
            var evt = p.Feed("");
            T.Eq("snapshot", Ev(evt), "explicit event: name is used");
            T.Eq("line1\nline2", Data(evt), "multiple data: lines join with \\n");
        }
        {
            var p = new SseLineParser();
            p.Feed("data: a"); // one space after the colon → stripped → "a"
            p.Feed("data:  b"); // two spaces → only one stripped → " b"
            p.Feed("data:c"); // no space → unchanged → "c"
            var evt = p.Feed("");
            T.Eq("a\n b\nc", Data(evt), "exactly one optional leading space is stripped, never more");
        }
        {
            var p = new SseLineParser();
            p.Feed("event: snapshot\r");
            p.Feed("data: x\r");
            var evt = p.Feed("\r"); // a CRLF stream's blank line is still just "\r" after the \n split
            T.Eq("snapshot", Ev(evt), "trailing \\r stripped from a CRLF stream (event)");
            T.Eq("x", Data(evt), "…and from a CRLF stream (data)");
        }
        {
            var p = new SseLineParser();
            // "snapshot" itself is split across two chunks, mid-word.
            var first = p.FeedChunk("event: sna").ToList();
            var second = p.FeedChunk("pshot\ndata: hello\n").ToList();
            var third = p.FeedChunk("\n").ToList();
            T.Eq(0, first.Count + second.Count, "no event dispatches until the terminating blank line arrives");
            T.Eq(1, third.Count, "…which arrives in a later FeedChunk call, split mid-line from the one before it");
            T.Check(third.Count == 1 && third[0] is { Event: "snapshot", Data: "hello" }, "…and reassembles correctly");
        }
        {
            var p = new SseLineParser();
            T.Check(p.Feed("") is null, "an empty line with no prior data dispatches nothing");
        }
        {
            var p = new SseLineParser();
            p.Feed("id: 123");
            p.Feed("retry: 5000");
            p.Feed("data: x");
            var evt = p.Feed("");
            T.Eq("message", Ev(evt), "unknown fields (id/retry) are ignored, not treated as the event name");
            T.Eq("x", Data(evt), "…and data still accumulates normally alongside them");
        }
        {
            var p = new SseLineParser();
            p.Feed("data"); // no colon at all → field "data", value ""
            p.Feed("data: x");
            var evt = p.Feed("");
            T.Eq("\nx", Data(evt), "a field line without a colon is a field with an empty value");
        }
        {
            var p = new SseLineParser();
            p.Feed("event: snapshot");
            p.Feed("data: partial");
            p.Reset();
            T.Check(p.Feed("") is null, "Reset() drops an in-progress (undispatched) block");
        }
    }

    private static void SnapshotTests()
    {
        T.Section("LobbySnapshot");

        var ok = LobbySnapshot.TryParse("""{"serversOnline":3,"pilotsOnline":27,"servers":[{"name":"x"}]}""");
        T.Check(
            ok is { ServersOnline: 3, PilotsOnline: 27 },
            "valid payload with an extra servers array parses, extra field ignored"
        );

        T.Check(LobbySnapshot.TryParse("{not json") is null, "malformed JSON → null, never throws");
        T.Check(LobbySnapshot.TryParse("") is null, "empty string → null, never throws");
        T.Check(
            LobbySnapshot.TryParse("""{"serversOnline":-1,"pilotsOnline":5}""") is null,
            "negative serversOnline → null"
        );
        T.Check(LobbySnapshot.TryParse("""{"serversOnline":5,"pilotsOnline":-1}""") is null, "negative pilotsOnline → null");
        T.Check(
            LobbySnapshot.TryParse("""{"serversOnline":0,"pilotsOnline":0}""") is { ServersOnline: 0, PilotsOnline: 0 },
            "zero counts are valid (not treated as missing)"
        );
    }

    private static void BackoffTests()
    {
        T.Section("LobbyBackoff");

        // TimeSpan.FromSeconds no longer rounds to the nearest millisecond, so a jitter factor that
        // isn't exactly representable in binary floating point (e.g. from 0.999999) can land a few
        // hundred nanoseconds off an expected literal computed the "obvious" way. A ~1ms tolerance is
        // far tighter than anything that matters for a reconnect delay and avoids relying on bit-exact
        // FP reproducibility across platforms/architectures.
        static void CheckClose(TimeSpan expected, TimeSpan actual, string name)
        {
            bool close = (expected - actual).Duration() <= TimeSpan.FromMilliseconds(1);
            if (close)
            {
                T.Check(true, name);
                return;
            }
            T.Eq(expected, actual, name); // fails, and prints the expected/actual diff
        }

        double[] ladder = [1, 2, 4, 8, 16, 30, 30];
        for (int i = 0; i < ladder.Length; i++)
        {
            int attempt = i + 1;
            CheckClose(
                TimeSpan.FromSeconds(ladder[i]),
                LobbyBackoff.Delay(attempt, 0.5),
                $"attempt {attempt} @ jitter 0.5 (no change) = {ladder[i]}s"
            );
        }

        CheckClose(TimeSpan.FromSeconds(0.8), LobbyBackoff.Delay(1, 0.0), "jitter 0 = -20% (attempt 1: 1s → 0.8s)");
        CheckClose(TimeSpan.FromSeconds(1.2), LobbyBackoff.Delay(1, 0.999999), "jitter ~1 = +20% (attempt 1: 1s → 1.2s)");
        CheckClose(TimeSpan.FromSeconds(24), LobbyBackoff.Delay(6, 0.0), "jitter 0 = -20% at the 30s ceiling → 24s");
        CheckClose(TimeSpan.FromSeconds(36), LobbyBackoff.Delay(6, 0.999999), "jitter ~1 = +20% at the 30s ceiling → 36s");

        CheckClose(
            TimeSpan.FromSeconds(60),
            LobbyBackoff.Delay(1, 0.5, after503: true),
            "503 floor overrides a short ladder delay (1s → 60s)"
        );
        CheckClose(
            TimeSpan.FromSeconds(60),
            LobbyBackoff.Delay(6, 0.999999, after503: true),
            "503 floor still wins even at ceiling+jitter (36s < 60s)"
        );

        // "Reset": Delay is a pure function of (attempt, jitter) with no hidden state, so calling it
        // at a high attempt count and then back at 1 reproduces the original base delay exactly —
        // which is exactly what LobbyStatusClient relies on when a fresh snapshot zeroes its counter.
        _ = LobbyBackoff.Delay(6, 0.5);
        CheckClose(
            TimeSpan.FromSeconds(1),
            LobbyBackoff.Delay(1, 0.5),
            "reset: attempt 1 after higher attempts is still the base delay"
        );
    }

    private static void AddressTests()
    {
        T.Section("LobbyAddress");

        T.Eq(LobbyAddress.DefaultLobbyBase, LobbyAddress.Resolve([], null), "no args, no env → default");
        T.Eq(
            "http://example.com",
            LobbyAddress.Resolve(["--lobby", "example.com"], null),
            "--lobby <url> (space form), http:// prepended"
        );
        T.Eq(
            "https://example.com",
            LobbyAddress.Resolve(["--lobby=https://example.com"], null),
            "--lobby=<url> (equals form), scheme kept as-is"
        );
        T.Eq(
            "http://example.com",
            LobbyAddress.Resolve([], "example.com"),
            "PUBLIC_LOBBY env used when there is no --lobby arg"
        );
        T.Eq(
            "http://from-arg.com",
            LobbyAddress.Resolve(["--lobby", "from-arg.com"], "from-env.com"),
            "--lobby arg beats the PUBLIC_LOBBY env"
        );
        T.Eq(
            "http://second.com",
            LobbyAddress.Resolve(["--lobby", "first.com", "--lobby", "second.com"], null),
            "repeated --lobby: the last one wins (matches the game's non-breaking scan)"
        );
        T.Eq(
            "http://eq-wins.com",
            LobbyAddress.Resolve(["--lobby", "space.com", "--lobby=eq-wins.com"], null),
            "whichever form appears later wins, not space-form priority"
        );

        // The passthrough-boundary rule. LauncherArgs.GameArgs includes the bare "--" and its tail
        // verbatim, but the real game only ever scans OS.GetCmdlineArgs(), which Godot truncates to
        // the region BEFORE that boundary — a --lobby meant for the UI harness must stay invisible.
        T.Eq(
            LobbyAddress.DefaultLobbyBase,
            LobbyAddress.Resolve(["--", "--lobby", "after-dashdash.com"], null),
            "--lobby after a bare -- is ignored entirely (default wins)"
        );
        T.Eq(
            "http://before.com",
            LobbyAddress.Resolve(["--lobby", "before.com", "--", "--lobby=after.com"], null),
            "--lobby before -- wins even when another follows it after --"
        );
        T.Eq(
            LobbyAddress.DefaultLobbyBase,
            LobbyAddress.Resolve(["--lobby", "--"], null),
            "--lobby as the last token before -- has no value available (mirrors GetCmdlineArgs() truncation)"
        );

        T.Eq(
            "https://example.com",
            LobbyAddress.Resolve(["--lobby=https://example.com/"], null),
            "one trailing slash trimmed"
        );
        T.Eq(
            "https://example.com",
            LobbyAddress.Resolve(["--lobby=https://example.com///"], null),
            "every trailing slash trimmed"
        );
        T.Eq(
            "http://plain-host:8080",
            LobbyAddress.Resolve(["--lobby=plain-host:8080"], null),
            "http:// prepended for a bare host:port"
        );
    }

    private static async Task ClientTests()
    {
        T.Section("LobbyStatusClient");

        // A snapshot delivered on the SSE stream reaches Changed(...) with the parsed values.
        {
            const string body = "event: snapshot\ndata: {\"serversOnline\":3,\"pilotsOnline\":27,\"servers\":[]}\n\n";
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
            await using var client = new LobbyStatusClient("http://fake.local", handler, jitter: () => 0.5);

            var got = new TaskCompletionSource<LobbySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += snap =>
            {
                if (snap is not null)
                    got.TrySetResult(snap);
            };
            client.Start();

            var winner = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            T.Check(winner == got.Task, "a snapshot event is delivered before the test timeout");
            var delivered = winner == got.Task ? await got.Task : null;
            T.Check(delivered is { ServersOnline: 3, PilotsOnline: 27 }, "…with the parsed values");
        }

        // A 503 raises Changed(null) and does not trigger a tight reconnect loop (the floor is >=60s
        // real time; a short real-time window with only 1 request proves there was no tight retry).
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            await using var client = new LobbyStatusClient("http://fake.local", handler, jitter: () => 0.5);

            var gotNull = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += snap =>
            {
                if (snap is null)
                    gotNull.TrySetResult(true);
            };
            client.Start();

            var winner = await Task.WhenAny(gotNull.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            T.Check(winner == gotNull.Task, "a 503 raises Changed(null)");

            await Task.Delay(250); // well under the >=60s floor
            T.Eq(1, handler.RequestCount, "…and the 503 backoff floor prevents a tight reconnect loop");
        }

        // Stop() ends the loop promptly, and no further requests happen afterward.
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var client = new LobbyStatusClient("http://fake.local", handler, jitter: () => 0.5);

            var gotNull = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += snap =>
            {
                if (snap is null)
                    gotNull.TrySetResult(true);
            };
            client.Start();
            await Task.WhenAny(gotNull.Task, Task.Delay(TimeSpan.FromSeconds(2)));

            client.Stop();
            var disposed = client.DisposeAsync().AsTask();
            var winner = await Task.WhenAny(disposed, Task.Delay(TimeSpan.FromSeconds(2)));
            T.Check(winner == disposed, "Stop() lets the background loop exit promptly (DisposeAsync doesn't hang)");

            int countAfterStop = handler.RequestCount;
            await Task.Delay(250);
            T.Eq(countAfterStop, handler.RequestCount, "…and no further requests happen after Stop()");
        }

        // Calling Start() twice while a session is live starts only one loop.
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            await using var client = new LobbyStatusClient("http://fake.local", handler, jitter: () => 0.5);

            var gotNull = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += snap =>
            {
                if (snap is null)
                    gotNull.TrySetResult(true);
            };
            client.Start();
            client.Start(); // idempotent while a session is already live
            await Task.WhenAny(gotNull.Task, Task.Delay(TimeSpan.FromSeconds(2)));

            await Task.Delay(250);
            T.Eq(1, handler.RequestCount, "double Start() makes only one request stream");
        }

        // Session cap: reached mid-connection (via the injected TimeProvider, no real waiting) stops
        // the session for good — one final Changed(null), and no reconnect attempt afterward.
        {
            var fake = new FakeClock(DateTimeOffset.UtcNow);
            var handler = new FakeHandler(_ =>
            {
                // Jump the fake clock past the 5-minute session cap from inside the same synchronous
                // callback that answers the connect — happens-before StreamOnceAsync's first deadline
                // check, so this is deterministic rather than a race against a background thread.
                fake.Now = fake.Now.AddMinutes(10);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(": keepalive\n\n") };
            });
            await using var client = new LobbyStatusClient("http://fake.local", handler, time: fake, jitter: () => 0.5);

            var gotNull = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Changed += snap =>
            {
                if (snap is null)
                    gotNull.TrySetResult(true);
            };
            client.Start();

            var winner = await Task.WhenAny(gotNull.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            T.Check(winner == gotNull.Task, "the session cap reached mid-connection raises Changed(null)");

            await Task.Delay(250);
            T.Eq(1, handler.RequestCount, "…and the session stops itself instead of reconnecting");
        }
    }

    // Records every request it answers; the tests only ever inspect the count and pick the response,
    // never a real socket.
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;
        public int RequestCount => _count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(respond(request));
        }
    }

    // Same shape as the repo's other fake clocks (e.g. tests/LobbyTest/JoinTokenVerifierTests.cs
    // FakeClock): GetUtcNow() only — LobbyStatusClient never relies on TimeProvider.CreateTimer, only
    // on polling GetUtcNow() at decision points, so this minimal fake is sufficient and deterministic.
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
