using System.Net;
using System.Net.Sockets;

namespace PublicLobby;

// Decides whether a registering game server is DIRECTLY joinable (has a reachable public WebSocket
// endpoint) or must fall back to WebRTC + STUN. The lobby is a public vantage point, so a probe
// from here reflects real internet reachability: we GET <scheme>://<host>:<port>/health and accept
// it as direct only if it answers with the sim server's token.
//
// Two shapes of reachable endpoint:
//   - bare host:port (home / port-forward) -> probe http, advertise "host:port" (client dials ws://)
//   - https:// / wss:// (a PaaS HTTPS edge, e.g. Railway's *.up.railway.app on 443, no raw port)
//     -> probe https on 443, advertise "wss://host" (client dials wss://host/game)
//
// SSRF guard: by default we probe the request's OWN source IP. A server may instead assert an
// explicit endpoint (SIM_PUBLIC_ENDPOINT — e.g. its host LAN/public address behind container NAT /
// a proxy, or its PaaS https domain); that's probed directly, but the probe only ever SUCCEEDS when
// the target answers /health with the sim server's token, so it can't be used to fingerprint
// arbitrary internal services. We also use the fixed /health path, a short timeout, and refuse
// link-local IP targets (e.g. cloud metadata at 169.254.x). Residual risk (unchanged): an asserted
// DOMAIN could resolve to an internal address, but only a token-answering target is ever advertised.
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
        bool secure = false;   // probe over HTTPS and advertise wss:// (a PaaS HTTPS edge)

        // An operator may assert an explicit endpoint (SIM_PUBLIC_ENDPOINT): the address clients
        // should actually use, e.g. the host's LAN/public address behind container NAT / a reverse
        // proxy, or a PaaS https domain. We probe it directly and advertise it only if /health
        // answers with the token (see IsReachableAsync). Otherwise we fall back to the source IP.
        if (!string.IsNullOrWhiteSpace(providedEndpoint))
        {
            var (h, p, isTls) = ParseEndpoint(providedEndpoint);
            if (h is null) return null;
            secure = isTls;
            host = NormalizeIp(h);
            if (p is > 0 and <= 65535) probePort = p;
            else if (isTls) probePort = 443;   // default HTTPS port when none is given
        }
        else
        {
            host = NormalizeIp(sourceIp);
        }

        if (probePort is < 1 or > 65535) return null;
        if (IsLinkLocal(host)) return null;

        if (!await IsReachableAsync(secure, host, probePort, ct))
            return null;

        // Advertise what the client should dial: wss://host for a TLS edge (ToWsUrl appends /game),
        // else the bare host:port (the client turns it into ws://host:port/game).
        if (secure)
            return probePort == 443 ? $"wss://{host}" : $"wss://{host}:{probePort}";
        return $"{host}:{probePort}";
    }

    async Task<bool> IsReachableAsync(bool secure, string host, int port, CancellationToken ct)
    {
        try
        {
            var scheme = secure ? "https" : "http";
            var authority = host.Contains(':') ? $"[{host}]" : host;   // bracket bare IPv6
            using var resp = await _http.GetAsync($"{scheme}://{authority}:{port}/health", ct);
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

    // Splits an asserted endpoint into (host, port, secure). Understands an optional scheme:
    // https:// or wss:// -> secure (probe HTTPS, advertise wss://); http:// / ws:// / none -> plain.
    // Port 0 means "unspecified" (the caller defaults it).
    static (string? Host, int Port, bool Secure) ParseEndpoint(string endpoint)
    {
        endpoint = endpoint.Trim();
        bool secure = false;
        foreach (var (prefix, tls) in new[] { ("https://", true), ("wss://", true), ("http://", false), ("ws://", false) })
        {
            if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                secure = tls;
                endpoint = endpoint[prefix.Length..];
                break;
            }
        }
        endpoint = endpoint.TrimEnd('/');
        int i = endpoint.LastIndexOf(':');
        if (i > 0 && int.TryParse(endpoint.AsSpan(i + 1), out var p))
            return (endpoint[..i], p, secure);
        return (endpoint.Length == 0 ? null : endpoint, 0, secure);
    }
}
