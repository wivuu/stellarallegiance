using Microsoft.EntityFrameworkCore;
using Orleans;
using Orleans.Concurrency;
using PublicLobby.Auth;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

namespace PublicLobby.Grains;

// One grain per session LINEAGE (key = the id of the row minted at login): the single writer of
// every `sessions` row in that lineage (ADR-0002). Rotation on refresh appends a row whose parent
// is the previous leaf; presenting a refresh token that belongs to a superseded row is the RFC
// 6819 "reuse" signal and revokes the whole lineage. Access tokens ride on the leaf row (15 min);
// the refresh window slides 90 days from each rotation (plan §1.1). Callers pass `now` so the
// suite can drive expiry without a fake clock.
public interface ISessionGrain : IGrainWithGuidKey
{
    /// <summary>Mint the first row of a new lineage. Fails if the lineage already exists.</summary>
    Task<IssuedSession> Create(SubjectKind kind, Guid subjectId, DateTimeOffset now);

    /// <summary>Rotate. Null = invalid_grant (unknown, expired, revoked, or a detected reuse).</summary>
    Task<IssuedSession?> Refresh(string refreshToken, DateTimeOffset now);

    /// <summary>Resolve an access-token hash to its subject, or null when it is not current.</summary>
    [ReadOnly]
    Task<SessionSubject?> ValidateAccess(string accessTokenHash, DateTimeOffset now);

    /// <summary>Revoke every row of the lineage (sign out / POST /auth/revoke).</summary>
    Task Revoke(DateTimeOffset now);
}

public sealed class SessionGrain(IDbContextFactory<LobbyDbContext> dbFactory) : Grain, ISessionGrain
{
    // Lineage rows oldest→newest; the last one is the leaf whose tokens are current.
    readonly List<Session> _rows = [];

    Session? Leaf => _rows.Count == 0 ? null : _rows[^1];

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var lineage = this.GetPrimaryKey();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _rows.AddRange(
            await db
                .Sessions.AsNoTracking()
                .Where(s => s.LineageId == lineage)
                .OrderBy(s => s.CreatedAt)
                .ThenBy(s => s.Id)
                .ToListAsync(cancellationToken)
        );
    }

    public async Task<IssuedSession> Create(SubjectKind kind, Guid subjectId, DateTimeOffset now)
    {
        if (_rows.Count != 0)
            throw new InvalidOperationException($"session lineage {this.GetPrimaryKey():N} already exists");
        return await AppendRow(kind, subjectId, parentId: null, now);
    }

    public async Task<IssuedSession?> Refresh(string refreshToken, DateTimeOffset now)
    {
        var leaf = Leaf;
        if (leaf is null || leaf.RevokedAt is not null || leaf.ExpiresAt <= now)
            return null;
        var hash = OpaqueTokens.Hash(refreshToken);
        var presented = _rows.Find(r => r.RefreshHash == hash);
        if (presented is null)
            return null;
        if (!ReferenceEquals(presented, leaf))
        {
            // A rotated-away refresh token came back: either the legitimate client lost the
            // rotation response or the token leaked. Both are answered the same way — kill the
            // lineage so whoever holds the current token must sign in again.
            await Revoke(now);
            return null;
        }
        return await AppendRow(leaf.SubjectKind, leaf.SubjectId, leaf.Id, now);
    }

    public Task<SessionSubject?> ValidateAccess(string accessTokenHash, DateTimeOffset now)
    {
        var leaf = Leaf;
        if (leaf is null)
            DeactivateOnIdle(); // a forged lineage id must not pin an empty activation
        var valid =
            leaf is not null
            && leaf.RevokedAt is null
            && leaf.ExpiresAt > now
            && leaf.AccessExpiresAt is { } accessExpiry
            && accessExpiry > now
            && leaf.AccessHash == accessTokenHash;
        return Task.FromResult(
            valid ? new SessionSubject(leaf!.SubjectKind, leaf.SubjectId, leaf.AccessExpiresAt!.Value) : null
        );
    }

    public async Task Revoke(DateTimeOffset now)
    {
        if (_rows.Count == 0 || _rows.TrueForAll(r => r.RevokedAt is not null))
            return;
        var lineage = this.GetPrimaryKey();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db
            .Sessions.Where(s => s.LineageId == lineage && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now));
        foreach (var row in _rows)
            row.RevokedAt ??= now;
    }

    async Task<IssuedSession> AppendRow(SubjectKind kind, Guid subjectId, Guid? parentId, DateTimeOffset now)
    {
        var lineage = this.GetPrimaryKey();
        var access = OpaqueTokens.Mint(OpaqueTokens.AccessPrefix, lineage);
        var refresh = OpaqueTokens.Mint(OpaqueTokens.RefreshPrefix, lineage);
        var row = new Session
        {
            Id = parentId is null ? lineage : Guid.CreateVersion7(),
            LineageId = lineage,
            SubjectKind = kind,
            SubjectId = subjectId,
            RefreshHash = OpaqueTokens.Hash(refresh),
            AccessHash = OpaqueTokens.Hash(access),
            AccessExpiresAt = now + OpaqueTokens.AccessLifetime,
            ParentId = parentId,
            CreatedAt = now,
            ExpiresAt = now + OpaqueTokens.RefreshLifetime,
        };
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Sessions.Add(row);
            await db.SaveChangesAsync();
        }
        _rows.Add(row);
        return new IssuedSession(access, refresh, new SessionSubject(kind, subjectId, row.AccessExpiresAt.Value));
    }
}
