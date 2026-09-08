using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SeoLoodoi.Infrastructure.Persistence;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations;

public partial class CrawlAnalysisAndEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CrawlAnalyses", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                LastJobKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CrawlAnalyses", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_CrawlAnalyses_CrawlId", schema: "loodoi", table: "CrawlAnalyses", column: "CrawlId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_CrawlAnalyses_ProjectId_Status", schema: "loodoi", table: "CrawlAnalyses", columns: ["ProjectId", "Status"]);

        migrationBuilder.CreateTable(
            name: "CompetitorCrawls", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitorId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                PagesDiscovered = table.Column<int>(type: "integer", nullable: false),
                PagesCrawled = table.Column<int>(type: "integer", nullable: false),
                Errors = table.Column<int>(type: "integer", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                EvidenceSnapshotJson = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CompetitorCrawls", x => x.Id);
                table.ForeignKey("FK_CompetitorCrawls_Competitors_CompetitorId", x => x.CompetitorId, "Competitors", "Id", principalSchema: "loodoi", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_CompetitorCrawls_CompetitorId_CreatedAt", schema: "loodoi", table: "CompetitorCrawls", columns: ["CompetitorId", "CreatedAt"]);
        migrationBuilder.CreateIndex(name: "IX_CompetitorCrawls_ProjectId_Status", schema: "loodoi", table: "CompetitorCrawls", columns: ["ProjectId", "Status"]);

        migrationBuilder.CreateTable(
            name: "CompetitorPages", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitorCrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitorId = table.Column<Guid>(type: "uuid", nullable: false),
                Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                StatusCode = table.Column<int>(type: "integer", nullable: false),
                ContentType = table.Column<string>(type: "text", nullable: true),
                Depth = table.Column<int>(type: "integer", nullable: false),
                ResponseTimeMs = table.Column<long>(type: "bigint", nullable: false),
                IsIndexable = table.Column<bool>(type: "boolean", nullable: false),
                WordCount = table.Column<int>(type: "integer", nullable: false),
                Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                HasMetaDescription = table.Column<bool>(type: "boolean", nullable: false),
                InternalLinkCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CompetitorPages", x => x.Id);
                table.ForeignKey("FK_CompetitorPages_CompetitorCrawls_CompetitorCrawlId", x => x.CompetitorCrawlId, "CompetitorCrawls", "Id", principalSchema: "loodoi", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_CompetitorPages_CompetitorCrawlId_Url", schema: "loodoi", table: "CompetitorPages", columns: ["CompetitorCrawlId", "Url"], unique: true);

        // Backfill durable analysis rows for completed crawls so the new
        // analysis-status endpoint has a row to report from day one.
        migrationBuilder.Sql("""
            INSERT INTO loodoi."CrawlAnalyses" ("Id", "CrawlId", "ProjectId", "Status", "Attempts", "LastJobKey", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), "Id", "ProjectId", 2, 1, 'analyze-crawl:' || "Id"::text, now(), now()
            FROM loodoi."Crawls" c
            WHERE c."Status" = 3
              AND EXISTS (SELECT 1 FROM loodoi."SeoScores" s WHERE s."CrawlId" = c."Id")
              AND NOT EXISTS (SELECT 1 FROM loodoi."CrawlAnalyses" a WHERE a."CrawlId" = c."Id");
            INSERT INTO loodoi."CrawlAnalyses" ("Id", "CrawlId", "ProjectId", "Status", "Attempts", "LastJobKey", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), "Id", "ProjectId",
                   CASE WHEN EXISTS (SELECT 1 FROM loodoi."CrawledUrls" u WHERE u."CrawlId" = c."Id") THEN 0 ELSE 4 END,
                   0, 'analyze-crawl:' || "Id"::text, now(), now()
            FROM loodoi."Crawls" c
            WHERE c."Status" = 3
              AND NOT EXISTS (SELECT 1 FROM loodoi."SeoScores" s WHERE s."CrawlId" = c."Id")
              AND NOT EXISTS (SELECT 1 FROM loodoi."CrawlAnalyses" a WHERE a."CrawlId" = c."Id");
            """);

        // Scores v2: categories without evaluated evidence are null instead of
        // a fabricated 100, and the overall covers only evaluated categories.
        migrationBuilder.AddColumn<bool>(name: "IsPartial", table: "SeoScores", schema: "loodoi", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AlterColumn<decimal>(name: "OverallScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "TechnicalScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "IndexabilityScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "OnPageScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "ContentScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "LinksScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "StructuredDataScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "PerformanceScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "InternationalScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
        migrationBuilder.AlterColumn<decimal>(name: "SecurityScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: true, oldClrType: typeof(decimal), oldType: "numeric");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<decimal>(name: "OverallScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "TechnicalScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "IndexabilityScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "OnPageScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "ContentScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "LinksScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "StructuredDataScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "PerformanceScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "InternationalScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "SecurityScore", table: "SeoScores", schema: "loodoi", type: "numeric", nullable: false, defaultValue: 0m, oldClrType: typeof(decimal), oldType: "numeric", oldNullable: true);
        migrationBuilder.DropColumn(name: "IsPartial", table: "SeoScores", schema: "loodoi");
        migrationBuilder.DropTable(name: "CompetitorPages", schema: "loodoi");
        migrationBuilder.DropTable(name: "CompetitorCrawls", schema: "loodoi");
        migrationBuilder.DropTable(name: "CrawlAnalyses", schema: "loodoi");
    }
}
