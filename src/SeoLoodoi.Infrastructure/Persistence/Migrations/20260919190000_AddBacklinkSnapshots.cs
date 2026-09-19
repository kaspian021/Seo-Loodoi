using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PHASE 9 — backlink provider architecture.
    /// <para>
    /// This migration is hand-authored: the identification attributes normally
    /// emitted into the designer partial are declared inline so the migration is
    /// discoverable without a generated designer file.
    /// </para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260919190000_AddBacklinkSnapshots")]
    public partial class AddBacklinkSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BacklinkSnapshots",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Availability = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReferringDomains = table.Column<int>(type: "integer", nullable: true),
                    TotalBacklinks = table.Column<int>(type: "integer", nullable: true),
                    FollowCount = table.Column<int>(type: "integer", nullable: true),
                    NoFollowCount = table.Column<int>(type: "integer", nullable: true),
                    NewCount = table.Column<int>(type: "integer", nullable: true),
                    LostCount = table.Column<int>(type: "integer", nullable: true),
                    AuthorityScore = table.Column<decimal>(type: "numeric", nullable: true),
                    AuthorityMetricName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    ObservationsTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    ProviderPayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklinkSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BacklinkObservations",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SourceHost = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TargetUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AnchorText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Rel = table.Column<int>(type: "integer", nullable: false),
                    FirstSeen = table.Column<DateOnly>(type: "date", nullable: true),
                    LastSeen = table.Column<DateOnly>(type: "date", nullable: true),
                    IsNew = table.Column<bool>(type: "boolean", nullable: false),
                    IsLost = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BacklinkObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BacklinkObservations_BacklinkSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "loodoi",
                        principalTable: "BacklinkSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BacklinkSnapshots_ProjectId_CreatedAt",
                schema: "loodoi",
                table: "BacklinkSnapshots",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BacklinkSnapshots_ProjectId_FetchedAt",
                schema: "loodoi",
                table: "BacklinkSnapshots",
                columns: new[] { "ProjectId", "FetchedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BacklinkObservations_SnapshotId_SourceHost",
                schema: "loodoi",
                table: "BacklinkObservations",
                columns: new[] { "SnapshotId", "SourceHost" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BacklinkObservations",
                schema: "loodoi");

            migrationBuilder.DropTable(
                name: "BacklinkSnapshots",
                schema: "loodoi");
        }
    }
}
