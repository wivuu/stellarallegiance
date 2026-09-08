using System.Text.Json.Serialization;

namespace StellarAllegiance.Shared.Lobby;

// ---- Public-lobby HTTP contracts shared by all three peers ----
//
// The Godot client, the sim server (LobbyRegistrar / LobbyMatchReporter / server auth) and the
// public lobby itself compile these SAME records so the JSON shapes of the identity, join-token
// and match-ingestion routes can't drift (plan .PLAN/LobbyRankingService.md §3.1/§3.3). They live
// in shared/ (dependency-free: System.Text.Json is BCL) rather than public-lobby/ because the
// client and server must not reference a Web SDK project. Language: public-lobby/CONTEXT.md.
//
// Naming: the device-authorization records use snake_case wire names (RFC 8628 / OAuth 2.0
// field names); everything else is camelCase, the lobby's default JSON policy. Every property is
// attributed explicitly so the shape is fixed regardless of the consumer's serializer options.
//
// The listing records (ServerEntry / RegisterRequest / roster) stay in public-lobby/Contracts.cs
// for now — the client mirrors them by hand (ServerLobbyOverlay.ServerDto) and the server has its
// own DTOs; they gain verified/gameServerId/operatorName/playerId in WP1.4/3.2.

/// <summary>Values for <see cref="DeviceAuthRequest.Client"/>: what is asking to be approved.</summary>
public static class LobbyClientKind
{
    public const string Godot = "godot";
    public const string SimServer = "sim-server";
}

/// <summary>Values for <see cref="TokenSubject.Kind"/>: who a lobby session belongs to.</summary>
public static class LobbySubjectKind
{
    public const string Player = "player";
    public const string Server = "server";
}

/// <summary>OAuth grant_type values accepted by POST /auth/token.</summary>
public static class LobbyGrantType
{
    public const string DeviceCode = "urn:ietf:params:oauth:grant-type:device_code";
    public const string RefreshToken = "refresh_token";

    /// <summary>
    /// Dev-only grant (AUTH_DEV_LOGIN=true on the lobby): mints a player session for a display
    /// name with no browser step, so headless harnesses can exercise the authenticated path.
    /// Refused with unsupported_grant_type unless the env is set; never enable in production.
    /// </summary>
    public const string Dev = "dev";
}

/// <summary>RFC 8628 / RFC 6749 error codes returned by POST /auth/token as {"error": ...}.</summary>
public static class LobbyTokenError
{
    public const string AuthorizationPending = "authorization_pending";
    public const string SlowDown = "slow_down";
    public const string ExpiredToken = "expired_token";
    public const string AccessDenied = "access_denied";
    public const string InvalidGrant = "invalid_grant";
    public const string UnsupportedGrantType = "unsupported_grant_type";
}

/// <summary>Values for <see cref="MatchResultReport.EndReason"/>. Only WinCondition counts.</summary>
public static class MatchEndReason
{
    public const string WinCondition = "win-condition";
    public const string Reset = "reset";
    public const string Shutdown = "shutdown";
}

/// <summary>Wire caps shared by the lobby's validation and the client's input fields.</summary>
public static class LobbyLimits
{
    public const int DisplayNameMin = 3;
    public const int DisplayNameMax = 24;
    public const int UserCodeLength = 8;
}

// ---- POST /auth/device ----------------------------------------------------

public sealed record DeviceAuthRequest(
    [property: JsonPropertyName("client")] string Client,
    // Only for client == sim-server: the SIM_PUBLIC_NAME shown on the approval page.
    [property: JsonPropertyName("serverName")] string? ServerName = null
);

public sealed record DeviceAuthResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("verification_uri_complete")] string VerificationUriComplete,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval
);

// ---- POST /auth/token -----------------------------------------------------

// Accepted as JSON (both our peers) or application/x-www-form-urlencoded (RFC shape).
public sealed record TokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("device_code")] string? DeviceCode = null,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null,
    // LobbyGrantType.Dev only.
    [property: JsonPropertyName("display_name")] string? DisplayName = null
);

public sealed record TokenSubject(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] Guid Id,
    // Player: the display name. Server: the game server's name.
    [property: JsonPropertyName("displayName")] string DisplayName
);

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("subject")] TokenSubject Subject
)
{
    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";
}

public sealed record TokenErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription = null
);

// ---- GET/PATCH /api/me ----------------------------------------------------

public sealed record LinkedLoginDto(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("displayName")] string? DisplayName
);

public sealed record PlayerProfileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("isAdmin")] bool IsAdmin,
    [property: JsonPropertyName("logins")] LinkedLoginDto[] Logins,
    [property: JsonPropertyName("matchesPlayed")] int MatchesPlayed,
    [property: JsonPropertyName("wins")] int Wins,
    [property: JsonPropertyName("losses")] int Losses,
    [property: JsonPropertyName("kills")] int Kills,
    [property: JsonPropertyName("deaths")] int Deaths,
    [property: JsonPropertyName("ejects")] int Ejects,
    [property: JsonPropertyName("points")] long Points
);

public sealed record UpdateProfileRequest([property: JsonPropertyName("displayName")] string DisplayName);

// ---- POST /servers/{listingId}/join ------------------------------------

public sealed record JoinTokenResponse(
    [property: JsonPropertyName("joinToken")] string JoinToken,
    [property: JsonPropertyName("expiresIn")] int ExpiresIn
);

// ---- POST /matches, POST /matches/{id}/result (plan §3.3) ---------------

public sealed record MatchStartRequest(
    [property: JsonPropertyName("matchId")] Guid MatchId,
    [property: JsonPropertyName("listingId")] string ListingId,
    [property: JsonPropertyName("map")] string Map,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt
);

public sealed record MatchTeamResult(
    [property: JsonPropertyName("team")] int Team,
    [property: JsonPropertyName("garrisonsDestroyed")] int GarrisonsDestroyed,
    [property: JsonPropertyName("outpostsDestroyed")] int OutpostsDestroyed,
    [property: JsonPropertyName("score")] long Score
);

public sealed record MatchPilotResult(
    // Null for an anonymous join (never recorded against a player; only possible on a listing
    // that was unverified, whose results are refused anyway).
    [property: JsonPropertyName("playerId")] Guid? PlayerId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("team")] int Team,
    [property: JsonPropertyName("kills")] int Kills,
    [property: JsonPropertyName("deaths")] int Deaths,
    [property: JsonPropertyName("ejects")] int Ejects,
    [property: JsonPropertyName("points")] long Points,
    [property: JsonPropertyName("connectedAtEnd")] bool ConnectedAtEnd
);

public sealed record MatchResultReport(
    [property: JsonPropertyName("matchId")] Guid MatchId,
    [property: JsonPropertyName("gameServerId")] Guid GameServerId,
    [property: JsonPropertyName("listingId")] string ListingId,
    [property: JsonPropertyName("map")] string Map,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt")] DateTimeOffset EndedAt,
    // Null when no side won (reset/shutdown).
    [property: JsonPropertyName("winnerTeam")] int? WinnerTeam,
    [property: JsonPropertyName("endReason")] string EndReason,
    [property: JsonPropertyName("teams")] MatchTeamResult[] Teams,
    [property: JsonPropertyName("pilots")] MatchPilotResult[] Pilots
);
