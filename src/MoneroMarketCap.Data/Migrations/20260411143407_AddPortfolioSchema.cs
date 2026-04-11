using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Portfolios_Coins_CoinId",
                table: "Portfolios");

            migrationBuilder.DropIndex(
                name: "IX_Portfolios_CoinId",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "CoinId",
                table: "Portfolios");

            migrationBuilder.RenameColumn(
                name: "AddedAt",
                table: "Portfolios",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "Price",
                table: "Coins",
                newName: "PriceUsd");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Portfolios",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Portfolios",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "CirculatingSupply",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<decimal>(
                name: "MarketCapUsd",
                table: "Coins",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "CoinPriceHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoinId = table.Column<int>(type: "integer", nullable: false),
                    PriceUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    CirculatingSupply = table.Column<decimal>(type: "numeric", nullable: false),
                    MarketCapUsd = table.Column<decimal>(type: "numeric", nullable: false),
                    Interval = table.Column<string>(type: "text", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinPriceHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoinPriceHistory_Coins_CoinId",
                        column: x => x.CoinId,
                        principalTable: "Coins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioCoins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    CoinId = table.Column<int>(type: "integer", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    TotalCostBasis = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioCoins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioCoins_Coins_CoinId",
                        column: x => x.CoinId,
                        principalTable: "Coins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PortfolioCoins_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CoinTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioCoinId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    PriceUsdAtTime = table.Column<decimal>(type: "numeric", nullable: false),
                    TransactedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoinTransactions_PortfolioCoins_PortfolioCoinId",
                        column: x => x.PortfolioCoinId,
                        principalTable: "PortfolioCoins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoinPriceHistory_CoinId",
                table: "CoinPriceHistory",
                column: "CoinId");

            migrationBuilder.CreateIndex(
                name: "IX_CoinTransactions_PortfolioCoinId",
                table: "CoinTransactions",
                column: "PortfolioCoinId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioCoins_CoinId",
                table: "PortfolioCoins",
                column: "CoinId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioCoins_PortfolioId_CoinId",
                table: "PortfolioCoins",
                columns: new[] { "PortfolioId", "CoinId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoinPriceHistory");

            migrationBuilder.DropTable(
                name: "CoinTransactions");

            migrationBuilder.DropTable(
                name: "PortfolioCoins");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Portfolios");

            migrationBuilder.DropColumn(
                name: "CirculatingSupply",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Coins");

            migrationBuilder.DropColumn(
                name: "MarketCapUsd",
                table: "Coins");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "Portfolios",
                newName: "AddedAt");

            migrationBuilder.RenameColumn(
                name: "PriceUsd",
                table: "Coins",
                newName: "Price");

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "Portfolios",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "CoinId",
                table: "Portfolios",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Portfolios_CoinId",
                table: "Portfolios",
                column: "CoinId");

            migrationBuilder.AddForeignKey(
                name: "FK_Portfolios_Coins_CoinId",
                table: "Portfolios",
                column: "CoinId",
                principalTable: "Coins",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
