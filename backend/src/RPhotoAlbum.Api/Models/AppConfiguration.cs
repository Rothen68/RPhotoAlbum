namespace RPhotoAlbum.Api.Models;

// Application configuration (single row) — albums parent folder.
// See ARCHITECTURE.md §6.2, §9.2.
public class AppConfiguration
{
    public int Id { get; set; }
    public long? AlbumParentFolderId { get; set; }
    public string? AlbumParentFolderPath { get; set; }

    // fileId of album-structure.json (root of the albums parent folder) — allows rewriting
    // in place rather than rediscovering via a listing, like AlbumSummary.AlbumJsonFileId for
    // album.json. Null as long as the structure (sections) has never been saved — see issue #6.
    public long? AlbumStructureFileId { get; set; }
}
