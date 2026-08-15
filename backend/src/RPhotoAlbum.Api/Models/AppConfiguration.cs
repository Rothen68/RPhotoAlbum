namespace RPhotoAlbum.Api.Models;

// Configuration applicative (ligne unique) — dossier parent des albums.
// Voir ARCHITECTURE.md §6.2, §9.2.
public class AppConfiguration
{
    public int Id { get; set; }
    public long? AlbumParentFolderId { get; set; }
    public string? AlbumParentFolderPath { get; set; }
}
