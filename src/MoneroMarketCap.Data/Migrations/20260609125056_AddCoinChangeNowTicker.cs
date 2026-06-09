using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCoinChangeNowTicker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangeNowTicker",
                table: "Coins",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeNowTicker",
                table: "Coins");
        }
    }
}
