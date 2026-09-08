using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Data;

// Status/kind columns (plan .PLAN/LobbyRankingService.md §3.4) are C# enums persisted as the
// exact lowercase literal text the plan and shared/Lobby/LobbyContracts.cs use elsewhere, so a
// hand-written SQL view can compare a column against a string constant. Where a shared wire
// constant already exists (LobbySubjectKind, MatchEndReason) the map below points at it instead
// of re-typing the literal, per WP0.1 "do NOT duplicate them".

/// <summary>Who a sessions/device_codes row belongs to. Mirrors <see cref="LobbySubjectKind"/>.</summary>
public enum SubjectKind
{
    Player,
    Server,
}

/// <summary>device_codes.status: RFC 8628 approval lifecycle.</summary>
public enum DeviceCodeStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
}

/// <summary>matches.status: plan §1.3 match lifecycle (counting happens only for Ended+WinCondition).</summary>
public enum MatchStatus
{
    Active,
    Ended,
    Abandoned,
}

/// <summary>matches.end_reason. Mirrors <see cref="MatchEndReason"/>; only WinCondition counts.</summary>
public enum MatchEndReasonKind
{
    WinCondition,
    Reset,
    Shutdown,
}

// Generic enum<->text converter driven by an explicit literal map, rather than a naive
// enum.ToString().ToLowerInvariant() — MatchEndReason.WinCondition is "win-condition" (a hyphen),
// which no CLR enum member name can spell. One small class, reused by every status/kind property
// in LobbyDbContext.OnModelCreating.
internal sealed class EnumTextConverter<TEnum>(IReadOnlyDictionary<TEnum, string> textByValue)
    : ValueConverter<TEnum, string>(e => textByValue[e], s => FindEnum(textByValue, s))
    where TEnum : struct, Enum
{
    internal static TEnum FindEnum(IReadOnlyDictionary<TEnum, string> map, string text)
    {
        foreach (var (value, literal) in map)
        {
            if (literal == text)
                return value;
        }
        throw new ArgumentOutOfRangeException(nameof(text), text, $"no {typeof(TEnum).Name} maps to '{text}'");
    }
}

// Nullable counterpart for the one optional status/kind column (matches.end_reason: null while a
// match is still Active, or when it ended without a winner).
internal sealed class NullableEnumTextConverter<TEnum>(IReadOnlyDictionary<TEnum, string> textByValue)
    : ValueConverter<TEnum?, string?>(
        e => e.HasValue ? textByValue[e.Value] : null,
        s => s == null ? null : EnumTextConverter<TEnum>.FindEnum(textByValue, s)
    )
    where TEnum : struct, Enum { }

// Field names deliberately differ from the enum type names above (SubjectKindText, not
// SubjectKind) to avoid a "color color" shadowing ambiguity between the type and the member.
internal static class EnumTextMaps
{
    public static readonly IReadOnlyDictionary<SubjectKind, string> SubjectKindText = new Dictionary<SubjectKind, string>
    {
        [SubjectKind.Player] = LobbySubjectKind.Player,
        [SubjectKind.Server] = LobbySubjectKind.Server,
    };

    public static readonly IReadOnlyDictionary<DeviceCodeStatus, string> DeviceCodeStatusText = new Dictionary<
        DeviceCodeStatus,
        string
    >
    {
        [DeviceCodeStatus.Pending] = "pending",
        [DeviceCodeStatus.Approved] = "approved",
        [DeviceCodeStatus.Denied] = "denied",
        [DeviceCodeStatus.Expired] = "expired",
        [DeviceCodeStatus.Consumed] = "consumed",
    };

    public static readonly IReadOnlyDictionary<MatchStatus, string> MatchStatusText = new Dictionary<MatchStatus, string>
    {
        [MatchStatus.Active] = "active",
        [MatchStatus.Ended] = "ended",
        [MatchStatus.Abandoned] = "abandoned",
    };

    public static readonly IReadOnlyDictionary<MatchEndReasonKind, string> MatchEndReasonText = new Dictionary<
        MatchEndReasonKind,
        string
    >
    {
        [MatchEndReasonKind.WinCondition] = MatchEndReason.WinCondition,
        [MatchEndReasonKind.Reset] = MatchEndReason.Reset,
        [MatchEndReasonKind.Shutdown] = MatchEndReason.Shutdown,
    };
}
