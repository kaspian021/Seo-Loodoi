using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// PHASE 10 — SERP intelligence.
    /// <para>
    /// Hand-authored: the identification attributes normally emitted into the
    /// designer partial are declared inline so the migration is discoverable
    /// without a generated designer file.
    /// </para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260919200000_AddSerpSnapshots")]
    public partial class AddSerpSnapshots : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SerpSnapshots",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeywordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Phrase = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedPhrase = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Country = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Device = table.Column<int>(type: "integer", nullable: false),
                    Surface = table.Column<int>(type: "integer", nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Availability = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    ResultsTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    OwnPosition = table.Column<int>(type: "integer", nullable: true),
                    OwnUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FeaturesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false, defaultValue: "[]"),
                    ProviderPayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerpSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SerpResultEntries",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Snippet = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    IsOwned = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SerpResultEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SerpResultEntries_SerpSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "loodoi",
                        principalTable: "SerpSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SerpSnapshots_ProjectId_KeywordId_CreatedAt",
                schema: "loodoi",
                table: "SerpSnapshots",
                columns: new[] { "ProjectId", "KeywordId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SerpSnapshots_ProjectId_CapturedAt",
                schema: "loodoi",
                table: "SerpSnapshots",
                columns: new[] { "ProjectId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SerpResultEntries_SnapshotId_Position",
                schema: "loodoi",
                table: "SerpResultEntries",
                columns: new[] { "SnapshotId", "Position" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SerpResultEntries",
                schema: "loodoi");

            migrationBuilder.DropTable(
                name: "SerpSnapshots",
                schema: "loodoi");
        }
    }
}
