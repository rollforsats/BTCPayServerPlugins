using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.BTCMap.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.BTCMap");

            migrationBuilder.CreateTable(
                name: "Listings",
                schema: "BTCPayServer.Plugins.BTCMap",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    OsmElementType = table.Column<string>(type: "text", nullable: true),
                    OsmElementId = table.Column<long>(type: "bigint", nullable: false),
                    OsmElementVersion = table.Column<int>(type: "integer", nullable: false),
                    BusinessName = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    HouseNumber = table.Column<string>(type: "text", nullable: true),
                    Street = table.Column<string>(type: "text", nullable: true),
                    City = table.Column<string>(type: "text", nullable: true),
                    PostCode = table.Column<string>(type: "text", nullable: true),
                    Country = table.Column<string>(type: "text", nullable: true),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    AcceptsLightning = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DirectorySubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DirectorySubmittedUrl = table.Column<string>(type: "text", nullable: true),
                    DirectoryPrUrl = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Twitter = table.Column<string>(type: "text", nullable: true),
                    Github = table.Column<string>(type: "text", nullable: true),
                    OnionUrl = table.Column<string>(type: "text", nullable: true),
                    DirectoryType = table.Column<string>(type: "text", nullable: true),
                    DirectorySubType = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Listings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_Status_LastVerifiedAt",
                schema: "BTCPayServer.Plugins.BTCMap",
                table: "Listings",
                columns: new[] { "Status", "LastVerifiedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_StoreId",
                schema: "BTCPayServer.Plugins.BTCMap",
                table: "Listings",
                column: "StoreId",
                unique: true);

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

            migrationBuilder.DropTable(
                name: "Listings",
                schema: "BTCPayServer.Plugins.BTCMap");
        }
    }
}
