using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PHASE 11 — AEO / GEO (Answer Engine Optimization).
    /// <para>
    /// Hand-authored, matching the style of the Phase 9 and 10 migrations: the
    /// identification attributes normally emitted into a designer partial are
    /// declared inline.
    /// </para>
    /// <para>
    /// The AI crawler definitions are seeded as rows, not compiled as constants.
    /// The answer-engine landscape changes far faster than this project ships
    /// releases, so operators must be able to add a crawler, retire one or
    /// reweight it without a redeploy. Only publicly documented user-agent tokens
    /// are seeded, and none of them imply any visibility measurement of their own.
    /// </para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920150000_AddAeoVisibility")]
    public partial class AddAeoVisibility : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiCrawlerProfiles",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    UserAgentToken = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCrawlerProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiVisibilitySnapshots",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrawlId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiCrawlabilityScore = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    AnswerReadinessScore = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    CitationReadinessScore = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    AiVisibilityScore = table.Column<decimal>(type: "numeric(5,2)", nullable: true),
                    CrawlersAllowed = table.Column<int>(type: "integer", nullable: false),
                    CrawlersBlocked = table.Column<int>(type: "integer", nullable: false),
                    CrawlersUnspecified = table.Column<int>(type: "integer", nullable: false),
                    PagesAnalyzed = table.Column<int>(type: "integer", nullable: false),
                    EvidenceJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    ComputedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiVisibilitySnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCrawlerProfiles_Key",
                schema: "loodoi",
                table: "AiCrawlerProfiles",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiVisibilitySnapshots_ProjectId_CrawlId",
                schema: "loodoi",
                table: "AiVisibilitySnapshots",
                columns: new[] { "ProjectId", "CrawlId" },
                unique: true);

            // Purpose: 0 = AnswerEngine, 1 = AiSearch, 2 = Training.
            // Seeded with raw SQL rather than InsertData: a hand-authored migration
            // carries no designer model, and InsertData needs one to infer column
            // types.
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000001", "openai-searchbot", "OpenAI SearchBot", "OAI-SearchBot", 1, 1.00m, "OpenAI crawler used for search results in ChatGPT.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000002", "chatgpt-user", "ChatGPT-User", "ChatGPT-User", 0, 1.00m, "On-demand fetch when a ChatGPT user opens a link.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000003", "perplexitybot", "PerplexityBot", "PerplexityBot", 0, 1.00m, "Perplexity answer engine crawler.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000004", "claude-user", "Claude-User", "Claude-User", 0, 1.00m, "On-demand fetch when a Claude user opens a link.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000005", "claude-searchbot", "Claude-SearchBot", "Claude-SearchBot", 1, 1.00m, "Anthropic crawler used to index for search.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000006", "gptbot", "OpenAI GPTBot", "GPTBot", 2, 0.80m, "OpenAI training crawler.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000007", "claudebot", "Anthropic ClaudeBot", "ClaudeBot", 2, 0.80m, "Anthropic training crawler.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000008", "google-extended", "Google-Extended", "Google-Extended", 2, 0.70m, "Opt-in control for Gemini grounding and training.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000009", "applebot-extended", "Applebot-Extended", "Applebot-Extended", 2, 0.70m, "Opt-out control for Apple Intelligence training.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000010", "bytespider", "ByteSpider", "Bytespider", 2, 0.60m, "ByteDance crawler feeding AI training.");
            Seed(migrationBuilder, "b1c0d0a0-0001-4a00-8000-000000000011", "cohere-ai", "Cohere AI", "cohere-ai", 1, 0.60m, "Cohere crawler used for retrieval.");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AiVisibilitySnapshots", schema: "loodoi");
            migrationBuilder.DropTable(name: "AiCrawlerProfiles", schema: "loodoi");
        }

        private const string SeededAt = "2026-09-20 15:00:00+00";

        private static void Seed(MigrationBuilder b, string id, string key, string displayName, string token, int purpose, decimal weight, string notes)
        {
            var name = displayName.Replace("'", "''");
            var note = string.IsNullOrEmpty(notes) ? "NULL" : "'" + notes.Replace("'", "''") + "'";
            var w = weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sql = "INSERT INTO loodoi.\"AiCrawlerProfiles\" "
                + "(\"Id\", \"Key\", \"DisplayName\", \"UserAgentToken\", \"Purpose\", \"Weight\", \"IsEnabled\", \"Notes\", \"CreatedAt\", \"UpdatedAt\") VALUES "
                + "('" + id + "'::uuid, '" + key + "', '" + name + "', '" + token + "', " + purpose + ", " + w + ", TRUE, " + note
                + ", TIMESTAMPTZ '" + SeededAt + "', TIMESTAMPTZ '" + SeededAt + "') "
                + "ON CONFLICT (\"Key\") DO NOTHING;";
            b.Sql(sql);
        }
    }
}
