using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// P0 — Billing / entitlement hardening.
    /// <para>BillingCheckoutSessions: server-side single-use nonces for signed checkout return state (anti-replay).</para>
    /// <para>ProcessedBillingEvents: idempotency ledger so a billing webhook EventId is applied at most once.</para>
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260923120000_AddBillingCheckoutSessionsAndEvents")]
    public partial class AddBillingCheckoutSessionsAndEvents : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BillingCheckoutSessions",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_BillingCheckoutSessions", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_BillingCheckoutSessions_Nonce", schema: "loodoi", table: "BillingCheckoutSessions", column: "Nonce", unique: true);
            migrationBuilder.CreateIndex(name: "IX_BillingCheckoutSessions_UserId_CreatedAt", schema: "loodoi", table: "BillingCheckoutSessions", columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateTable(
                name: "ProcessedBillingEvents",
                schema: "loodoi",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_ProcessedBillingEvents", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_ProcessedBillingEvents_EventId", schema: "loodoi", table: "ProcessedBillingEvents", column: "EventId", unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ProcessedBillingEvents", schema: "loodoi");
            migrationBuilder.DropTable(name: "BillingCheckoutSessions", schema: "loodoi");
        }
    }
}
