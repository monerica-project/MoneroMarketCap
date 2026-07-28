using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoinDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Coins",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DescriptionUpdatedAtUtc",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "DescriptionUpdatedAtUtc",
                table: "Coins");
        }
    }
}
