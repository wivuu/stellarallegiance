using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PublicLobby.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    player_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => new { x.player_id, x.@event });
                    table.ForeignKey(
                        name: "fk_notification_deliveries_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");
        }
    }
}
