using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PublicLobby.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlayerDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_join_tokens_issued_players_player_id",
                table: "join_tokens_issued");

            migrationBuilder.DropForeignKey(
                name: "fk_match_pilots_players_player_id",
                table: "match_pilots");

            migrationBuilder.AlterColumn<Guid>(
                name: "operator_player_id",
                table: "game_servers",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "operator_player_id",
                table: "game_servers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_join_tokens_issued_players_player_id",
                table: "join_tokens_issued",
                column: "player_id",
                principalTable: "players",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_match_pilots_players_player_id",
                table: "match_pilots",
                column: "player_id",
                principalTable: "players",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
