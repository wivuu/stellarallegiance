using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PublicLobby.Data.Migrations
{
    // Orleans co-host (WP0.2, plan §1.4/§3.5, ADR-0002): ADO.NET clustering + reminders share
    // this Postgres. This migration does NOT touch the EF model (no C# entity carries these
    // tables — Orleans owns them directly through its own ADO.NET provider, not through
    // LobbyDbContext), so `dotnet ef migrations has-pending-model-changes` reports none. The SQL
    // bodies are the official scripts, embedded verbatim in OrleansSql.cs — see that file's
    // header for exact provenance (dotnet/orleans tag v10.3.1).
    /// <inheritdoc />
    public partial class OrleansAdoNet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Main creates OrleansQuery, which Clustering/Reminders both INSERT INTO — order matters.
            migrationBuilder.Sql(OrleansSql.PostgreSqlMain);
            migrationBuilder.Sql(OrleansSql.PostgreSqlClustering);
            migrationBuilder.Sql(OrleansSql.PostgreSqlReminders);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse order: drop the tables/functions Reminders/Clustering added, then Main's
            // OrleansQuery table they depended on.
            migrationBuilder.Sql(
                @"DROP TABLE IF EXISTS ""OrleansRemindersTable"" CASCADE;
DROP FUNCTION IF EXISTS upsert_reminder_row(varchar, varchar, varchar, timestamptz, bigint, integer);
DROP FUNCTION IF EXISTS delete_reminder_row(varchar, varchar, varchar, integer);"
            );
            migrationBuilder.Sql(
                @"DROP TABLE IF EXISTS ""OrleansMembershipTable"" CASCADE;
DROP TABLE IF EXISTS ""OrleansMembershipVersionTable"" CASCADE;
DROP FUNCTION IF EXISTS update_i_am_alive_time(varchar, varchar, integer, integer, timestamptz);
DROP FUNCTION IF EXISTS insert_membership_version(varchar);
DROP FUNCTION IF EXISTS insert_membership(varchar, varchar, integer, integer, varchar, varchar, integer, integer, timestamptz, timestamptz, integer);
DROP FUNCTION IF EXISTS update_membership(varchar, varchar, integer, integer, integer, varchar, timestamptz, integer);"
            );
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""OrleansQuery"" CASCADE;");
        }
    }
}
