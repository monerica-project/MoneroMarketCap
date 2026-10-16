using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class DedupeDailyHistoryUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Remove duplicate history rows BEFORE adding the unique index, otherwise
            //    the index build fails. Production had ~21.5k duplicate
            //    (CoinId, '1d', RecordedAt) groups left by the per-cycle backfill and the
            //    startup backfill racing on the same coins. Keep exactly one row per
            //    (CoinId, Interval, RecordedAt) — the one with the LOWEST Id (the first
            //    written) — and delete the rest. Applies to every interval so the index
            //    can be created whatever else is in the table.
            migrationBuilder.Sql(@"
DELETE FROM ""CoinPriceHistories"" h
USING (
    SELECT ""Id""
    FROM (
        SELECT ""Id"",
               ROW_NUMBER() OVER (
                   PARTITION BY ""CoinId"", ""Interval"", ""RecordedAt""
                   ORDER BY ""Id"") AS rn
        FROM ""CoinPriceHistories""
    ) ranked
    WHERE ranked.rn > 1
) dup
WHERE h.""Id"" = dup.""Id"";");

            // 2. Enforce one row per coin/interval/timestamp from now on. CoinId leads the
            //    index, so it also serves the CoinId foreign-key lookups the old single
            //    column index handled; create the new one before dropping the old.
            migrationBuilder.CreateIndex(
                name: "IX_CoinPriceHistories_CoinId_Interval_RecordedAt",
                table: "CoinPriceHistories",
                columns: new[] { "CoinId", "Interval", "RecordedAt" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_CoinPriceHistories_CoinId",
                table: "CoinPriceHistories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deleted duplicate rows are not restored (they were redundant copies).
            migrationBuilder.DropIndex(
                name: "IX_CoinPriceHistories_CoinId_Interval_RecordedAt",
                table: "CoinPriceHistories");

            migrationBuilder.CreateIndex(
                name: "IX_CoinPriceHistories_CoinId",
                table: "CoinPriceHistories",
                column: "CoinId");
        }
    }
}
