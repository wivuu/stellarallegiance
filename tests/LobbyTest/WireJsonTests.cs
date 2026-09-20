using System.Text.Json;
using SimServer.Net;
using StellarAllegiance.Shared.Lobby;

// The server's JSON is source-generated (server/Net/ServerJson.cs) because it publishes as NativeAOT.
// These pin that the move changed NOTHING on the wire: every body is compared, byte for byte, with
// what the previous reflection code produced - the same anonymous type, serialized with the options
// that call site implicitly used (System.Net.Http.Json = Web defaults; the /servers/ws frames =
// plain defaults, which is why the roster is PascalCase there and camelCase in the HTTP body). The
// deployed public lobby talks to old and new servers alike, so a drift here is a production bug.
static class WireJsonTests
{
    static readonly JsonSerializerOptions LegacyWeb = new(JsonSerializerDefaults.Web);
    static readonly JsonSerializerOptions LegacyDefault = new();

    public static int Run()
    {
        int failures = 0;
        void Same(string actual, string legacy, string what)
        {
            bool ok = actual == legacy;
            Console.WriteLine((ok ? "PASS: " : "FAIL: ") + what + (ok ? "" : $"\n  now:    {actual}\n  legacy: {legacy}"));
            if (!ok)
                failures++;
        }
        void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "PASS: " : "FAIL: ") + what);
            if (!cond)
                failures++;
        }

        // A name with non-ASCII + HTML-sensitive characters pins the (default) escaping too.
        var roster = new List<LobbyStatus.RosterDto>
        {
            new("Ünï <&> 'q'", 0, true, false, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
            new("anon", 1, false, true),
        };

        // ---- POST /servers ----
        Same(
            JsonSerializer.Serialize(
                new RegisterRequestDto("Srv", 8090, null, 2, 32, "lobby", 42, roster, true),
                LobbyHttpJson.Default.RegisterRequestDto
            ),
            JsonSerializer.Serialize(
                new
                {
                    name = "Srv",
                    port = 8090,
                    publicEndpoint = (string?)null,
                    players = 2,
                    maxPlayers = 32,
                    state = "lobby",
                    protocolVersion = 42,
                    roster,
                    @protected = true,
                },
                LegacyWeb
            ),
            "register body is byte-identical to the reflection-era anonymous type (camelCase roster, null endpoint kept)"
        );

        // ---- POST /connect/{ticket}/answer ----
        Same(
            JsonSerializer.Serialize(new WebRtcAnswerDto("v=0\r\na=x"), LobbyHttpJson.Default.WebRtcAnswerDto),
            JsonSerializer.Serialize(new { sdpAnswer = "v=0\r\na=x" }, LegacyWeb),
            "WebRTC answer body is byte-identical"
        );

        // ---- /servers/ws frames (DEFAULT options: PascalCase roster members) ----
        Same(
            JsonSerializer.Serialize(new WsAuthMsg("auth", "sid-1", null), LobbyWsJson.Default.WsAuthMsg),
            JsonSerializer.Serialize(
                new
                {
                    type = "auth",
                    sessionId = "sid-1",
                    secret = (string?)null,
                },
                LegacyDefault
            ),
            "ws auth frame is byte-identical (null secret kept)"
        );
        Same(
            JsonSerializer.Serialize(new WsUpdateMsg("update", 2, 32, "active", roster), LobbyWsJson.Default.WsUpdateMsg),
            JsonSerializer.Serialize(
                new
                {
                    type = "update",
                    players = 2,
                    maxPlayers = 32,
                    state = "active",
                    roster,
                },
                LegacyDefault
            ),
            "ws update frame is byte-identical (PascalCase roster, unlike the HTTP body)"
        );
        Same(
            JsonSerializer.Serialize(new WsPingMsg("ping"), LobbyWsJson.Default.WsPingMsg),
            JsonSerializer.Serialize(new { type = "ping" }, LegacyDefault),
            "ws ping frame is byte-identical"
        );

        // ---- reads: the lobby answers camelCase; both contexts bind case-insensitively ----
        var reply = JsonSerializer.Deserialize("""{"type":"error","message":"nope"}"""u8, LobbyWsJson.Default.WsReplyDto);
        Check(reply is { Type: "error", Message: "nope" }, "ws reply binds from camelCase");
        var offer = JsonSerializer.Deserialize(
            """{"type":"offer","ticket":"t1","sdpOffer":"v=0"}"""u8,
            LobbyWsJson.Default.WsOfferMsg
        );
        Check(offer is { Type: "offer", Ticket: "t1", SdpOffer: "v=0" }, "ws offer push binds from camelCase");
        var registered = JsonSerializer.Deserialize(
            """{"server":{"sessionId":"s1","publicEndpoint":null,"iceServers":[{"urls":["stun:x:3478"],"username":null,"credential":null}],"extra":1},"secret":"cap"}""",
            LobbyHttpJson.Default.RegisterResponseDto
        );
        Check(
            registered is { Secret: "cap", Server: { SessionId: "s1", PublicEndpoint: null, IceServers.Count: 1 } }
                && registered.Server.IceServers![0].Urls is ["stun:x:3478"],
            "register response binds (ICE servers, null endpoint, unknown members ignored)"
        );

        // ---- the two on-disk files (old files must stay readable, new ones identical) ----
        var cred = new LobbyCredential(
            "https://lobby.example",
            Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301"),
            "Srv",
            "rt"
        );
        Same(
            JsonSerializer.Serialize(cred, LobbyHttpJson.Default.LobbyCredential),
            JsonSerializer.Serialize(cred, LegacyWeb),
            "credential file is byte-identical"
        );
        var start = new MatchStartInfo(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "Brimstone Gambit",
            DateTimeOffset.UnixEpoch,
            "l1"
        );
        var result = new MatchResultInfo(
            start.MatchId,
            start.Map,
            start.StartedAt,
            DateTimeOffset.UnixEpoch.AddMinutes(9),
            1,
            MatchEndReason.WinCondition,
            [new MatchTeamResult(0, 1, 2, 300)],
            [new MatchPilotResult(null, "p", 0, 1, 2, 3, 40, true)],
            "l1"
        );
        foreach (
            var item in new[]
            {
                new LobbyMatchReporter.SpoolItem("start", start, null),
                new LobbyMatchReporter.SpoolItem("result", null, result),
            }
        )
        {
            string now = JsonSerializer.Serialize(item, LobbyHttpJson.Default.SpoolItem);
            Same(now, JsonSerializer.Serialize(item, LegacyWeb), $"spool file ({item.Kind}) is byte-identical");
            var back = JsonSerializer.Deserialize(now, LobbyHttpJson.Default.SpoolItem);
            Check(
                back is not null && JsonSerializer.Serialize(back, LobbyHttpJson.Default.SpoolItem) == now,
                $"spool file ({item.Kind}) round-trips"
            );
        }

        return failures;
    }
}
