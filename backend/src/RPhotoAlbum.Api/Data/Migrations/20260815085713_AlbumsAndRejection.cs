using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RPhotoAlbum.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlbumsAndRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThumbnailUrl",
                table: "MediaIndex");

            migrationBuilder.RenameColumn(
                name: "AlbumJsonPath",
                table: "AlbumSummaries",
                newName: "AlbumFolderPath");

            migrationBuilder.AddColumn<bool>(
                name: "IsRejected",
                table: "MediaIndex",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "AlbumFolderId",
                table: "AlbumSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AlbumJsonFileId",
                table: "AlbumSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "CoverFileId",
                table: "AlbumSummaries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemCount",
                table: "AlbumSummaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRejected",
                table: "MediaIndex");

            migrationBuilder.DropColumn(
                name: "AlbumFolderId",
                table: "AlbumSummaries");

            migrationBuilder.DropColumn(
                name: "AlbumJsonFileId",
                table: "AlbumSummaries");

            migrationBuilder.DropColumn(
                name: "CoverFileId",
                table: "AlbumSummaries");

            migrationBuilder.DropColumn(
                name: "ItemCount",
                table: "AlbumSummaries");

            migrationBuilder.RenameColumn(
                name: "AlbumFolderPath",
                table: "AlbumSummaries",
                newName: "AlbumJsonPath");

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailUrl",
                table: "MediaIndex",
                type: "TEXT",
                nullable: true);
        }
    }
}
