using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RPhotoAlbum.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumStructureFileId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AlbumStructureFileId",
                table: "AppConfigurations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlbumStructureFileId",
                table: "AppConfigurations");
        }
    }
}
