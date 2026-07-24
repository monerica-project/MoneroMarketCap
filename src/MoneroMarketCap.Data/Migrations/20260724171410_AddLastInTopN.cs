using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoneroMarketCap.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLastInTopN : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastInTopNUtc",
                table: "Coins",
                type: "timestamp with time zone",
                nullable: true);

            // Coins currently in the top N are, by definition, in it right now — seed the
            // stamp so their grace window is measured from today rather than starting null.
            // (The next worker cycle would stamp them anyway; this makes the state correct
            // immediately after the migration.)
            migrationBuilder.Sql(
                @"UPDATE ""Coins"" SET ""LastInTopNUtc"" = NOW() AT TIME ZONE 'UTC' WHERE ""IsActive"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastInTopNUtc",
                table: "Coins");
        }
    }
}
