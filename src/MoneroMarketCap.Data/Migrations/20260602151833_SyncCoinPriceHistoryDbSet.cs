using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncCoinPriceHistoryDbSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoinPriceHistory_Coins_CoinId",
                table: "CoinPriceHistory");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CoinPriceHistory",
                table: "CoinPriceHistory");

            migrationBuilder.RenameTable(
                name: "CoinPriceHistory",
                newName: "CoinPriceHistories");

            migrationBuilder.RenameIndex(
                name: "IX_CoinPriceHistory_CoinId",
                table: "CoinPriceHistories",
                newName: "IX_CoinPriceHistories_CoinId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CoinPriceHistories",
                table: "CoinPriceHistories",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CoinPriceHistories_Coins_CoinId",
                table: "CoinPriceHistories",
                column: "CoinId",
                principalTable: "Coins",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CoinPriceHistories_Coins_CoinId",
                table: "CoinPriceHistories");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CoinPriceHistories",
                table: "CoinPriceHistories");

            migrationBuilder.RenameTable(
                name: "CoinPriceHistories",
                newName: "CoinPriceHistory");

            migrationBuilder.RenameIndex(
                name: "IX_CoinPriceHistories_CoinId",
                table: "CoinPriceHistory",
                newName: "IX_CoinPriceHistory_CoinId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CoinPriceHistory",
                table: "CoinPriceHistory",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CoinPriceHistory_Coins_CoinId",
                table: "CoinPriceHistory",
                column: "CoinId",
                principalTable: "Coins",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
