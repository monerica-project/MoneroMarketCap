using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExchangeSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Exchanges",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "Exchanges");
        }
    }
}
