using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoinPriceUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PriceUpdatedAtUtc",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill: before this column existed, UpdatedAt was the only "last refreshed"
            // signal. It is the best available approximation for existing rows; the Worker
            // overwrites it with the real price time on its next cycle for every coin it prices.
            migrationBuilder.Sql(@"UPDATE ""Coins"" SET ""PriceUpdatedAtUtc"" = ""UpdatedAt"" WHERE ""PriceUpdatedAtUtc"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceUpdatedAtUtc",
                table: "Coins");
        }
    }
}
