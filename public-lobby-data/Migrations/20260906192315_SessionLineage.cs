using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PublicLobby.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lineage_id",
                table: "sessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_sessions_lineage_id",
                table: "sessions",
                column: "lineage_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sessions_lineage_id",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "lineage_id",
                table: "sessions");
        }
    }
}
