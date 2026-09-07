using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableCrawlQueues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAt",
                schema: "loodoi",
                table: "Crawls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CrawlFrontierItems",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    NormalizedUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    DiscoveredFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlFrontierItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeoBackgroundJobs",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeoBackgroundJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrawlFrontierItems_CrawlId_NormalizedUrl",
                schema: "loodoi",
                table: "CrawlFrontierItems",
                columns: new[] { "CrawlId", "NormalizedUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrawlFrontierItems_CrawlId_Status_NotBefore_Depth",
                schema: "loodoi",
                table: "CrawlFrontierItems",
                columns: new[] { "CrawlId", "Status", "NotBefore", "Depth" });

            migrationBuilder.CreateIndex(
                name: "IX_SeoBackgroundJobs_IdempotencyKey",
                schema: "loodoi",
                table: "SeoBackgroundJobs",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeoBackgroundJobs_Status_NotBefore_CreatedAt",
                schema: "loodoi",
                table: "SeoBackgroundJobs",
                columns: new[] { "Status", "NotBefore", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrawlFrontierItems",
                schema: "loodoi");

            migrationBuilder.DropTable(
                name: "SeoBackgroundJobs",
                schema: "loodoi");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                schema: "loodoi",
                table: "Crawls");
        }
    }
}
