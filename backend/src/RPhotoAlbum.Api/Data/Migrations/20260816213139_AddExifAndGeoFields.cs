using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RPhotoAlbum.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExifAndGeoFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "County",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DateTaken",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExifProcessedAt",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeoProcessedAt",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "MediaIndex",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "MediaIndex",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GeoLocationCache",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoundedLatitude = table.Column<double>(type: "REAL", nullable: false),
                    RoundedLongitude = table.Column<double>(type: "REAL", nullable: false),
                    Country = table.Column<string>(type: "TEXT", nullable: true),
                    Region = table.Column<string>(type: "TEXT", nullable: true),
                    County = table.Column<string>(type: "TEXT", nullable: true),
                    City = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GeoLocationCache", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GeoLocationCache_RoundedLatitude_RoundedLongitude",
                table: "GeoLocationCache",
                columns: new[] { "RoundedLatitude", "RoundedLongitude" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GeoLocationCache");

            migrationBuilder.DropColumn(
                name: "City",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "County",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "DateTaken",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "ExifProcessedAt",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "GeoProcessedAt",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "MediaIndex");
        }
    }
}
