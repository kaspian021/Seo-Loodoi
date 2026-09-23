using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Crawler v2. Adds PageRenderEvidences, which holds per-page render status, the deterministic
    /// raw-vs-rendered diff, and resource metadata. It also serves as the render-quota ledger.
    /// The new CrawlSettings fields (RenderMode, DiscoveryMode, Viewport, MaxRendersPerCrawl, UrlList)
    /// live in the existing jsonb Settings column and are nullable. Existing rows therefore need no
    /// backfill and keep pre-v2 behaviour (html mode, hybrid discovery).
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260924100000_AddCrawlerV2RenderEvidence")]
    public partial class AddCrawlerV2RenderEvidence : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PageRenderEvidences",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawledUrlId = table.Column<Guid>(type: "uuid", nullable: true),
                    NormalizedUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RenderMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Viewport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TriggerSignalsJson = table.Column<string>(type: "text", nullable: false),
                    DiffJson = table.Column<string>(type: "text", nullable: false),
                    ResourcesJson = table.Column<string>(type: "text", nullable: false),
                    RenderedFinalUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RenderedWordCount = table.Column<int>(type: "integer", nullable: false),
                    RawWordCount = table.Column<int>(type: "integer", nullable: false),
                    CriticalDifferences = table.Column<int>(type: "integer", nullable: false),
                    SubresourceRequests = table.Column<int>(type: "integer", nullable: false),
                    BlockedRequests = table.Column<int>(type: "integer", nullable: false),
                    JsErrors = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageRenderEvidences", x => x.Id);
                });
            migrationBuilder.CreateIndex(name: "IX_PageRenderEvidences_CrawlId_NormalizedUrl", schema: "loodoi", table: "PageRenderEvidences", columns: new[] { "CrawlId", "NormalizedUrl" }, unique: true);
            migrationBuilder.CreateIndex(name: "IX_PageRenderEvidences_ProjectId_CreatedAt", schema: "loodoi", table: "PageRenderEvidences", columns: new[] { "ProjectId", "CreatedAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PageRenderEvidences", schema: "loodoi");
        }
    }
}
