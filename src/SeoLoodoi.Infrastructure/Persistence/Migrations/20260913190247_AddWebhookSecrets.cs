using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookSecret",
                schema: "loodoi",
                table: "AlertRules",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecret",
                schema: "loodoi",
                table: "AlertDeliveries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebhookSecret",
                schema: "loodoi",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "WebhookSecret",
                schema: "loodoi",
                table: "AlertDeliveries");
        }
    }
}
