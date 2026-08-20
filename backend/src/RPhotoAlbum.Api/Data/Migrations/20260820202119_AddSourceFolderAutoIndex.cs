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
            // defaultValue: true (pas le défaut CLR de bool) — sinon les dossiers sources déjà
            // configurés basculeraient silencieusement en "non auto-indexé" à cette migration,
            // désactivant leur réindexation périodique sans action explicite de l'utilisateur.
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
