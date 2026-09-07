using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PublicLobby.Data.Entities;

namespace PublicLobby.Data;

// System of record for the public lobby (ADR-0002). Grains are the single writers of their rows;
// endpoints never write through this context directly. Tables below are exactly plan
// .PLAN/LobbyRankingService.md §3.4 — column names come from EFCore.NamingConventions
// snake-casing the CLR property names, table names are set explicitly to match the plan's names.
//
// Identity's own tables ship as-is from IdentityDbContext EXCEPT for two things WP0.1 had to work
// around, both confirmed against the generated migration rather than assumed:
//   1. IdentityDbContext<TUser,TRole,TKey> (the 3-generic-argument convenience form the WP0.0
//      stub used) does NOT include the .NET 10 passkeys table — TUserPasskey only exists on the
//      9-argument form. Widened below to IdentityUserPasskey<Guid> so the schema carries it now;
//      no passkey *handlers* (ceremony endpoints, ".AddPasskeys()") are added — that's WP0.3.
//   2. IdentityDbContext's own OnModelCreating calls ToTable("AspNetUsers") etc. EXPLICITLY, and
//      EFCore.NamingConventions does not rewrite a name that's already been set explicitly (only
//      convention-assigned names) — so left alone, Identity's tables stay PascalCase
//      ("AspNetUsers") while every table below is snake_case. Plan §1.4 says Identity's tables
//      "get snake_cased too (renamed by the convention)", so OnModelCreating re-maps them below,
//      after base.OnModelCreating, to match every other table in this schema.
public class LobbyDbContext(DbContextOptions<LobbyDbContext> options)
    : IdentityDbContext<
        LobbyUser,
        IdentityRole<Guid>,
        Guid,
        IdentityUserClaim<Guid>,
        IdentityUserRole<Guid>,
        IdentityUserLogin<Guid>,
        IdentityRoleClaim<Guid>,
        IdentityUserToken<Guid>,
        IdentityUserPasskey<Guid>
    >(options),
        IDataProtectionKeyContext
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<DeviceCode> DeviceCodes => Set<DeviceCode>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<GameServer> GameServers => Set<GameServer>();
    public DbSet<JoinTokenIssued> JoinTokensIssued => Set<JoinTokenIssued>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchTeam> MatchTeams => Set<MatchTeam>();
    public DbSet<MatchPilot> MatchPilots => Set<MatchPilot>();

    // ASP.NET DataProtection key ring (IDataProtectionKeyContext), table data_protection_keys —
    // persisted here so cookies and passkey/external-login state survive container redeploys
    // (wired in public-lobby/Hosting/Persistence.cs).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // citext backs case-insensitive display-name uniqueness (plan §1.1); enabled here so the
        // first migration creates the extension before any column uses it.
        builder.HasPostgresExtension("citext");

        // Re-map Identity's own explicit ToTable names to snake_case (see the class comment for
        // why EFCore.NamingConventions doesn't do this on its own).
        builder.Entity<LobbyUser>().ToTable("asp_net_users");
        builder.Entity<IdentityRole<Guid>>().ToTable("asp_net_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("asp_net_user_claims");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("asp_net_user_roles");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("asp_net_user_logins");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("asp_net_role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("asp_net_user_tokens");
        builder.Entity<IdentityUserPasskey<Guid>>().ToTable("asp_net_user_passkeys");

        builder.Entity<Player>(e =>
        {
            e.ToTable("players");
            e.HasKey(p => p.Id);
            // Shared primary key with the Identity user row: Player.Id IS the FK, never
            // database-generated (assigned at account creation from the Identity user's id).
            e.Property(p => p.Id).ValueGeneratedNever();
            e.HasOne<LobbyUser>().WithOne().HasForeignKey<Player>(p => p.Id).OnDelete(DeleteBehavior.Restrict);

            e.Property(p => p.DisplayName).HasColumnType("citext");
            e.HasIndex(p => p.DisplayName).IsUnique();
        });

        builder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedNever(); // app-assigned (Guid.CreateVersion7), not DB
            e.Property(s => s.SubjectKind).HasConversion(new EnumTextConverter<SubjectKind>(EnumTextMaps.SubjectKindText));
            e.HasIndex(s => s.RefreshHash).IsUnique();
            e.HasIndex(s => s.AccessHash);
            e.HasIndex(s => s.LineageId);
            e.HasIndex(s => new { s.SubjectKind, s.SubjectId });
        });

        builder.Entity<DeviceCode>(e =>
        {
            e.ToTable("device_codes");
            e.HasKey(d => d.Code);
            // Property is `Code` (C# forbids a member named the same as its enclosing type
            // DeviceCode) but the column is device_code per plan §3.4.
            e.Property(d => d.Code).HasColumnName("device_code");
            e.Property(d => d.SubjectKind).HasConversion(new EnumTextConverter<SubjectKind>(EnumTextMaps.SubjectKindText));
            e.Property(d => d.Status)
                .HasConversion(new EnumTextConverter<DeviceCodeStatus>(EnumTextMaps.DeviceCodeStatusText));
            e.HasIndex(d => d.UserCode).IsUnique();
            // No FK to Player: ApprovedByPlayerId is only ever read alongside its DeviceCode row
            // (approval history), and a device code always expires/gets consumed long before a
            // Player row would ever be deleted (which never happens — no retention policy).
        });

        builder.Entity<SigningKey>(e =>
        {
            e.ToTable("signing_keys");
            e.HasKey(k => k.Kid);
        });

        builder.Entity<GameServer>(e =>
        {
            e.ToTable("game_servers");
            e.HasKey(g => g.Id);
            e.Property(g => g.Id).ValueGeneratedNever(); // minted at first device-code approval
            e.HasOne<Player>().WithMany().HasForeignKey(g => g.OperatorPlayerId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<JoinTokenIssued>(e =>
        {
            e.ToTable("join_tokens_issued");
            e.HasKey(j => j.Jti);
            e.HasOne<Player>().WithMany().HasForeignKey(j => j.PlayerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GameServer>().WithMany().HasForeignKey(j => j.GameServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(j => new { j.PlayerId, j.GameServerId });
        });

        builder.Entity<Match>(e =>
        {
            e.ToTable("matches");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).ValueGeneratedNever(); // minted by the game server, not EF/PG
            e.HasOne<GameServer>().WithMany().HasForeignKey(m => m.GameServerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(m => m.Status).HasConversion(new EnumTextConverter<MatchStatus>(EnumTextMaps.MatchStatusText));
            e.Property(m => m.EndReason)
                .HasConversion(new NullableEnumTextConverter<MatchEndReasonKind>(EnumTextMaps.MatchEndReasonText));
            e.HasIndex(m => new { m.GameServerId, m.StartedAt });
        });

        builder.Entity<MatchTeam>(e =>
        {
            e.ToTable("match_teams");
            e.HasKey(t => new { t.MatchId, t.Team });
            e.HasOne<Match>().WithMany().HasForeignKey(t => t.MatchId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MatchPilot>(e =>
        {
            e.ToTable("match_pilots");
            e.HasKey(p => new { p.MatchId, p.PlayerId });
            e.HasOne<Match>().WithMany().HasForeignKey(p => p.MatchId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Player>().WithMany().HasForeignKey(p => p.PlayerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => p.PlayerId);
        });
    }
}
