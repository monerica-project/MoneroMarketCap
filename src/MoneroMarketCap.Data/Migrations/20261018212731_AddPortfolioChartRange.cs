using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioChartRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PortfolioChartRange",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "all");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PortfolioChartRange",
                table: "Users");
        }
    }
}
