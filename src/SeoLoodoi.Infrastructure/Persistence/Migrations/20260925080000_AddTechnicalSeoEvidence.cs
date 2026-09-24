using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P1 Technical SEO engine. Adds the persisted crawl-level evidence the analysis
    /// layer needs (robots.txt raw text, sitemap discovery outcome and sitemap URLs)
    /// and extends issue/score rows with the evidence-first contract fields:
    /// SeoIssues.Confidence + SeoIssues.RuleVersion and SeoScores.ExplanationJson.
    /// All changes are additive; existing rows keep null confidence/version and an
    /// empty explanation.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260925080000_AddTechnicalSeoEvidence")]
    public partial class AddTechnicalSeoEvidence : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Confidence",
                schema: "loodoi",
                table: "SeoIssues",
                type: "numeric(4,3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleVersion",
                schema: "loodoi",
                table: "SeoIssues",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExplanationJson",
                schema: "loodoi",
                table: "SeoScores",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "CrawlRobotsEvidences",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawText = table.Column<string>(type: "text", nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    TemporarilyUnavailable = table.Column<bool>(type: "boolean", nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlRobotsEvidences", x => x.Id);
                });
            migrationBuilder.CreateIndex(name: "IX_CrawlRobotsEvidences_CrawlId", schema: "loodoi", table: "CrawlRobotsEvidences", columns: new[] { "CrawlId" }, unique: true);

            migrationBuilder.CreateTable(
                name: "CrawlSitemapStates",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeedsJson = table.Column<string>(type: "text", nullable: false),
                    ProcessedSitemapsJson = table.Column<string>(type: "text", nullable: false),
                    ErrorsJson = table.Column<string>(type: "text", nullable: false),
                    Truncated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlSitemapStates", x => x.Id);
                });
            migrationBuilder.CreateIndex(name: "IX_CrawlSitemapStates_CrawlId", schema: "loodoi", table: "CrawlSitemapStates", columns: new[] { "CrawlId" }, unique: true);

            migrationBuilder.CreateTable(
                name: "CrawlSitemapEntries",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    NormalizedUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    LastModified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ChangeFrequency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Priority = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlSitemapEntries", x => x.Id);
                });
            migrationBuilder.CreateIndex(name: "IX_CrawlSitemapEntries_CrawlId_NormalizedUrl", schema: "loodoi", table: "CrawlSitemapEntries", columns: new[] { "CrawlId", "NormalizedUrl" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_CrawlSitemapEntries_ProjectId_CreatedAt", schema: "loodoi", table: "CrawlSitemapEntries", columns: new[] { "ProjectId", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CrawlSitemapEntries", schema: "loodoi");
            migrationBuilder.DropTable(name: "CrawlSitemapStates", schema: "loodoi");
            migrationBuilder.DropTable(name: "CrawlRobotsEvidences", schema: "loodoi");

            migrationBuilder.DropColumn(name: "ExplanationJson", schema: "loodoi", table: "SeoScores");
            migrationBuilder.DropColumn(name: "Confidence", schema: "loodoi", table: "SeoIssues");
            migrationBuilder.DropColumn(name: "RuleVersion", schema: "loodoi", table: "SeoIssues");
        }
    }
}
