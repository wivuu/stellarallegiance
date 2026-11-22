// Schema section (WP0.1): proves the real migrations apply, are idempotent, and produce the
// exact tables/columns of plan .PLAN/LobbyRankingService.md §3.4 against a throwaway Postgres
// (Testcontainers). Skipped with a WARN (not a failure) when Docker is unreachable — see
// PostgresFixture.

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PublicLobby.Data;
using PublicLobby.Data.Entities;

static partial class Suite
{
    // Every table plan §3.4 (WP0.1 deliverable 1) names, exactly as the migration created them —
    // see the "exact table names" verification in the work-package report.
    static readonly string[] ExpectedTables =
    [
        "players",
        "sessions",
        "device_codes",
        "signing_keys",
        "game_servers",
        "join_tokens_issued",
        "matches",
        "match_teams",
        "match_pilots",
        "asp_net_users",
        "asp_net_roles",
        "asp_net_user_claims",
        "asp_net_user_roles",
        "asp_net_user_logins",
        "asp_net_role_claims",
        "asp_net_user_tokens",
        "asp_net_user_passkeys",
        "data_protection_keys",
    ];

    static async Task RunSchemaTestsAsync()
    {
        Console.WriteLine("-- SchemaTests --");

        RunNamingConventionChecks();

        var dataSource = await PostgresFixture.GetDataSourceAsync();
        if (dataSource is null)
            return; // WARN already printed by PostgresFixture; not a failure on a Docker-less box

        var services = new ServiceCollection();
        // LobbyDbContext.OnModelCreating unconditionally maps IdentityUserPasskey<Guid> (the .NET
        // 10 passkeys table) — that mapping throws unless IdentityOptions.Stores.SchemaVersion is
        // at least Version3 (see the class comment on LobbyDbContext and Persistence.cs), so any
        // host of this context — including this suite — must configure it the same way.
        services.Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
        services.AddDbContext<LobbyDbContext>(o => o.UseNpgsql(dataSource));
        await using var provider = services.BuildServiceProvider();

        // ---- migrate, twice (idempotent) ----
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();
            await db.Database.MigrateAsync();
        }
        var secondMigrateThrew = false;
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();
            await db.Database.MigrateAsync();
        }
        catch
        {
            secondMigrateThrew = true;
        }
        Check(!secondMigrateThrew, "MigrateAsync is idempotent (second run is a no-op)");

        // ---- information_schema: every table exists, players.display_name is citext ----
        await using (var conn = await dataSource.OpenConnectionAsync())
        {
            foreach (var table in ExpectedTables)
            {
                var exists = await ScalarBoolAsync(
                    conn,
                    "select exists (select 1 from information_schema.tables where table_schema = 'public' and table_name = @t)",
                    ("@t", table)
                );
                Check(exists, $"table '{table}' exists");
            }

            var udtName = await ScalarStringAsync(
                conn,
                "select udt_name from information_schema.columns where table_name = 'players' and column_name = 'display_name'"
            );
            Eq("citext", udtName, "players.display_name is citext");

            var historyColumns = await ScalarStringAsync(
                conn,
                "select string_agg(column_name, ',' order by ordinal_position) from information_schema.columns where table_name = '__EFMigrationsHistory'"
            );
            Eq("migration_id,product_version", historyColumns, "__EFMigrationsHistory columns are snake_case");
        }

        // ---- round trip: Identity user + Player + GameServer + Match + MatchPilot ----
        var playerId = Guid.CreateVersion7();
        var gameServerId = Guid.CreateVersion7();
        var matchId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();

            db.Users.Add(
                new LobbyUser
                {
                    Id = playerId,
                    UserName = "vex",
                    NormalizedUserName = "VEX",
                }
            );
            db.Players.Add(
                new Player
                {
                    Id = playerId,
                    DisplayName = "Schema Probe",
                    CreatedAt = now,
                    LastSeenAt = now,
                }
            );
            db.GameServers.Add(
                new GameServer
                {
                    Id = gameServerId,
                    OperatorPlayerId = playerId,
                    Name = "Test Server",
                    Ranked = true,
                    CreatedAt = now,
                }
            );
            db.Matches.Add(
                new Match
                {
                    Id = matchId,
                    GameServerId = gameServerId,
                    ListingId = "listing-1",
                    Map = "Brimstone Gambit",
                    StartedAt = now,
                    EndedAt = now.AddMinutes(12),
                    WinnerTeam = 0,
                    EndReason = MatchEndReasonKind.WinCondition,
                    Status = MatchStatus.Ended,
                    Counted = true,
                    Ranked = true,
                }
            );
            db.MatchPilots.Add(
                new MatchPilot
                {
                    MatchId = matchId,
                    PlayerId = playerId,
                    DisplayNameAtMatch = "Schema Probe",
                    Team = 0,
                    Kills = 3,
                    Deaths = 1,
                    Ejects = 0,
                    Points = 275,
                    ConnectedAtEnd = true,
                    Won = true,
                }
            );
            await db.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();

            var player = await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId);
            Check(player is not null, "Player round-trips");
            Eq("Schema Probe", player?.DisplayName, "Player.DisplayName round-trips");

            var gameServer = await db.GameServers.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameServerId);
            Check(gameServer is not null, "GameServer round-trips");
            Eq(playerId, gameServer?.OperatorPlayerId ?? Guid.Empty, "GameServer.OperatorPlayerId round-trips");

            var match = await db.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == matchId);
            Check(match is not null, "Match round-trips");
            Eq(MatchStatus.Ended, match?.Status ?? default, "Match.Status round-trips through EnumTextConverter");
            Eq(
                MatchEndReasonKind.WinCondition,
                match?.EndReason ?? default,
                "Match.EndReason round-trips through NullableEnumTextConverter"
            );

            var pilot = await db
                .MatchPilots.AsNoTracking()
                .SingleOrDefaultAsync(p => p.MatchId == matchId && p.PlayerId == playerId);
            Check(pilot is not null, "MatchPilot round-trips");
            Eq(275L, pilot?.Points ?? -1, "MatchPilot.Points round-trips");
        }

        // ---- citext uniqueness: a case-variant duplicate display name is rejected ----
        var dupRejected = false;
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LobbyDbContext>();
            var dupId = Guid.CreateVersion7();
            db.Users.Add(
                new LobbyUser
                {
                    Id = dupId,
                    UserName = "vex2",
                    NormalizedUserName = "VEX2",
                }
            );
            db.Players.Add(
                new Player
                {
                    Id = dupId,
                    DisplayName = "SCHEMA PROBE", // same name, different case as "Schema Probe" above
                    CreatedAt = now,
                    LastSeenAt = now,
                }
            );
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            dupRejected = true;
        }
        Check(dupRejected, "citext unique index rejects a case-variant duplicate display name");
    }

    // SnakeCaseNamingConvention replaced the EFCore.NamingConventions package, and every committed
    // migration plus the model snapshot was generated with the package. Needs no database: a
    // renamed table, column, key, FK, index or JSON column shows up as a pending model change.
    static void RunNamingConventionChecks()
    {
        using (var db = new LobbyDbContextFactory().CreateDbContext([]))
        {
            Check(
                !db.Database.HasPendingModelChanges(),
                "model matches the migrations snapshot (SnakeCaseNamingConvention names are unchanged)"
            );

            // EF names its migrations history table from the same convention set, and every
            // existing database (production included) was created with exactly this script. A
            // fresh test database can't catch a drift here: EF would create and query its own
            // table consistently, while a deployed lobby would fail to migrate.
            Eq(
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    migration_id character varying(150) NOT NULL,
                    product_version character varying(32) NOT NULL,
                    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
                );
                """,
                db.GetService<IHistoryRepository>().GetCreateScript().ReplaceLineEndings("\n").TrimEnd(),
                "migrations history table keeps the snake_case names existing databases have"
            );
        }

        // Expected values are EFCore.NamingConventions 10.0.1's own output for the same inputs, so
        // names added later keep the package's style, quirks included.
        (string Input, string Expected)[] cases =
        [
            ("AspNetUsers", "asp_net_users"),
            ("DataProtectionKeys", "data_protection_keys"),
            ("IdentityUserPasskeyCredentialId", "identity_user_passkey_credential_id"),
            ("PK_AspNetUsers", "pk_asp_net_users"),
            ("FK_players_asp_net_users_Id", "fk_players_asp_net_users_id"),
            ("IX_sessions_SubjectKind_SubjectId", "ix_sessions_subject_kind_subject_id"),
            ("HTMLParser", "html_parser"),
            ("UserID", "user_id"),
            ("MaxRTT", "max_rtt"),
            ("OAuthToken", "o_auth_token"),
            ("Address1Line", "address1line"),
            ("Sha256Hash", "sha256hash"),
            ("Kills2Deaths", "kills2deaths"),
            ("V2", "v2"),
            ("ABC", "abc"),
            ("X", "x"),
            ("already_snake", "already_snake"),
            ("Item_Name", "item_name"),
            ("_Leading", "_leading"),
            ("Trailing_", "trailing_"),
            ("Has Space", "has_space"),
            ("ÉcoleName", "école_name"),
        ];
        var mismatches = cases
            .Select(c => (c.Input, c.Expected, Actual: SnakeCaseNamingConvention.ToSnakeCase(c.Input)))
            .Where(c => c.Actual != c.Expected)
            .Select(c => $"{c.Input} -> {c.Actual} (want {c.Expected})")
            .ToList();
        Check(
            mismatches.Count == 0,
            $"ToSnakeCase matches EFCore.NamingConventions on {cases.Length} names"
                + (mismatches.Count == 0 ? "" : ": " + string.Join(", ", mismatches))
        );
    }

    static async Task<bool> ScalarBoolAsync(NpgsqlConnection conn, string sql, params (string, object)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
