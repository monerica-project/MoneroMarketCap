using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class TotalSupplyLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" ADD COLUMN IF NOT EXISTS ""NodeSupply"" numeric NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" ADD COLUMN IF NOT EXISTS ""NodeSupplyHeight"" numeric(20,0) NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" ADD COLUMN IF NOT EXISTS ""NodeSupplyUpdatedAt"" timestamp with time zone NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" DROP COLUMN IF EXISTS ""NodeSupply"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" DROP COLUMN IF EXISTS ""NodeSupplyHeight"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Coins"" DROP COLUMN IF EXISTS ""NodeSupplyUpdatedAt"";");
        }
    }
}