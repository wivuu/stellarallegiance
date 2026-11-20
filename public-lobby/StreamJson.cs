using System.Text.Json.Serialization;

namespace PublicLobby;

// Source-generated JSON for the lobby's long-lived streams: the SSE feeds (/servers/events,
// /servers/live) and the game-server socket (/servers/ws). They serialize on every event for as
// long as a stream stays open, and reflection-based serialization builds its accessors at run time
// (Reflection.Emit plus JIT), while the lobby's memory is mostly JIT code and runtime data, not
// heap. Every shape here is byte-identical to the reflection output it replaced (pinned by
// tests/PublicLobbyTest/StreamJsonTests.cs); change one and that test says what moved.

// SSE payloads, camelCase like the rest of the lobby's JSON.
public sealed record SseRelease(string? Version);

public sealed record SseRemoved(string? SessionId);

// Outbound /servers/ws frames. Member order is the JSON order older servers bind, and camelCase
// turns these into exactly the lowercase names of the anonymous objects they replaced.
public sealed record WsOkFrame(string Type);

public sealed record WsOfferFrame(string Type, string Ticket, string SdpOffer);

public sealed record WsReleaseFrame(string Type, string Version);

// Inbound /servers/ws frames from the game server. Secret is the per-session capability from
// registration. Roster entries carry `playerId` (nullable; WP1.4) via LobbyRosterEntry itself.
public sealed record WsAuthMsg(string? Type, string? SessionId, string? Secret);

public sealed record WsServerMsg(
    string? Type,
    int Players = 0,
    int MaxPlayers = 0,
    string? State = null,
    LobbyRosterEntry[]? Roster = null
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyCollection<ServerEntry>))]
[JsonSerializable(typeof(ServerEntry))]
[JsonSerializable(typeof(PublicServerStrip))]
[JsonSerializable(typeof(SseRelease))]
[JsonSerializable(typeof(SseRemoved))]
[JsonSerializable(typeof(WsOkFrame))]
[JsonSerializable(typeof(WsOfferFrame))]
[JsonSerializable(typeof(WsReleaseFrame))]
public partial class LobbyStreamJson : JsonSerializerContext;

// Game servers have always been read case-insensitively (their frames are PascalCase records).
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WsAuthMsg))]
[JsonSerializable(typeof(WsServerMsg))]
public partial class LobbyWsInboundJson : JsonSerializerContext;
