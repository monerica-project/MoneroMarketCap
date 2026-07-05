using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExchangeCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoinExchanges");

            migrationBuilder.CreateTable(
                name: "Exchanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    WebsiteUrl = table.Column<string>(type: "text", nullable: false),
                    AffiliateUrl = table.Column<string>(type: "text", nullable: true),
                    TorUrl = table.Column<string>(type: "text", nullable: true),
                    I2pUrl = table.Column<string>(type: "text", nullable: true),
                    CountryCode = table.Column<string>(type: "text", nullable: true),
                    Grade = table.Column<string>(type: "text", nullable: true),
                    Kyc = table.Column<string>(type: "text", nullable: true),
                    Aml = table.Column<string>(type: "text", nullable: true),
                    FeeMinPercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    FeeMaxPercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    FeeVariesByProvider = table.Column<bool>(type: "boolean", nullable: false),
                    ProcessingTimeMinutes = table.Column<int>(type: "integer", nullable: true),
                    LaunchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exchanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeCoins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeId = table.Column<int>(type: "integer", nullable: false),
                    CoinId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeCoins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeCoins_Coins_CoinId",
                        column: x => x.CoinId,
                        principalTable: "Coins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExchangeCoins_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExchangeContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ExchangeId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExchangeContacts_Exchanges_ExchangeId",
                        column: x => x.ExchangeId,
                        principalTable: "Exchanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeCoins_CoinId",
                table: "ExchangeCoins",
                column: "CoinId");

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeCoins_ExchangeId_CoinId",
                table: "ExchangeCoins",
                columns: new[] { "ExchangeId", "CoinId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeContacts_ExchangeId",
                table: "ExchangeContacts",
                column: "ExchangeId");

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_Slug",
                table: "Exchanges",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Exchanges_SortOrder",
                table: "Exchanges",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeCoins");

            migrationBuilder.DropTable(
                name: "ExchangeContacts");

            migrationBuilder.DropTable(
                name: "Exchanges");

            migrationBuilder.CreateTable(
                name: "CoinExchanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoinId = table.Column<int>(type: "integer", nullable: false),
                    Aml = table.Column<string>(type: "text", nullable: true),
                    FeeMaxPercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    FeeMinPercent = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    FeeVariesByProvider = table.Column<bool>(type: "boolean", nullable: false),
                    Grade = table.Column<string>(type: "text", nullable: true),
                    Kyc = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoinExchanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoinExchanges_Coins_CoinId",
                        column: x => x.CoinId,
                        principalTable: "Coins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoinExchanges_CoinId",
                table: "CoinExchanges",
                column: "CoinId");

            migrationBuilder.CreateIndex(
                name: "IX_CoinExchanges_CoinId_Url",
                table: "CoinExchanges",
                columns: new[] { "CoinId", "Url" },
                unique: true);
        }
    }
}
