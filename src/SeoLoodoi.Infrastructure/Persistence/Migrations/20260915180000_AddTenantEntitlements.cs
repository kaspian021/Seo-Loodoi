using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantEntitlements",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoodoiAccountId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxProjects = table.Column<int>(type: "integer", nullable: false),
                    MaxPagesPerMonth = table.Column<int>(type: "integer", nullable: false),
                    MaxKeywords = table.Column<int>(type: "integer", nullable: false),
                    MaxCompetitors = table.Column<int>(type: "integer", nullable: false),
                    MaxTeamMembers = table.Column<int>(type: "integer", nullable: false),
                    MaxAiCreditsPerMonth = table.Column<int>(type: "integer", nullable: false),
                    AiCreditsUsed = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    FeaturesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantEntitlements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlements_LoodoiAccountId",
                schema: "loodoi",
                table: "TenantEntitlements",
                column: "LoodoiAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantEntitlements_UserId",
                schema: "loodoi",
                table: "TenantEntitlements",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantEntitlements",
                schema: "loodoi");
        }
    }
}
