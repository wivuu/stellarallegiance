namespace ServerShare;

// ---- Wire contracts (JSON) shared by the registry + signaling routes ----

// A host announcing itself. Name (3-50 chars, validated in ServerRegistry) and Port (the
// public-facing port to probe/advertise) are required. PublicEndpoint is an OPTIONAL host:port the
// server asserts as its reachable address (e.g. its host LAN/public address when it sits behind
// container NAT or a proxy); the lobby probes it and advertises it only if it answers /health (see
// ReachabilityProbe), so a server can't simply CLAIM to be directly joinable. When empty the lobby
// probes the request's source IP instead. IceCandidates is legacy/unused.
public record RegisterRequest(string Name, int Port, string? PublicEndpoint, string[]? IceCandidates);

// What the registry stores and hands back. IceServers is the STUN/TURN config this box owns
// (from its env) so every client + game server gets one consistent ICE configuration to dial.
public record ServerEntry(
    string SessionId,
    string Name,
    string? PublicEndpoint,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeen,
    IReadOnlyList<IceServer> IceServers);

// A single ICE server entry, mirroring the WebRTC RTCIceServer shape. Urls is one or more
// stun:/turn: URLs; Username/Credential are set only for TURN.
public record IceServer(string[] Urls, string? Username = null, string? Credential = null);

// ---- Signaling (WebRTC SDP relay) ----

// A joining client posts its SDP offer for a given server; the relay returns a Ticket the client
// then polls for the answer, and the game server long-polls /pending to pick the offer up.
public record OfferRequest(string SdpOffer);
public record OfferResponse(string Ticket);

// One pending offer handed to the game server's /pending long-poll.
public record PendingOffer(string Ticket, string SdpOffer);

// The game server posts its SDP answer for a ticket; the client polls /answer to receive it.
public record AnswerRequest(string SdpAnswer);
public record AnswerResponse(string SdpAnswer);
