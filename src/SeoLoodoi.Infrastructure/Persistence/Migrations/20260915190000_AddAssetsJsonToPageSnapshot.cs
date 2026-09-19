using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetsJsonToPageSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssetsJson",
                schema: "loodoi",
                table: "PageSnapshots",
                type: "text",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssetsJson",
                schema: "loodoi",
                table: "PageSnapshots");
        }
    }
}
