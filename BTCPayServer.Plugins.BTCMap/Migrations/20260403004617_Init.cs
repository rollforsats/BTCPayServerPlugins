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
                    Street = table.Column<string>(type: "text", nullable: true),
                    City = table.Column<string>(type: "text", nullable: true),
                    PostCode = table.Column<string>(type: "text", nullable: true),
                    Country = table.Column<string>(type: "text", nullable: true),
                    AcceptsOnchain = table.Column<bool>(type: "boolean", nullable: false),
                    AcceptsLightning = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Listings",
                schema: "BTCPayServer.Plugins.BTCMap");
        }
    }
}
