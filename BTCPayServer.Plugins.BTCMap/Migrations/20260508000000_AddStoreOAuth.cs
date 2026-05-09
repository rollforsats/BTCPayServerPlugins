using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.BTCMap.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreOAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoreOAuth",
                schema: "BTCPayServer.Plugins.BTCMap",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    OsmClientId = table.Column<string>(type: "text", nullable: true),
                    OsmClientSecretEncrypted = table.Column<string>(type: "text", nullable: true),
                    OsmAccessTokenEncrypted = table.Column<string>(type: "text", nullable: true),
                    OsmUsername = table.Column<string>(type: "text", nullable: true),
                    PendingState = table.Column<string>(type: "text", nullable: true),
                    PendingStateExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OsmConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OsmDisconnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreOAuth", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreOAuth_StoreId",
                schema: "BTCPayServer.Plugins.BTCMap",
                table: "StoreOAuth",
                column: "StoreId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoreOAuth",
                schema: "BTCPayServer.Plugins.BTCMap");
        }
    }
}
