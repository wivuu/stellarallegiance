using System.Text.Json;
using System.Text.Json.Serialization;
using StellarAllegiance.Shared.Lobby;

namespace SimServer.Net;

// Source-generated System.Text.Json metadata for EVERY JSON shape the server reads or writes. The
// server publishes as NativeAOT (see SimServer.csproj), where reflection-based serialization does
// not exist: a JsonSerializer / HttpClient-JSON call WITHOUT one of these contexts is a build error
// (IL2026/IL3050), and an anonymous type can never be serialized. A new JSON shape = a record + one
// [JsonSerializable] line here.
//
// Two contexts because the two transports always had different options, and the bytes on the wire
// must not change (the deployed public lobby talks to old and new servers alike):
//   * LobbyHttpJson - HTTP bodies and the two on-disk files. JsonSerializerDefaults.Web is what
//                     System.Net.Http.Json (PostAsJsonAsync / JsonContent.Create / ReadFromJsonAsync)
//                     used implicitly: camelCase names, case-insensitive reads, numbers from strings.
//   * LobbyWsJson   - the /servers/ws text frames. Those were written with DEFAULT options, so a
//                     member without an explicit name keeps its declared (PascalCase) name - that is
//                     why the roster is PascalCase in a WS "update" but camelCase in the HTTP
//                     register body. Reads were, and stay, case-insensitive.
// tests/LobbyTest/WireJsonTests.cs pins the exact strings.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DeviceAuthRequest))]
[JsonSerializable(typeof(DeviceAuthResponse))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(TokenErrorResponse))]
[JsonSerializable(typeof(MatchStartRequest))]
[JsonSerializable(typeof(MatchResultReport))]
[JsonSerializable(typeof(RegisterRequestDto))]
[JsonSerializable(typeof(RegisterResponseDto))]
[JsonSerializable(typeof(WebRtcAnswerDto))]
[JsonSerializable(typeof(LobbyCredential))]
[JsonSerializable(typeof(LobbyMatchReporter.SpoolItem))]
public sealed partial class LobbyHttpJson : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WsAuthMsg))]
[JsonSerializable(typeof(WsUpdateMsg))]
[JsonSerializable(typeof(WsPingMsg))]
[JsonSerializable(typeof(WsReplyDto))]
[JsonSerializable(typeof(WsOfferMsg))]
public sealed partial class LobbyWsJson : JsonSerializerContext;

// ---- POST /servers (register / re-register). Member ORDER is the wire order - keep it. ----

public sealed record RegisterRequestDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("publicEndpoint")] string? PublicEndpoint,
    [property: JsonPropertyName("players")] int Players,
    [property: JsonPropertyName("maxPlayers")] int MaxPlayers,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("roster")] List<LobbyStatus.RosterDto> Roster,
    [property: JsonPropertyName("protected")] bool Protected
);

// Public lobby register-response JSON (camelCase; web JSON defaults are case-insensitive).
// Secret is the per-session capability, disclosed only here, that we echo to mutate/close our
// listing. Server holds only the fields we actually consume.
public sealed record RegisterResponseDto(ServerEntryDto? Server, string? Secret);

public sealed record ServerEntryDto(string SessionId, string? PublicEndpoint, IReadOnlyList<IceServerDto>? IceServers);

public sealed record IceServerDto(string[]? Urls, string? Username, string? Credential);

// ---- POST /connect/{ticket}/answer (WebRtcListener) ----

public sealed record WebRtcAnswerDto([property: JsonPropertyName("sdpAnswer")] string SdpAnswer);

// ---- /servers/ws text frames ----

public sealed record WsAuthMsg(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("secret")] string? Secret
);

public sealed record WsUpdateMsg(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("players")] int Players,
    [property: JsonPropertyName("maxPlayers")] int MaxPlayers,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("roster")] List<LobbyStatus.RosterDto> Roster
);

public sealed record WsPingMsg([property: JsonPropertyName("type")] string Type);

public sealed record WsReplyDto(string? Type, string? Message);

public sealed record WsOfferMsg(string? Type, string? Ticket, string? SdpOffer);
