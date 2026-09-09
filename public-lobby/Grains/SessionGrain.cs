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
//
// Lost-rotation grace (2026-09-08 incident, plan §8): a rotation is committed here before the
// HTTP response carrying the new tokens leaves the process. When that response is lost (the
// lobby redeployed mid-request, a gateway timeout) the client still holds the PREVIOUS token and
// retries with it — which is exactly what reuse detection punishes, so a redeploy could burn a
// game server's credential and force a re-pair. So the leaf's parent is accepted for a short
// window after the rotation that superseded it, provided nobody has used the leaf's access token
// yet: the unclaimed leaf is revoked and a fresh rotation is minted from the parent. A stolen
// parent token replayed inside that window only steals the leaf's place; the legitimate holder's
// next refresh is then a genuine reuse and kills the lineage.
public interface ISessionGrain : IGrainWithGuidKey
{
    /// <summary>Mint the first row of a new lineage. Fails if the lineage already exists.</summary>
    Task<IssuedSession> Create(SubjectKind kind, Guid subjectId, DateTimeOffset now);

    /// <summary>
    /// The decision <see cref="Refresh"/> would make for this token, without rotating: the subject
    /// it would rotate for, or null when it would be refused. A detected reuse revokes the lineage
    /// here exactly as Refresh would. Lets a caller resolve everything else the response needs
    /// BEFORE committing the rotation, so nothing after the commit can lose the new tokens.
    /// </summary>
    Task<SessionSubject?> Preflight(string refreshToken, DateTimeOffset now);

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
    /// <summary>
    /// How long after a rotation its parent token is still accepted as a lost-rotation retry. Long
    /// enough to ride out a Railway deploy swap (the sim server retries every 5 s throughout).
    /// </summary>
    public static readonly TimeSpan LostRotationGrace = TimeSpan.FromMinutes(5);

    // Lineage rows oldest→newest; the last one is the leaf whose tokens are current.
    readonly List<Session> _rows = [];

    // Set once the leaf's access token has validated on this activation: from then on the leaf has
    // demonstrably reached its holder, so its parent coming back is reuse, not a lost rotation.
    // In-memory only — after a reactivation it is unknown (false), which errs towards the grace.
    bool _leafAccessUsed;

    Session? Leaf => _rows.Count == 0 ? null : _rows[^1];

    enum Presentation
    {
        Refused,
        Leaf,
        LostRotation,
        Reuse,
    }

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

    public async Task<SessionSubject?> Preflight(string refreshToken, DateTimeOffset now)
    {
        switch (Classify(refreshToken, now, out var row))
        {
            case Presentation.Leaf:
            case Presentation.LostRotation:
                return new SessionSubject(row!.SubjectKind, row.SubjectId, now);
            case Presentation.Reuse:
                await Revoke(now);
                return null;
            default:
                return null;
        }
    }

    public async Task<IssuedSession?> Refresh(string refreshToken, DateTimeOffset now)
    {
        switch (Classify(refreshToken, now, out var row))
        {
            case Presentation.Leaf:
                return await AppendRow(row!.SubjectKind, row.SubjectId, row.Id, now);
            case Presentation.LostRotation:
                // Retry of a rotation whose response never reached the client: retire the leaf
                // nobody claimed and rotate from the presented parent instead.
                return await AppendRow(row!.SubjectKind, row.SubjectId, row.Id, now, supersede: Leaf);
            case Presentation.Reuse:
                // A rotated-away refresh token came back outside the grace: the token leaked, or
                // the client is too far behind to trust. Kill the lineage so whoever holds the
                // current token must sign in again.
                await Revoke(now);
                return null;
            default:
                return null;
        }
    }

    Presentation Classify(string refreshToken, DateTimeOffset now, out Session? row)
    {
        row = null;
        var leaf = Leaf;
        if (leaf is null || leaf.RevokedAt is not null || leaf.ExpiresAt <= now)
            return Presentation.Refused;
        var hash = OpaqueTokens.Hash(refreshToken);
        var presented = _rows.Find(r => r.RefreshHash == hash);
        if (presented is null)
            return Presentation.Refused;
        row = presented;
        if (ReferenceEquals(presented, leaf))
            return Presentation.Leaf;
        // The grace is anchored on the FIRST rotation that superseded the presented row (rows are
        // oldest-first), so recovering once does not restart the clock for the same token.
        var superseded = _rows.Find(r => r.ParentId == presented.Id);
        if (
            leaf.ParentId == presented.Id
            && presented.RevokedAt is null
            && !_leafAccessUsed
            && superseded is not null
            && superseded.CreatedAt + LostRotationGrace > now
        )
            return Presentation.LostRotation;
        return Presentation.Reuse;
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
        if (valid)
            _leafAccessUsed = true;
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

    // Appends the next row of the lineage. `supersede` = an unclaimed leaf to revoke in the same
    // transaction (lost-rotation recovery), so the lineage never has two live leaves.
    async Task<IssuedSession> AppendRow(
        SubjectKind kind,
        Guid subjectId,
        Guid? parentId,
        DateTimeOffset now,
        Session? supersede = null
    )
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
            await using var tx = await db.Database.BeginTransactionAsync();
            if (supersede is not null)
                await db
                    .Sessions.Where(s => s.Id == supersede.Id && s.RevokedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now));
            db.Sessions.Add(row);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        if (supersede is not null)
            supersede.RevokedAt ??= now;
        _rows.Add(row);
        _leafAccessUsed = false;
        return new IssuedSession(access, refresh, new SessionSubject(kind, subjectId, row.AccessExpiresAt.Value));
    }
}
