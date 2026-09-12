using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RPhotoAlbum.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceFolderAutoIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true (not bool's CLR default) — otherwise already-configured
            // source folders would silently flip to "not auto-indexed" at this migration,
            // disabling their periodic reindexing without any explicit user action.
            migrationBuilder.AddColumn<bool>(
                name: "AutoIndex",
                table: "SourceFolders",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoIndex",
                table: "SourceFolders");
        }
    }
}
