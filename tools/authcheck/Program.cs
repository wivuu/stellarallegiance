using System.Net.WebSockets;
using System.Text;
using StellarAllegiance.Shared;

// Live auth-handshake check. Usage: authcheck <ws-url> <secret>
// Boots nothing — expects a sim server already running with that --secret. Exits non-zero on
// any unexpected accept/reject so a script can gate on it.

static class Program
{
    const byte MsgWelcome = 1;
    const byte ProtocolVersion = 6;
    static int _fail;

    static async Task<int> Main(string[] args)
    {
        string url = args.Length > 0 ? args[0] : "ws://localhost:8091/game";
        string secret = args.Length > 1 ? args[1] : "testsecret";
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const ulong epoch = 7;

        // Valid credential for (identity, team=0, epoch, expiry in the future).
        string id = "0xfeedface";
        long good = now + 3600;
        string tok = JoinTokens.Compute(secret, id, 0, epoch, good);

        // 1) A valid token is accepted (Welcome v6) and the socket stays open. Hold it open so
        //    the server's epoch pin is set to `epoch` for the cross-epoch test below.
        var held = await Expect("valid token", url, id, 0, epoch, good, tok, expectAccept: true);

        // 2) Forged token (right shape, wrong MAC) is rejected.
        await Expect("forged token", url, id, 0, epoch, good, "deadbeef" + tok[8..], expectAccept: false);

        // 3) Expired token is rejected (valid MAC, expiry in the past).
        long past = now - 60;
        await Expect("expired token", url, id, 0, epoch, past,
            JoinTokens.Compute(secret, id, 0, epoch, past), expectAccept: false);

        // 4) Wrong epoch is rejected (valid MAC for a different match epoch than the pinned one).
        await Expect("wrong epoch", url, id, 0, epoch + 1, good,
            JoinTokens.Compute(secret, id, 0, epoch + 1, good), expectAccept: false);

        // 5) Tampered team is rejected (token was minted for team 0, claim team 1).
        await Expect("tampered team", url, id, 1, epoch, good, tok, expectAccept: false);

        if (held is not null)
            try { await held.CloseAsync(WebSocketCloseStatus.NormalClosure, "", default); } catch { }

        Console.WriteLine(_fail == 0 ? "authcheck: all checks passed" : $"authcheck: {_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    // Connect, send a v6 Hello, and observe whether the server accepts (Welcome) or rejects
    // (close/abort). Returns the open socket on an expected-accept so the caller can hold it.
    static async Task<ClientWebSocket?> Expect(
        string name, string url, string id, byte team, ulong matchId, long expiry, string token, bool expectAccept)
    {
        var ws = new ClientWebSocket();
        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(new Uri(url), connectCts.Token);
            await ws.SendAsync(BuildHello(0, team, id, matchId, expiry, token),
                WebSocketMessageType.Binary, true, connectCts.Token);

            var buf = new byte[2048];
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var r = await ws.ReceiveAsync(buf, readCts.Token);
            bool accepted = r.MessageType != WebSocketMessageType.Close
                            && r.Count >= 2 && buf[0] == MsgWelcome && buf[1] == ProtocolVersion;
            Report(name, expectAccept, accepted);
            if (accepted && expectAccept)
                return ws;   // keep it open (caller closes)
        }
        catch (Exception)
        {
            // Connect/read failure = the server tore the connection down = a rejection.
            Report(name, expectAccept, accepted: false);
        }
        try { ws.Dispose(); } catch { }
        return null;
    }

    static byte[] BuildHello(byte cls, byte team, string id, ulong matchId, long expiry, string token)
    {
        var idb = Encoding.UTF8.GetBytes(id);
        var tkb = Encoding.UTF8.GetBytes(token);
        var f = new byte[4 + idb.Length + 8 + 8 + 1 + tkb.Length];
        int o = 0;
        f[o++] = 1; f[o++] = cls; f[o++] = team; f[o++] = (byte)idb.Length;
        idb.CopyTo(f, o); o += idb.Length;
        BitConverter.TryWriteBytes(f.AsSpan(o), matchId); o += 8;
        BitConverter.TryWriteBytes(f.AsSpan(o), expiry); o += 8;
        f[o++] = (byte)tkb.Length;
        tkb.CopyTo(f, o);
        return f;
    }

    static void Report(string name, bool expectAccept, bool accepted)
    {
        bool ok = accepted == expectAccept;
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}: {(accepted ? "accepted" : "rejected")} " +
                          $"(expected {(expectAccept ? "accept" : "reject")})");
        if (!ok) _fail++;
    }
}
