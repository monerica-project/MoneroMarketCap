using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class TotalSupplyLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "NodeEmissionHigh64",
                table: "Coins",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NodeEmissionLow64",
                table: "Coins",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NodeSupply",
                table: "Coins",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NodeSupplyHeight",
                table: "Coins",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NodeSupplyUpdatedAt",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NodeEmissionHigh64",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "NodeEmissionLow64",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "NodeSupply",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "NodeSupplyHeight",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "NodeSupplyUpdatedAt",
                table: "Coins");
        }
    }
}
