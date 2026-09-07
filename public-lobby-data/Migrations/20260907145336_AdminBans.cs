using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PublicLobby.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ban_expires_at",
                table: "players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ban_reason",
                table: "players",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "banned_at",
                table: "players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "banned_by_display_name",
                table: "players",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "banned_by_player_id",
                table: "players",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ban_expires_at",
                table: "game_servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ban_reason",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "banned_at",
                table: "game_servers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "banned_by_display_name",
                table: "game_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "banned_by_player_id",
                table: "game_servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_matches_started_at",
                table: "matches",
                column: "started_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_matches_started_at",
                table: "matches");

            migrationBuilder.DropColumn(
                name: "ban_expires_at",
                table: "players");

            migrationBuilder.DropColumn(
                name: "ban_reason",
                table: "players");

            migrationBuilder.DropColumn(
                name: "banned_at",
                table: "players");

            migrationBuilder.DropColumn(
                name: "banned_by_display_name",
                table: "players");

            migrationBuilder.DropColumn(
                name: "banned_by_player_id",
                table: "players");

            migrationBuilder.DropColumn(
                name: "ban_expires_at",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "ban_reason",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "banned_at",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "banned_by_display_name",
                table: "game_servers");

            migrationBuilder.DropColumn(
                name: "banned_by_player_id",
                table: "game_servers");
        }
    }
}
