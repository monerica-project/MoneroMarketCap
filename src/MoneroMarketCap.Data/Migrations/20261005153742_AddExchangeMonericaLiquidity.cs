using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeMonericaLiquidity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Liquidity",
                table: "Exchanges",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MonericaSlug",
                table: "Exchanges",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Liquidity",
                table: "Exchanges");

            migrationBuilder.DropColumn(
                name: "MonericaSlug",
                table: "Exchanges");
        }
    }
}
