using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P0 A1/A3 migration.
    /// <para>A1 ProjectMembers.Status (0 = Active, 1 = Suspended) and the ProjectInvitations table. Pending invitations reserve team seats.</para>
    /// <para>A3 TenantEntitlements.LoodoiAccountId becomes nullable and unique when present.
    /// Earlier builds synthesised <c>loodoi_acc_{userId}</c> locally, and that value was never
    /// issued by the Loodoi identity service. Those synthetic ids are cleared, so each tenant
    /// is re-bound from the identity adapter at its next checkout. Genuine ids that arrived
    /// through signed webhooks are kept. Before the unique index is created, duplicate genuine
    /// ids are cleared on every row except the earliest.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260923150000_AddTeamSeatsAndStableLoodoiIdentity")]
    public partial class AddTeamSeatsAndStableLoodoiIdentity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(name: "Status", schema: "loodoi", table: "ProjectMembers", type: "integer", nullable: false, defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ProjectInvitations",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectInvitations", x => x.Id);
                    table.ForeignKey("FK_ProjectInvitations_SeoProjects_ProjectId", x => x.ProjectId, principalSchema: "loodoi", principalTable: "SeoProjects", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(name: "IX_ProjectInvitations_TokenHash", schema: "loodoi", table: "ProjectInvitations", column: "TokenHash", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ProjectInvitations_ProjectId_Status", schema: "loodoi", table: "ProjectInvitations", columns: new[] { "ProjectId", "Status" });
            migrationBuilder.CreateIndex(name: "IX_ProjectInvitations_ProjectId_NormalizedEmail", schema: "loodoi", table: "ProjectInvitations", columns: new[] { "ProjectId", "NormalizedEmail" });

            migrationBuilder.DropIndex(name: "IX_TenantEntitlements_LoodoiAccountId", schema: "loodoi", table: "TenantEntitlements");
            migrationBuilder.AlterColumn<string>(name: "LoodoiAccountId", schema: "loodoi", table: "TenantEntitlements", type: "character varying(128)", maxLength: 128, nullable: true,
                oldClrType: typeof(string), oldType: "character varying(128)", oldMaxLength: 128);
            migrationBuilder.Sql("""
                UPDATE loodoi."TenantEntitlements" SET "LoodoiAccountId" = NULL
                WHERE "LoodoiAccountId" = 'loodoi_acc_' || replace("UserId"::text, '-', '');
                UPDATE loodoi."TenantEntitlements" t SET "LoodoiAccountId" = NULL
                WHERE t."LoodoiAccountId" IS NOT NULL AND EXISTS (
                    SELECT 1 FROM loodoi."TenantEntitlements" o
                    WHERE o."LoodoiAccountId" = t."LoodoiAccountId" AND (o."CreatedAt", o."Id") < (t."CreatedAt", t."Id"));
                """);
            migrationBuilder.CreateIndex(name: "IX_TenantEntitlements_LoodoiAccountId", schema: "loodoi", table: "TenantEntitlements", column: "LoodoiAccountId", unique: true, filter: "\"LoodoiAccountId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_TenantEntitlements_LoodoiAccountId", schema: "loodoi", table: "TenantEntitlements");
            migrationBuilder.Sql("""UPDATE loodoi."TenantEntitlements" SET "LoodoiAccountId" = 'loodoi_acc_' || replace("UserId"::text, '-', '') WHERE "LoodoiAccountId" IS NULL;""");
            migrationBuilder.AlterColumn<string>(name: "LoodoiAccountId", schema: "loodoi", table: "TenantEntitlements", type: "character varying(128)", maxLength: 128, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(128)", oldMaxLength: 128, oldNullable: true);
            migrationBuilder.CreateIndex(name: "IX_TenantEntitlements_LoodoiAccountId", schema: "loodoi", table: "TenantEntitlements", column: "LoodoiAccountId");
            migrationBuilder.DropTable(name: "ProjectInvitations", schema: "loodoi");
            migrationBuilder.DropColumn(name: "Status", schema: "loodoi", table: "ProjectMembers");
        }
    }
}
