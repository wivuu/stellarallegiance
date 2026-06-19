using System.Net;
using System.Net.Http.Json;
using SIPSorcery.Net;

namespace SimServer.Net;

// Publishes this game server to the public lobby (public lobby) so clients can discover and join
// it through WebRTC — the opt-in path for player-run servers behind a NAT. Opt-in BY NAME: it
// only registers when SIM_PUBLIC_NAME (3-50 chars) is set; with no name the server stays private
// (direct ws://host:8090 only) and this whole subsystem is dormant.
//
// On register the lobby PROBES our port back and tells us our mode: if our port is reachable it
// records a direct host:port (clients connect straight to us over WebSocket and we do nothing
// extra); if not, we're a NAT'd server and start a WebRtcListener so clients can join via the
// relayed SDP handshake + STUN. Then we heartbeat to stay live (registry TTL is 30 s); if the
// lobby forgets us (restart -> 404) we re-register (which re-probes). On shutdown we delete the
// entry for a clean disappearance.
//
// Env:
//   PUBLIC_LOBBY          public-lobby host:port (default 192.168.1.101:8091)
//   SIM_PUBLIC_NAME       3-50 char public name; gates registration
//   SIM_PUBLIC_PORT       public-facing port to advertise/probe (default = the listen port; set
//                         when a port-forward maps a different external port)
//   SIM_PUBLIC_ENDPOINT   optional host:port we assert as our reachable address (e.g. the host's
//                         LAN/public address when we're behind container NAT or a proxy); the lobby
//                         probes it and advertises it only if it answers /health
public sealed class LobbyRegistrar
{
    public const string DefaultLobby = "192.168.1.101:8091";
    static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(10);

    private readonly ClientHub _hub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _shareBase;     // http://host:port
    private readonly string _name;
    private readonly int _port;             // public-facing port the lobby probes/advertises
    private readonly string? _publicEndpoint;

    private string? _sessionId;
    private CancellationTokenSource? _listenerCts;

    private LobbyRegistrar(ClientHub hub, string shareBase, string name, int port, string? publicEndpoint)
    {
        _hub = hub;
        _shareBase = shareBase;
        _name = name;
        _port = port;
        _publicEndpoint = publicEndpoint;
    }

    // Builds a registrar from the environment, or returns null when no public name is set
    // (the server stays private). Logs the decision either way.
    public static LobbyRegistrar? FromEnv(ClientHub hub, int listenPort)
    {
        var name = (Environment.GetEnvironmentVariable("SIM_PUBLIC_NAME") ?? "").Trim();
        if (name.Length == 0)
            return null;   // private: not published to any lobby
        if (name.Length is < 3 or > 50)
        {
            Console.WriteLine($"[Lobby] SIM_PUBLIC_NAME must be 3-50 chars (got {name.Length}); staying private.");
            return null;
        }

        var lobby = (Environment.GetEnvironmentVariable("PUBLIC_LOBBY") ?? "").Trim();
        if (lobby.Length == 0) lobby = DefaultLobby;
        var shareBase = lobby.StartsWith("http") ? lobby.TrimEnd('/') : $"http://{lobby}";
        var endpoint = (Environment.GetEnvironmentVariable("SIM_PUBLIC_ENDPOINT") ?? "").Trim();

        // Advertise/probe the public-facing port — usually the listen port, but a port-forward may
        // map a different external port, so allow an override.
        var port = int.TryParse(Environment.GetEnvironmentVariable("SIM_PUBLIC_PORT"), out var pp) && pp is > 0 and <= 65535
            ? pp : listenPort;

        Console.WriteLine($"[Lobby] publishing \"{name}\" to {shareBase} (port {port})");
        return new LobbyRegistrar(hub, shareBase, name, port, endpoint.Length == 0 ? null : endpoint);
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => RunAsync(ct), ct);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!await RegisterAndListen(ct)) return;

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(HeartbeatEvery, ct); }
                catch (OperationCanceledException) { break; }

                var ok = await Heartbeat(ct);
                if (!ok && !ct.IsCancellationRequested)
                {
                    Console.WriteLine("[Lobby] heartbeat lost (lobby forgot us?); re-registering.");
                    _listenerCts?.Cancel();
                    await RegisterAndListen(ct);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally { await Deregister(); }
    }

    private async Task<bool> RegisterAndListen(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync($"{_shareBase}/servers",
                new { name = _name, port = _port, publicEndpoint = _publicEndpoint }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Lobby] register failed ({(int)resp.StatusCode}).");
                return false;
            }

            var entry = await resp.Content.ReadFromJsonAsync<RegisterResponseDto>(ct);
            if (entry is null || string.IsNullOrEmpty(entry.SessionId))
            {
                Console.WriteLine("[Lobby] register returned no session id.");
                return false;
            }

            _sessionId = entry.SessionId;

            // The lobby decided our mode from a reachability probe. A non-empty PublicEndpoint means
            // we're directly joinable over WebSocket — nothing more to do here. Otherwise we're
            // behind a NAT, so accept WebRTC joins relayed through the lobby (public STUN only).
            if (!string.IsNullOrEmpty(entry.PublicEndpoint))
            {
                Console.WriteLine($"[Lobby] registered session {_sessionId} — DIRECT at {entry.PublicEndpoint} (no WebRTC listener).");
            }
            else
            {
                var ice = ToIceServers(entry.IceServers);
                _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                new WebRtcListener(_hub, _shareBase, _sessionId, ice).Start(_listenerCts.Token);
                Console.WriteLine($"[Lobby] registered session {_sessionId} — STUN/WebRTC ({ice.Count} ICE server(s)).");
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Console.WriteLine($"[Lobby] register error: {e.Message}");
            return false;
        }
    }

    private async Task<bool> Heartbeat(CancellationToken ct)
    {
        if (_sessionId is null) return false;
        try
        {
            using var resp = await _http.PostAsync($"{_shareBase}/servers/{_sessionId}/heartbeat", null, ct);
            return resp.StatusCode != HttpStatusCode.NotFound && resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task Deregister()
    {
        if (_sessionId is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _http.DeleteAsync($"{_shareBase}/servers/{_sessionId}", cts.Token);
            Console.WriteLine($"[Lobby] deregistered session {_sessionId}.");
        }
        catch { /* best effort on shutdown */ }
    }

    private static List<RTCIceServer> ToIceServers(IReadOnlyList<IceServerDto>? dtos)
    {
        var list = new List<RTCIceServer>();
        if (dtos is null) return list;
        foreach (var d in dtos)
        {
            if (d.Urls is null || d.Urls.Length == 0) continue;
            list.Add(new RTCIceServer
            {
                urls = string.Join(',', d.Urls),
                username = d.Username,
                credential = d.Credential,
            });
        }
        return list;
    }

    // the public lobby's register-response JSON (camelCase; web JSON defaults are case-insensitive).
    // PublicEndpoint is set by the lobby's reachability probe (direct mode) or null (WebRTC mode).
    private sealed record RegisterResponseDto(string SessionId, string? PublicEndpoint, IReadOnlyList<IceServerDto>? IceServers);
    private sealed record IceServerDto(string[]? Urls, string? Username, string? Credential);
}
