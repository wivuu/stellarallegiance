using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orleans;
using PublicLobby.Data;
using PublicLobby.Data.Entities;
using PublicLobby.Grains;
using StellarAllegiance.Shared.Lobby;

namespace PublicLobby.Accounts;

/// <summary>Role names used by the public lobby (plan .PLAN/LobbyRankingService.md §1.1, CONTEXT.md "Admin").</summary>
public static class LobbyRoles
{
    public const string Admin = "admin";
}

/// <summary>Outcome of an account-service mutation: either the created/updated <see cref="LobbyUser"/>, or a
/// friendly, user-facing <see cref="Error"/> (never an exception — validation/uniqueness failures are
/// expected traffic on a public sign-up form).</summary>
public sealed record AccountResult(bool Succeeded, LobbyUser? User, string? Error)
{
    public static AccountResult Ok(LobbyUser user) => new(true, user, null);

    public static AccountResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// THE ONE place the web sign-in paths create or find accounts (plan .PLAN/LobbyRankingService.md §1.1,
/// §3.5, WP0.3). Creating the <c>players</c> row at sign-up and flipping <c>is_admin</c> at sign-in are
/// the ONLY writes this work package makes outside a grain — WP1.2's <c>PlayerGrain</c> becomes the
/// single writer of every later change (display-name edits, <c>last_seen_at</c> touches, aggregates).
/// Do not grow this class into a general Player-mutation service; new writes belong on the grain.
/// </summary>
public sealed class AccountService(
    UserManager<LobbyUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    LobbyDbContext db,
    IGrainFactory grains,
    ILogger<AccountService> logger
)
{
    // Bounded retry for the citext-unique display-name collision on external-login signup (plan §1.1
    // "made unique by appending digits on citext collision"). Each attempt after the first mutates the
    // candidate name with a fresh random suffix rather than incrementing, so no lookup query is needed.
    const int MaxDisplayNameAttempts = 8;

    /// <summary>
    /// Finds the Player behind an already-linked external login, or creates a brand-new LobbyUser +
    /// linked login + Player row in one transaction. The default display name is derived from the
    /// provider profile (GitHub login / Google given name / Steam persona) and made citext-unique via
    /// savepoint-scoped retry — Postgres aborts the whole transaction on the first failed statement, so
    /// each attempt runs inside its own savepoint that we roll back to on a unique-violation.
    /// </summary>
    public async Task<(LobbyUser User, Player Player)> FindOrCreateFromExternalLoginAsync(
        ExternalLoginInfo info,
        CancellationToken ct = default
    )
    {
        var existingUser = await userManager.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (existingUser is not null)
        {
            var existingPlayer =
                await db.Players.FindAsync([existingUser.Id], ct)
                ?? throw new InvalidOperationException(
                    $"LobbyUser {existingUser.Id} ({info.LoginProvider}) has no players row — accounts are always created with one."
                );
            return (existingUser, existingPlayer);
        }

        var seed = SanitizeDisplayNameSeed(DefaultDisplayNameSeed(info));

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // UserName is Identity's own internal handle; the game-facing name lives on Player.DisplayName.
        var user = new LobbyUser { UserName = Guid.NewGuid().ToString("N") };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            throw new InvalidOperationException($"Could not create account: {Describe(createResult)}");

        var loginResult = await userManager.AddLoginAsync(
            user,
            new UserLoginInfo(info.LoginProvider, info.ProviderKey, info.ProviderDisplayName)
        );
        if (!loginResult.Succeeded)
            throw new InvalidOperationException($"Could not link {info.LoginProvider} login: {Describe(loginResult)}");

        var now = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < MaxDisplayNameAttempts; attempt++)
        {
            var savepoint = $"display_name_attempt_{attempt}";
            await tx.CreateSavepointAsync(savepoint, ct);

            var candidate = attempt == 0 ? seed : AppendRandomSuffix(seed);
            var player = new Player
            {
                Id = user.Id,
                DisplayName = candidate,
                CreatedAt = now,
                LastSeenAt = now,
            };
            db.Players.Add(player);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (user, player);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                logger.LogDebug(
                    "Display name '{Candidate}' collided for new {Provider} account, retrying (attempt {Attempt}).",
                    candidate,
                    info.LoginProvider,
                    attempt
                );
                await tx.RollbackToSavepointAsync(savepoint, ct);
                db.Entry(player).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException(
            $"Could not allocate a unique display name for a new {info.LoginProvider} account after {MaxDisplayNameAttempts} attempts."
        );
    }

    /// <summary>
    /// Passkey-only sign-up: the player chose <paramref name="displayName"/> themselves, so a collision
    /// is reported back as a friendly error rather than silently mutated (unlike the external-login
    /// default-name path above). <paramref name="userEntity"/>/<paramref name="passkey"/> come from a
    /// successful <c>SignInManager.PerformPasskeyAttestationAsync</c> call.
    /// </summary>
    public async Task<AccountResult> CreateWithPasskeyAsync(
        string displayName,
        PasskeyUserEntity userEntity,
        UserPasskeyInfo passkey,
        CancellationToken ct = default
    )
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length < LobbyLimits.DisplayNameMin || trimmed.Length > LobbyLimits.DisplayNameMax)
            return AccountResult.Failed(
                $"Display name must be {LobbyLimits.DisplayNameMin}-{LobbyLimits.DisplayNameMax} characters."
            );

        if (!Guid.TryParse(userEntity.Id, out var userId))
            throw new InvalidOperationException($"Passkey user entity id '{userEntity.Id}' is not a Guid.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var user = new LobbyUser { Id = userId, UserName = userId.ToString() };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
            return AccountResult.Failed(Describe(createResult));

        var now = DateTimeOffset.UtcNow;
        db.Players.Add(
            new Player
            {
                Id = userId,
                DisplayName = trimmed,
                CreatedAt = now,
                LastSeenAt = now,
            }
        );
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            return AccountResult.Failed("That display name is taken.");
        }

        var passkeyResult = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
        if (!passkeyResult.Succeeded)
        {
            await tx.RollbackAsync(ct);
            return AccountResult.Failed(Describe(passkeyResult));
        }

        await tx.CommitAsync(ct);
        return AccountResult.Ok(user);
    }

    /// <summary>
    /// LOBBY_ADMINS (plan §1.1): a comma list of <c>github:&lt;login&gt;</c>, <c>google:&lt;sub&gt;</c>,
    /// <c>steam:&lt;steamid&gt;</c> or <c>name:&lt;display-name&gt;</c> tokens. Called once per sign-in
    /// (external callback or passkey) with whatever provider claims are available — <paramref
    /// name="provider"/>/<paramref name="providerPrincipal"/> are null for a passkey sign-in, so only the
    /// <c>name:</c> form can match there. A match ensures the "admin" role exists, adds the user to it,
    /// and flips <c>players.is_admin</c>.
    /// </summary>
    public async Task ApplyAdminPolicyAsync(
        LobbyUser user,
        string? provider,
        ClaimsPrincipal? providerPrincipal,
        CancellationToken ct = default
    )
    {
        var raw = Environment.GetEnvironmentVariable("LOBBY_ADMINS");
        if (string.IsNullOrWhiteSpace(raw))
            return;
        var admins = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (admins.Length == 0)
            return;

        var player =
            await db.Players.FindAsync([user.Id], ct)
            ?? throw new InvalidOperationException($"players row missing for {user.Id}.");

        var candidates = new List<string> { $"name:{player.DisplayName}" };
        var providerToken = (provider, providerPrincipal) switch
        {
            ("GitHub", { } p) => Token("github", p.FindFirstValue(ClaimTypes.Name)), // GitHub login (see GitHubAuthenticationOptions claim map)
            ("Google", { } p) => Token("google", p.FindFirstValue(ClaimTypes.NameIdentifier)), // sub
            ("Steam", { } p) => Token("steam", ExtractSteamId(p.FindFirstValue(ClaimTypes.NameIdentifier))),
            _ => null,
        };
        if (providerToken is not null)
            candidates.Add(providerToken);

        var isAdmin = candidates.Any(c => admins.Contains(c, StringComparer.OrdinalIgnoreCase));
        if (!isAdmin)
            return;

        if (!await roleManager.RoleExistsAsync(LobbyRoles.Admin))
            await roleManager.CreateAsync(new IdentityRole<Guid>(LobbyRoles.Admin));
        if (!await userManager.IsInRoleAsync(user, LobbyRoles.Admin))
            await userManager.AddToRoleAsync(user, LobbyRoles.Admin);

        // players.is_admin belongs to PlayerGrain (the row's single writer, which also caches it).
        if (!player.IsAdmin)
            await grains.GetGrain<IPlayerGrain>(user.Id).SetAdmin(true);
    }

    /// <summary>
    /// <c>grant_type=dev</c> (AUTH_DEV_LOGIN=true only — plan §6 item 1): a player keyed by display
    /// name alone, with no external login or passkey, so headless harnesses can hold a real session.
    /// Finds the existing player of that name (citext) or creates LobbyUser + Player.
    /// </summary>
    public async Task<Player> FindOrCreateForDevGrantAsync(string displayName, CancellationToken ct = default)
    {
        var trimmed = displayName.Trim();
        for (var attempt = 0; ; attempt++)
        {
            var existing = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.DisplayName == trimmed, ct);
            if (existing is not null)
                return existing;

            var userId = Guid.CreateVersion7();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var user = new LobbyUser { Id = userId, UserName = userId.ToString() };
            var createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
                throw new InvalidOperationException($"dev grant: could not create user: {Describe(createResult)}");
            var now = DateTimeOffset.UtcNow;
            var player = new Player
            {
                Id = userId,
                DisplayName = trimmed,
                CreatedAt = now,
                LastSeenAt = now,
            };
            db.Players.Add(player);
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogInformation("dev grant created player {DisplayName} ({PlayerId})", trimmed, userId);
                return player;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < 2)
            {
                // Lost a race with a concurrent dev grant for the same name: re-read it.
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
    }

    static string? Token(string prefix, string? value) => string.IsNullOrWhiteSpace(value) ? null : $"{prefix}:{value}";

    // Steam's OpenID claimed identifier is a full URL (https://steamcommunity.com/openid/id/<id64>, or the
    // pre-2018 http:// form) — strip the namespace prefix the same way SteamAuthenticationHandler does
    // internally, since that stripped value isn't exposed as its own claim.
    static string? ExtractSteamId(string? claimedIdentifier)
    {
        const string modern = "https://steamcommunity.com/openid/id/";
        const string legacy = "http://steamcommunity.com/openid/id/";
        if (claimedIdentifier is null)
            return null;
        if (claimedIdentifier.StartsWith(modern, StringComparison.Ordinal))
            return claimedIdentifier[modern.Length..];
        if (claimedIdentifier.StartsWith(legacy, StringComparison.Ordinal))
            return claimedIdentifier[legacy.Length..];
        return null;
    }

    static string DefaultDisplayNameSeed(ExternalLoginInfo info)
    {
        var principal = info.Principal;
        var seed = info.LoginProvider switch
        {
            // GitHub's ClaimTypes.Name IS the login/username (see GitHubAuthenticationOptions' claim
            // map); "urn:github:name" is the longer profile name and only a fallback.
            "GitHub" => principal.FindFirstValue(ClaimTypes.Name) ?? principal.FindFirstValue("urn:github:name"),
            "Google" => principal.FindFirstValue(ClaimTypes.GivenName) ?? principal.FindFirstValue(ClaimTypes.Name),
            // Steam's persona name only arrives (as ClaimTypes.Name) when AUTH_STEAM_API_KEY is set.
            "Steam" => principal.FindFirstValue(ClaimTypes.Name),
            _ => principal.FindFirstValue(ClaimTypes.Name),
        };
        return string.IsNullOrWhiteSpace(seed) ? "Pilot" : seed;
    }

    static string SanitizeDisplayNameSeed(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > LobbyLimits.DisplayNameMax)
            trimmed = trimmed[..LobbyLimits.DisplayNameMax];
        if (trimmed.Length < LobbyLimits.DisplayNameMin)
            trimmed = AppendRandomSuffix("Pilot");
        return trimmed;
    }

    static string AppendRandomSuffix(string seed)
    {
        var suffix = Random.Shared.Next(10, 10000).ToString();
        var maxBaseLength = Math.Max(LobbyLimits.DisplayNameMax - suffix.Length, LobbyLimits.DisplayNameMin);
        var baseName = seed.Length > maxBaseLength ? seed[..maxBaseLength] : seed;
        return baseName + suffix;
    }

    static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    static string Describe(IdentityResult result) => string.Join("; ", result.Errors.Select(e => e.Description));
}
