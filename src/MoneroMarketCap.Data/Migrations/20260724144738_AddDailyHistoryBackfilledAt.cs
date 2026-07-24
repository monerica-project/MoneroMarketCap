using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyHistoryBackfilledAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DailyHistoryBackfilledAtUtc",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);

            // Coins that already hold a full year of daily history are treated as
            // already backfilled, so upgrading doesn't re-request all of them from
            // CoinGecko on the first cycle. Anything thinner stays NULL and gets its
            // one backfill, which is exactly the old intent minus the retry loop.
            migrationBuilder.Sql(@"
                UPDATE ""Coins"" SET ""DailyHistoryBackfilledAtUtc"" = NOW() AT TIME ZONE 'UTC'
                WHERE ""Id"" IN (
                    SELECT ""CoinId"" FROM ""CoinPriceHistories""
                    WHERE ""Interval"" = '1d'
                    GROUP BY ""CoinId""
                    HAVING COUNT(*) >= 300
                );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DailyHistoryBackfilledAtUtc",
                table: "Coins");
        }
    }
}
