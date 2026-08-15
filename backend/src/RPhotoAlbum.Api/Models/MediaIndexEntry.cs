namespace RPhotoAlbum.Api.Models;

// Entrée d'index du cache local (SQLite) pour un média détecté dans un dossier source.
// Reconstructible à tout moment depuis pCloud — voir ARCHITECTURE.md §6.1 et §9.4.
public class MediaIndexEntry
{
    public long Id { get; set; }
    public long PCloudFileId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required string Hash { get; set; }
    public required string MediaType { get; set; } // "image" | "video"
    // DateTime UTC plutôt que DateTimeOffset : SQLite/EF Core ne sait pas trier sur DateTimeOffset.
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long Size { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTime IndexedAt { get; set; }
}
