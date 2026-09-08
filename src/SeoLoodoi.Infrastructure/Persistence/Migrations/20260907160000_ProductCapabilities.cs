using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SeoLoodoi.Infrastructure.Persistence;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations;

public partial class ProductCapabilities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "RedirectChainJson", table: "CrawledUrls", schema: "loodoi", type: "text", nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<string>(name: "HeadersJson", table: "CrawledUrls", schema: "loodoi", type: "text", nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>(name: "HreflangJson", table: "PageSnapshots", schema: "loodoi", type: "text", nullable: false, defaultValue: "[]");
        migrationBuilder.AddColumn<string>(name: "OpenGraphJson", table: "PageSnapshots", schema: "loodoi", type: "text", nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>(name: "TwitterCardsJson", table: "PageSnapshots", schema: "loodoi", type: "text", nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>(name: "XRobotsTag", table: "PageSnapshots", schema: "loodoi", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "LastMetricAt", table: "Keywords", schema: "loodoi", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(name: "Channel", table: "AlertRules", schema: "loodoi", type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "dashboard");
        migrationBuilder.AddColumn<string>(name: "Destination", table: "AlertRules", schema: "loodoi", type: "character varying(2048)", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsRead", table: "AlertEvents", schema: "loodoi", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(name: "Format", table: "Reports", schema: "loodoi", type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "json");
        migrationBuilder.AddColumn<Guid>(name: "CrawlId", table: "Reports", schema: "loodoi", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "Content", table: "Reports", schema: "loodoi", type: "text", nullable: false, defaultValue: "");

        // Refuse to destroy existing data when tightening bounded text columns.
        migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM loodoi.\"Keywords\" WHERE char_length(\"Phrase\") > 200 OR char_length(\"NormalizedPhrase\") > 200 OR char_length(\"Language\") > 10 OR char_length(\"Country\") > 10) THEN RAISE EXCEPTION 'Existing keyword data exceeds the new length limits'; END IF; IF EXISTS (SELECT 1 FROM loodoi.\"Competitors\" WHERE char_length(\"Name\") > 160 OR char_length(\"BaseUrl\") > 2048) THEN RAISE EXCEPTION 'Existing competitor data exceeds the new length limits'; END IF; IF EXISTS (SELECT 1 FROM loodoi.\"Reports\" WHERE char_length(\"Type\") > 40) THEN RAISE EXCEPTION 'Existing report type exceeds the new length limit'; END IF; END $$;");
        migrationBuilder.AlterColumn<string>(name: "Phrase", table: "Keywords", schema: "loodoi", type: "character varying(200)", maxLength: 200, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "NormalizedPhrase", table: "Keywords", schema: "loodoi", type: "character varying(200)", maxLength: 200, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "Language", table: "Keywords", schema: "loodoi", type: "character varying(10)", maxLength: 10, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "Country", table: "Keywords", schema: "loodoi", type: "character varying(10)", maxLength: 10, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "Name", table: "Competitors", schema: "loodoi", type: "character varying(160)", maxLength: 160, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "BaseUrl", table: "Competitors", schema: "loodoi", type: "character varying(2048)", maxLength: 2048, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "Type", table: "Reports", schema: "loodoi", type: "character varying(40)", maxLength: 40, nullable: false, oldClrType: typeof(string), oldType: "text");

        migrationBuilder.CreateTable(
            name: "KeywordMetrics", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                KeywordId = table.Column<Guid>(type: "uuid", nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                Clicks = table.Column<int>(type: "integer", nullable: false),
                Impressions = table.Column<int>(type: "integer", nullable: false),
                Ctr = table.Column<decimal>(type: "numeric", nullable: false),
                AveragePosition = table.Column<decimal>(type: "numeric", nullable: false),
                Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                PageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Country = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                Device = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_KeywordMetrics", x => x.Id);
                table.ForeignKey("FK_KeywordMetrics_Keywords_KeywordId", x => x.KeywordId, "Keywords", "Id", principalSchema: "loodoi", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_KeywordMetrics_ProjectId_KeywordId_Date_Country_Device_PageUrl", schema: "loodoi", table: "KeywordMetrics", columns: ["ProjectId", "KeywordId", "Date", "Country", "Device", "PageUrl"], unique: true);
        migrationBuilder.CreateIndex(name: "IX_KeywordMetrics_KeywordId", schema: "loodoi", table: "KeywordMetrics", column: "KeywordId");

        migrationBuilder.CreateTable(
            name: "ProjectMembers", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProjectMembers", x => x.Id);
                table.ForeignKey("FK_ProjectMembers_SeoProjects_ProjectId", x => x.ProjectId, "SeoProjects", "Id", principalSchema: "loodoi", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_ProjectMembers_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", principalSchema: "loodoi", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_ProjectMembers_ProjectId_UserId", schema: "loodoi", table: "ProjectMembers", columns: ["ProjectId", "UserId"], unique: true);
        migrationBuilder.CreateIndex(name: "IX_ProjectMembers_UserId", schema: "loodoi", table: "ProjectMembers", column: "UserId");

        migrationBuilder.CreateTable(
            name: "AuditLogs", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                EntityId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                MetadataJson = table.Column<string>(type: "text", nullable: false),
                IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AuditLogs", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_AuditLogs_ProjectId_CreatedAt", schema: "loodoi", table: "AuditLogs", columns: ["ProjectId", "CreatedAt"]);
        migrationBuilder.CreateIndex(name: "IX_AuditLogs_ActorId_CreatedAt", schema: "loodoi", table: "AuditLogs", columns: ["ActorId", "CreatedAt"]);

        migrationBuilder.CreateTable(
            name: "AlertDeliveries", schema: "loodoi",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                AlertEventId = table.Column<Guid>(type: "uuid", nullable: false),
                Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Destination = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AlertDeliveries", x => x.Id);
                table.ForeignKey("FK_AlertDeliveries_AlertEvents_AlertEventId", x => x.AlertEventId, "AlertEvents", "Id", principalSchema: "loodoi", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex(name: "IX_AlertDeliveries_Status_NextAttemptAt_LockedUntil", schema: "loodoi", table: "AlertDeliveries", columns: ["Status", "NextAttemptAt", "LockedUntil"]);
        migrationBuilder.CreateIndex(name: "IX_AlertDeliveries_AlertEventId", schema: "loodoi", table: "AlertDeliveries", column: "AlertEventId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AlertDeliveries", schema: "loodoi");
        migrationBuilder.DropTable(name: "AuditLogs", schema: "loodoi");
        migrationBuilder.DropTable(name: "KeywordMetrics", schema: "loodoi");
        migrationBuilder.DropTable(name: "ProjectMembers", schema: "loodoi");
        migrationBuilder.AlterColumn<string>(name: "Phrase", table: "Keywords", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(200)", oldMaxLength: 200);
        migrationBuilder.AlterColumn<string>(name: "NormalizedPhrase", table: "Keywords", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(200)", oldMaxLength: 200);
        migrationBuilder.AlterColumn<string>(name: "Language", table: "Keywords", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10);
        migrationBuilder.AlterColumn<string>(name: "Country", table: "Keywords", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10);
        migrationBuilder.AlterColumn<string>(name: "Name", table: "Competitors", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(160)", oldMaxLength: 160);
        migrationBuilder.AlterColumn<string>(name: "BaseUrl", table: "Competitors", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(2048)", oldMaxLength: 2048);
        migrationBuilder.AlterColumn<string>(name: "Type", table: "Reports", schema: "loodoi", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(40)", oldMaxLength: 40);
        migrationBuilder.DropColumn(name: "RedirectChainJson", table: "CrawledUrls", schema: "loodoi");
        migrationBuilder.DropColumn(name: "HeadersJson", table: "CrawledUrls", schema: "loodoi");
        migrationBuilder.DropColumn(name: "HreflangJson", table: "PageSnapshots", schema: "loodoi");
        migrationBuilder.DropColumn(name: "OpenGraphJson", table: "PageSnapshots", schema: "loodoi");
        migrationBuilder.DropColumn(name: "TwitterCardsJson", table: "PageSnapshots", schema: "loodoi");
        migrationBuilder.DropColumn(name: "XRobotsTag", table: "PageSnapshots", schema: "loodoi");
        migrationBuilder.DropColumn(name: "LastMetricAt", table: "Keywords", schema: "loodoi");
        migrationBuilder.DropColumn(name: "Channel", table: "AlertRules", schema: "loodoi");
        migrationBuilder.DropColumn(name: "Destination", table: "AlertRules", schema: "loodoi");
        migrationBuilder.DropColumn(name: "IsRead", table: "AlertEvents", schema: "loodoi");
        migrationBuilder.DropColumn(name: "Format", table: "Reports", schema: "loodoi");
        migrationBuilder.DropColumn(name: "CrawlId", table: "Reports", schema: "loodoi");
        migrationBuilder.DropColumn(name: "Content", table: "Reports", schema: "loodoi");
    }
}
