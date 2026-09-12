namespace RPhotoAlbum.Api.Models;

// Lightweight summary of an album in the local cache, to display the album list
// without downloading each album.json from pCloud — see ARCHITECTURE.md §6.1.
// Not the source of truth (album.json on pCloud is) but avoids a pCloud round trip
// per displayed album; rebuilt on every album write (AlbumService.PersistAsync).
public class AlbumSummary
{
    public required string Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public long AlbumFolderId { get; set; }
    public required string AlbumFolderPath { get; set; }
    public long AlbumJsonFileId { get; set; }
    public int ItemCount { get; set; }
    public long? CoverFileId { get; set; }
    public DateTime UpdatedAt { get; set; }
}
