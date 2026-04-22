using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class SyncNodeColumnState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" DROP COLUMN IF EXISTS ""NodeEmissionHigh64"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" DROP COLUMN IF EXISTS ""NodeEmissionLow64"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "NodeEmissionHigh64",
                table: "Coins",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "NodeEmissionLow64",
                table: "Coins",
                type: "numeric(20,0)",
                nullable: true);
        }
    }
}