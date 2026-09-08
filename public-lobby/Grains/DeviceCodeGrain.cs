using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Grains;

// One RFC 8628 device-authorization attempt (CONTEXT.md "Device Code"), keyed by the long
// device_code the polling peer holds. Single writer of its `device_codes` row (ADR-0002). The
// same flow serves a Godot client (SubjectKind.Player → approved subject = the approving player)
// and a game server (SubjectKind.Server → approval MINTS the durable GameServer owned by the
// approving player, plan §1.2). Approval is single use: the first successful poll consumes it.
public interface IDeviceCodeGrain : IGrainWithStringKey
{
    /// <summary>Open the attempt; returns the 8-char user code the human confirms in the browser.</summary>
    Task<string> Start(SubjectKind kind, string? serverName, DateTimeOffset now);

    /// <summary>POST /auth/token poll. Approved is returned exactly once.</summary>
    Task<DevicePollResult> Poll(DateTimeOffset now);

    [ReadOnly]
    Task<DeviceCodeView?> Describe(DateTimeOffset now);

    /// <summary>The signed-in player approves. False when the code is not pending any more.</summary>
    Task<bool> Approve(Guid playerId, DateTimeOffset now);

    Task<bool> Deny(Guid playerId, DateTimeOffset now);
}

// Codes live 10 minutes, so an activation has nothing to do after ~12; and a poll for a code that
// never existed must not keep a row-less activation resident (see Poll).
[CollectionAgeLimit(Minutes = 12)]
public sealed class DeviceCodeGrain(IDbContextFactory<LobbyDbContext> dbFactory) : Grain, IDeviceCodeGrain
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public const int PollIntervalSeconds = 5;

    // 20 consonants: no vowels, so an 8-char code never spells a word, and no I/O/U/Y/A/E to
    // confuse with digits or each other when read aloud.
    const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    DeviceCode? _row;
    DateTimeOffset? _lastPolledAt; // in-memory only; a lost value merely allows one early poll

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var code = this.GetPrimaryKeyString();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _row = await db.DeviceCodes.AsNoTracking().SingleOrDefaultAsync(d => d.Code == code, cancellationToken);
        _lastPolledAt = _row?.LastPolledAt;
    }

    public async Task<string> Start(SubjectKind kind, string? serverName, DateTimeOffset now)
    {
        if (_row is not null)
            throw new InvalidOperationException("device code already started");
        if (kind == SubjectKind.Server && InMemoryServerRegistry.NormalizeName(serverName) is null)
            throw new ArgumentException("server name required", nameof(serverName));

        // Retry a user-code collision (20^8 space; the unique index is the arbiter).
        for (var attempt = 0; ; attempt++)
        {
            var row = new DeviceCode
            {
                Code = this.GetPrimaryKeyString(),
                UserCode = MintUserCode(),
                SubjectKind = kind,
                RequestedServerName = kind == SubjectKind.Server ? InMemoryServerRegistry.NormalizeName(serverName) : null,
                Status = DeviceCodeStatus.Pending,
                CreatedAt = now,
                ExpiresAt = now + Lifetime,
            };
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                db.DeviceCodes.Add(row);
                await db.SaveChangesAsync();
                _row = row;
                return row.UserCode;
            }
            catch (DbUpdateException e) when (PostgresErrors.IsUniqueViolation(e) && attempt < 3)
            {
                // fall through and mint another user code
            }
        }
    }

    public async Task<DevicePollResult> Poll(DateTimeOffset now)
    {
        if (_row is null)
        {
            DeactivateOnIdle();
            return new DevicePollResult(DevicePollOutcome.Invalid, SubjectKind.Player, null);
        }
        await ExpireIfDue(now);
        switch (_row.Status)
        {
            case DeviceCodeStatus.Pending:
                if (_lastPolledAt is { } last && now - last < PollInterval)
                    return new DevicePollResult(DevicePollOutcome.SlowDown, _row.SubjectKind, null);
                _lastPolledAt = now;
                return new DevicePollResult(DevicePollOutcome.Pending, _row.SubjectKind, null);
            case DeviceCodeStatus.Approved:
                // Single use: hand the subject out once, then the code is spent.
                await Persist(r =>
                {
                    r.Status = DeviceCodeStatus.Consumed;
                    r.LastPolledAt = now;
                });
                return new DevicePollResult(DevicePollOutcome.Approved, _row.SubjectKind, _row.ApprovedSubjectId);
            case DeviceCodeStatus.Denied:
                return new DevicePollResult(DevicePollOutcome.Denied, _row.SubjectKind, null);
            case DeviceCodeStatus.Expired:
                return new DevicePollResult(DevicePollOutcome.Expired, _row.SubjectKind, null);
            default:
                return new DevicePollResult(DevicePollOutcome.Invalid, _row.SubjectKind, null);
        }
    }

    public async Task<DeviceCodeView?> Describe(DateTimeOffset now)
    {
        if (_row is null)
            return null;
        await ExpireIfDue(now);
        return new DeviceCodeView(_row.SubjectKind, _row.RequestedServerName, _row.Status, _row.ExpiresAt);
    }

    public async Task<bool> Approve(Guid playerId, DateTimeOffset now)
    {
        if (_row is null)
            return false;
        await ExpireIfDue(now);
        if (_row.Status != DeviceCodeStatus.Pending)
            return false;

        Guid subjectId;
        if (_row.SubjectKind == SubjectKind.Server)
        {
            // A game server's first approval mints its durable identity, owned by the approver.
            subjectId = Guid.CreateVersion7();
            await GrainFactory.GetGrain<IGameServerGrain>(subjectId).Create(playerId, _row.RequestedServerName!, now);
        }
        else
            subjectId = playerId;

        await Persist(r =>
        {
            r.Status = DeviceCodeStatus.Approved;
            r.ApprovedSubjectId = subjectId;
            r.ApprovedByPlayerId = playerId;
        });
        return true;
    }

    public async Task<bool> Deny(Guid playerId, DateTimeOffset now)
    {
        if (_row is null)
            return false;
        await ExpireIfDue(now);
        if (_row.Status != DeviceCodeStatus.Pending)
            return false;
        await Persist(r =>
        {
            r.Status = DeviceCodeStatus.Denied;
            r.ApprovedByPlayerId = playerId;
        });
        return true;
    }

    async Task ExpireIfDue(DateTimeOffset now)
    {
        if (_row!.Status == DeviceCodeStatus.Pending && now >= _row.ExpiresAt)
            await Persist(r => r.Status = DeviceCodeStatus.Expired);
    }

    async Task Persist(Action<DeviceCode> mutate)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.DeviceCodes.Attach(_row!);
        mutate(_row!);
        await db.SaveChangesAsync();
    }

    public static string MintUserCode()
    {
        Span<char> chars = stackalloc char[LobbyLimits.UserCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        return new string(chars);
    }

    // Accepts what a human types back: case-insensitive, hyphens/spaces stripped ("BCDF-GHJK").
    public static string? NormalizeUserCode(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
            return null;
        var cleaned = new string(typed.Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        if (cleaned.Length != LobbyLimits.UserCodeLength || cleaned.Any(c => !UserCodeAlphabet.Contains(c)))
            return null;
        return cleaned;
    }

    public static string FormatUserCode(string userCode) => $"{userCode[..4]}-{userCode[4..]}";
}
