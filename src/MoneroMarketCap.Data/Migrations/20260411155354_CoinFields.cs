using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class CoinFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Ath",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AthChangePercentage",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "AthDate",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Atl",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AtlChangePercentage",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "AtlDate",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FullyDilutedValuation",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "High24h",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "Low24h",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MarketCapRank",
                table: "Coins",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxSupply",
                table: "Coins",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalSupply",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalVolume",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ath",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "AthChangePercentage",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "AthDate",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "Atl",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "AtlChangePercentage",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "AtlDate",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "FullyDilutedValuation",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "High24h",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "Low24h",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "MarketCapRank",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "MaxSupply",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "TotalSupply",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "TotalVolume",
                table: "Coins");
        }
    }
}
