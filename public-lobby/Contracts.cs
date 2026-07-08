namespace PublicLobby;

// ---- Wire contracts (JSON) shared by the registry + signaling routes ----

// A host announcing itself. Name (3-50 chars, validated in ServerRegistry) and Port (the
// public-facing port to probe/advertise) are required. PublicEndpoint is an OPTIONAL host:port the
// server asserts as its reachable address (e.g. its host LAN/public address when it sits behind
// container NAT or a proxy); the lobby probes it and advertises it only if it answers /health (see
// ReachabilityProbe), so a server can't simply CLAIM to be directly joinable. When empty the lobby
// probes the request's source IP instead. IceCandidates is legacy/unused.
// Players/MaxPlayers/State seed the live status fields the browser shows (also refreshed by the
// heartbeat) — current player count, capacity, and "lobby"/"in-progress"/"ended".
// ProtocolVersion is the server's wire-protocol version (server/Net/Protocol.cs); clients filter the
// browser list to their own protocol so they only see servers they can actually handshake with. 0 =
// unspecified (a legacy server that predates this field) — those match no real client filter.
public record RegisterRequest(
    string Name,
    int Port,
    string? PublicEndpoint,
    string[]? IceCandidates,
    int Players = 0,
    int MaxPlayers = 0,
    string? State = null,
    int ProtocolVersion = 0,
    string? HostedBy = null,
    string? MapName = null,
    MapLayout? Map = null,
    LobbyRosterEntry[]? Roster = null
);

// Periodic liveness ping. Carries the current player count, capacity, and game state so the
// browser list stays fresh between (re)registrations. All optional — a body-less ping just
// refreshes LastSeen. Roster is null when unchanged/unsupported (keep the stored one); an
// empty array explicitly clears it.
public record HeartbeatRequest(
    int Players = 0,
    int MaxPlayers = 0,
    string? State = null,
    LobbyRosterEntry[]? Roster = null
);

// One player on a registered server, as shown in the server-browser detail panel. Team is the
// side index (0/1); Flying means the player has an active ship (vs waiting in the lobby).
public record LobbyRosterEntry(string Name, int Team, bool Ready = false, bool Flying = false);

// Compact sector-graph layout of a server's map — enough for the browser's sector-map preview
// (sector circles, team base positions, aleph gates), deliberately excluding asteroids or any
// other bulk geometry. Coordinates are world X/Z within the owning sector.
// Only the owning team of a sector's garrison — the sector-local position is intentionally not
// carried (randomized per match, secret; the browser highlights the whole sector by team).
public record MapBase(int Team);

public record MapGate(uint ToSector, float X, float Z);

// MapX/MapY (valid when HasMapPos) is the authored 2D diagram position — same field the game
// lobby carries — so the browser preview lays sectors out by their star/custom silhouette rather
// than the horizontal-row fallback. Optional/defaulted: servers predating it still deserialize.
public record MapSector(
    uint Id, float Radius, MapBase[]? Bases = null, MapGate[]? Gates = null, string? Name = null,
    float MapX = 0f, float MapY = 0f, bool HasMapPos = false);

public record MapLayout(MapSector[] Sectors);

// What the registry stores and hands back. IceServers is the STUN/TURN config this box owns
// (from its env) so every client + game server gets one consistent ICE configuration to dial.
// Players/MaxPlayers/State are the live status the browser renders as "(players/max) · state".
public record ServerEntry(
    string SessionId,
    string Name,
    string? PublicEndpoint,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeen,
    IReadOnlyList<IceServer> IceServers,
    int Players = 0,
    int MaxPlayers = 0,
    string? State = null,
    int ProtocolVersion = 0,
    string? HostedBy = null,
    string? MapName = null,
    MapLayout? Map = null,
    IReadOnlyList<LobbyRosterEntry>? Roster = null
);

// A single ICE server entry, mirroring the WebRTC RTCIceServer shape. Urls is one or more
// stun:/turn: URLs; Username/Credential are set only for TURN.
public record IceServer(string[] Urls, string? Username = null, string? Credential = null);

// Returned ONLY in the direct POST /servers response. Secret is the per-session capability the
// registrant must echo to mutate or close its listing (WS auth frame + graceful DELETE); the lobby
// validates it with a constant-time compare. It is never broadcast over SSE / GET /servers, so a
// client reading the public server list can't manipulate or delete a listing it didn't register.
public record RegisterResponse(ServerEntry Server, string Secret);

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
