using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SeoLoodoi.Infrastructure.Persistence;

#nullable disable

namespace SeoLoodoi.Infrastructure.Persistence.Migrations;

public partial class AddCrawlConcurrencyStamp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ConcurrencyStamp",
            schema: "loodoi",
            table: "Crawls",
            type: "text",
            nullable: false,
            defaultValue: "");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ConcurrencyStamp",
            schema: "loodoi",
            table: "Crawls");
    }
}
