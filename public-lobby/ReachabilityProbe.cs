using System.Net;
using System.Net.Sockets;

namespace PublicLobby;

// Decides whether a registering game server is DIRECTLY joinable (has a reachable public WebSocket
// port) or must fall back to WebRTC + STUN. The lobby is a public vantage point, so a probe from
// here reflects real internet reachability: we GET http://<host>:<port>/health and accept it as
// direct only if it answers with the sim server's token.
//
// SSRF guard: by default we probe the request's OWN source IP. A server may instead assert an
// explicit endpoint (SIM_PUBLIC_ENDPOINT — e.g. its host LAN/public address when it sits behind
// container NAT or a proxy); that's probed directly, but the probe only ever SUCCEEDS when the
// target answers /health with the sim server's token, so it can't be used to fingerprint arbitrary
// internal services. We also use http + the fixed /health path, a short timeout, and refuse
// link-local targets (e.g. cloud metadata at 169.254.x).
public sealed class ReachabilityProbe
{
    const string ExpectedToken = "wivuu-sim";
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    readonly HttpClient _http = new() { Timeout = ProbeTimeout };

    // Returns the host:port to advertise for a direct join, or null if the server isn't reachable
    // from here (caller then registers it as a WebRTC/STUN server).
    public async Task<string?> ResolveAsync(string? sourceIp, int port, string? providedEndpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceIp) || port is < 1 or > 65535)
            return null;

        string host;
        int probePort = port;

        // An operator may assert an explicit endpoint (SIM_PUBLIC_ENDPOINT): the address clients
        // should actually use, e.g. the host's LAN/public address when the server sits behind
        // container NAT or a reverse proxy and its source IP isn't reachable. We probe it directly
        // and advertise it only if /health answers with the token (see IsReachableAsync). Otherwise
        // we fall back to the request's own source IP.
        if (!string.IsNullOrWhiteSpace(providedEndpoint))
        {
            var (h, p) = ParseHostPort(providedEndpoint);
            if (h is null) return null;
            host = NormalizeIp(h);
            if (p is > 0 and <= 65535) probePort = p;
        }
        else
        {
            host = NormalizeIp(sourceIp);
        }

        if (IsLinkLocal(host))
            return null;

        return await IsReachableAsync(host, probePort, ct) ? $"{host}:{probePort}" : null;
    }

    async Task<bool> IsReachableAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            var authority = host.Contains(':') ? $"[{host}]" : host;   // bracket bare IPv6
            using var resp = await _http.GetAsync($"http://{authority}:{port}/health", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return body.Contains(ExpectedToken, StringComparison.Ordinal);
        }
        catch { return false; }   // unreachable / timeout / wrong service -> not direct
    }

    static string NormalizeIp(string ip)
    {
        // X-Forwarded-For / RemoteIpAddress can hand us IPv4-mapped IPv6 (::ffff:1.2.3.4).
        return IPAddress.TryParse(ip, out var a) && a.IsIPv4MappedToIPv6
            ? a.MapToIPv4().ToString()
            : ip;
    }

    static bool IsLinkLocal(string host) =>
        IPAddress.TryParse(host, out var a) &&
        (a.IsIPv6LinkLocal ||
         (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..]));

    static (string? Host, int Port) ParseHostPort(string endpoint)
    {
        endpoint = endpoint.Trim();
        int i = endpoint.LastIndexOf(':');
        if (i > 0 && int.TryParse(endpoint.AsSpan(i + 1), out var p))
            return (endpoint[..i], p);
        return (endpoint, 0);
    }
}
