using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPrivacyMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PrivacyMode",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PrivacyMode",
                table: "Users");
        }
    }
}
