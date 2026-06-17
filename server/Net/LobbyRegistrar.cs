using System.Net;
using System.Net.Http.Json;
using SIPSorcery.Net;

namespace SimServer.Net;

// Publishes this game server to the public lobby (ServerShare) so clients can discover and join
// it through WebRTC — the opt-in path for player-run servers behind a NAT. Opt-in BY NAME: it
// only registers when SIM_PUBLIC_NAME (3-50 chars) is set; with no name the server stays private
// (direct ws://host:8090 only) and this whole subsystem is dormant.
//
// On register it gets a SessionId + the lobby's ICE config, starts a WebRtcListener for that
// session, then heartbeats so the entry stays live (registry TTL is 30 s). If the lobby forgets
// us (restart -> 404 on heartbeat) it re-registers and relaunches the listener. On shutdown it
// deletes the entry for a clean disappearance.
//
// Env:
//   PUBLIC_LOBBY          ServerShare host:port (default 192.168.1.101:8091)
//   SIM_PUBLIC_NAME       3-50 char public name; gates registration
//   SIM_PUBLIC_ENDPOINT   optional direct ws:// fallback host:port advertised in the listing
public sealed class LobbyRegistrar
{
    public const string DefaultLobby = "192.168.1.101:8091";
    static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(10);

    private readonly ClientHub _hub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _shareBase;     // http://host:port
    private readonly string _name;
    private readonly string? _publicEndpoint;

    private string? _sessionId;
    private CancellationTokenSource? _listenerCts;

    private LobbyRegistrar(ClientHub hub, string shareBase, string name, string? publicEndpoint)
    {
        _hub = hub;
        _shareBase = shareBase;
        _name = name;
        _publicEndpoint = publicEndpoint;
    }

    // Builds a registrar from the environment, or returns null when no public name is set
    // (the server stays private). Logs the decision either way.
    public static LobbyRegistrar? FromEnv(ClientHub hub)
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

        Console.WriteLine($"[Lobby] publishing \"{name}\" to {shareBase}");
        return new LobbyRegistrar(hub, shareBase, name, endpoint.Length == 0 ? null : endpoint);
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
                new { name = _name, publicEndpoint = _publicEndpoint }, ct);
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
            var ice = ToIceServers(entry.IceServers);
            _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            new WebRtcListener(_hub, _shareBase, _sessionId, ice).Start(_listenerCts.Token);
            Console.WriteLine($"[Lobby] registered session {_sessionId} ({ice.Count} ICE server(s)).");
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

    // ServerShare's register-response JSON (camelCase; web JSON defaults are case-insensitive).
    private sealed record RegisterResponseDto(string SessionId, IReadOnlyList<IceServerDto>? IceServers);
    private sealed record IceServerDto(string[]? Urls, string? Username, string? Credential);
}
