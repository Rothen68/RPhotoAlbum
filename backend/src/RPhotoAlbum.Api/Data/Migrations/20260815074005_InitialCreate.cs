using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RPhotoAlbum.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlbumSummaries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumJsonPath = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumSummaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlbumParentFolderId = table.Column<long>(type: "INTEGER", nullable: true),
                    AlbumParentFolderPath = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaIndex",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PCloudFileId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ThumbnailUrl = table.Column<string>(type: "TEXT", nullable: true),
                    IndexedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaIndex", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PCloudConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hostname = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PCloudConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PCloudFolderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaIndex_PCloudFileId",
                table: "MediaIndex",
                column: "PCloudFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceFolders_PCloudFolderId",
                table: "SourceFolders",
                column: "PCloudFolderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumSummaries");

            migrationBuilder.DropTable(
                name: "AppConfigurations");

            migrationBuilder.DropTable(
                name: "MediaIndex");

            migrationBuilder.DropTable(
                name: "PCloudConnections");

            migrationBuilder.DropTable(
                name: "SourceFolders");
        }
    }
}
