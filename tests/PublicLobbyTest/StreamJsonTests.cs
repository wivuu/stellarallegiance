using System.Text.Json;
using PublicLobby;

static partial class Suite
{
    // Pure logic, no Docker: the SSE feeds and /servers/ws moved from reflection-based JSON to the
    // source-generated contexts in public-lobby/StreamJson.cs. Browsers, launchers and older game
    // servers parse these bytes, so each shape must serialize exactly as the reflection path did:
    // camelCase options for the SSE payloads, default options over the anonymous objects for the
    // outbound socket frames. Inbound frames must still bind case-insensitively.
    static Task RunStreamJsonTestsAsync()
    {
        Console.WriteLine("[stream-json] source-generated SSE + /servers/ws shapes");
        var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var t0 = new DateTimeOffset(2026, 11, 10, 20, 15, 30, TimeSpan.Zero);
        var full = new ServerEntry(
            "sess-1",
            "Carina",
            "carina.example:443",
            t0,
            t0.AddSeconds(42),
            [new IceServer(["stun:stun.example:3478"]), new IceServer(["turn:t.example"], "u", "c")],
            Players: 2,
            MaxPlayers: 32,
            State: "in-progress",
            ProtocolVersion: 45,
            Verified: true,
            GameServerId: Guid.Parse("01a1a7c3-0e84-71fc-93e4-6badaaeda78e"),
            OperatorName: "Op \"quoted\" <tag> é",
            Roster:
            [
                new LobbyRosterEntry("Dev Pilot", 0, true, true, Guid.Parse("01a07d18-b013-7c4a-88a5-59d5a2db62e6")),
                new LobbyRosterEntry("Anon", 1),
            ],
            Protected: true
        );
        var bare = new ServerEntry("sess-2", "Vela", null, t0, t0, []);

        Same(JsonSerializer.Serialize<object?>(full, camel), JsonSerializer.Serialize(full, LobbyStreamJson.Default.ServerEntry), "ServerEntry (every field set)");
        Same(JsonSerializer.Serialize<object?>(bare, camel), JsonSerializer.Serialize(bare, LobbyStreamJson.Default.ServerEntry), "ServerEntry (defaults and nulls)");
        Same(JsonSerializer.Serialize<object?>(null, camel), JsonSerializer.Serialize(null, LobbyStreamJson.Default.ServerEntry), "null entry");

        IReadOnlyCollection<ServerEntry> list = [full, bare];
        Same(JsonSerializer.Serialize<object?>(list, camel), JsonSerializer.Serialize(list, LobbyStreamJson.Default.IReadOnlyCollectionServerEntry), "snapshot list");
        IReadOnlyCollection<ServerEntry> none = [];
        Same(JsonSerializer.Serialize<object?>(none, camel), JsonSerializer.Serialize(none, LobbyStreamJson.Default.IReadOnlyCollectionServerEntry), "empty snapshot");

        var strip = PublicServerStrip.From([full, bare], 1); // More = 1: the computed property must come along
        Same(JsonSerializer.Serialize<object?>(strip, camel), JsonSerializer.Serialize(strip, LobbyStreamJson.Default.PublicServerStrip), "public strip (with More)");

        Same(JsonSerializer.Serialize<object?>(new { version = "0.0.20" }, camel), JsonSerializer.Serialize(new SseRelease("0.0.20"), LobbyStreamJson.Default.SseRelease), "release event");
        Same(JsonSerializer.Serialize<object?>(new { version = (string?)null }, camel), JsonSerializer.Serialize(new SseRelease(null), LobbyStreamJson.Default.SseRelease), "release event (null)");
        Same(JsonSerializer.Serialize<object?>(new { sessionId = "sess-1" }, camel), JsonSerializer.Serialize(new SseRemoved("sess-1"), LobbyStreamJson.Default.SseRemoved), "removed event");

        // Outbound socket frames used to be anonymous objects serialized with DEFAULT options.
        Same(JsonSerializer.Serialize<object>(new { type = "ok" }), JsonSerializer.Serialize(new WsOkFrame("ok"), LobbyStreamJson.Default.WsOkFrame), "ws ok frame");
        Same(
            JsonSerializer.Serialize<object>(new { type = "offer", ticket = "t1", sdpOffer = "v=0\r\no=- 1 1 IN IP4 0.0.0.0" }),
            JsonSerializer.Serialize(new WsOfferFrame("offer", "t1", "v=0\r\no=- 1 1 IN IP4 0.0.0.0"), LobbyStreamJson.Default.WsOfferFrame),
            "ws offer frame (member order, escaping)"
        );
        Same(JsonSerializer.Serialize<object>(new { type = "release", version = "0.0.20" }), JsonSerializer.Serialize(new WsReleaseFrame("release", "0.0.20"), LobbyStreamJson.Default.WsReleaseFrame), "ws release frame");

        // Inbound: the game server writes PascalCase records (server/Net/ServerJson.cs, default options).
        var auth = JsonSerializer.Deserialize("""{"Type":"auth","SessionId":"sess-1","Secret":"s3"}""", LobbyWsInboundJson.Default.WsAuthMsg);
        Eq(("auth", "sess-1", "s3"), (auth?.Type, auth?.SessionId, auth?.Secret), "inbound auth frame binds PascalCase");
        var upd = JsonSerializer.Deserialize(
            """{"Type":"update","Players":1,"MaxPlayers":32,"State":"lobby","Roster":[{"Name":"Dev Pilot","Team":0,"Ready":true,"Flying":false,"PlayerId":"01a07d18-b013-7c4a-88a5-59d5a2db62e6"}]}""",
            LobbyWsInboundJson.Default.WsServerMsg
        );
        Eq(("update", 1, 32, "lobby"), (upd?.Type, upd?.Players, upd?.MaxPlayers, upd?.State), "inbound update frame fields");
        Eq(new LobbyRosterEntry("Dev Pilot", 0, true, false, Guid.Parse("01a07d18-b013-7c4a-88a5-59d5a2db62e6")), upd?.Roster?.Single(), "inbound roster entry");
        var ping = JsonSerializer.Deserialize("""{"type":"ping"}""", LobbyWsInboundJson.Default.WsServerMsg);
        Eq("ping", ping?.Type, "inbound camelCase still binds (case-insensitive)");
        return Task.CompletedTask;

        static void Same(string reflection, string generated, string what)
        {
            if (reflection == generated)
                Console.WriteLine($"  ok   {what}");
            else
            {
                _failures++;
                Console.WriteLine($"  FAIL {what}:\n       reflection {reflection}\n       generated  {generated}");
            }
        }
    }
}
