namespace RPhotoAlbum.Api.Models;

// Local cache (SQLite) index entry for a media file detected in a source folder.
// Rebuildable at any time from pCloud — see ARCHITECTURE.md §6.1 and §9.4.
public class MediaIndexEntry
{
    public long Id { get; set; }
    public long PCloudFileId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required string Hash { get; set; }
    public required string MediaType { get; set; } // "image" | "video"
    // DateTime UTC rather than DateTimeOffset: SQLite/EF Core cannot sort on DateTimeOffset.
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long Size { get; set; }
    public DateTime IndexedAt { get; set; }

    // Global rejection (user choice, not rebuildable data) — see ARCHITECTURE.md §6.4, §12.
    public bool IsRejected { get; set; }

    // --- EXIF (step 9) — rebuildable from the pCloud file, like the rest of the index. ---

    // Actual date taken (EXIF DateTimeOriginal), as opposed to ModifiedAt/CreatedAt
    // which reflect the pCloud upload date — see V2 finding: ~70% of the library
    // imported at once shares the same file date.
    public DateTime? DateTaken { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Marks that the EXIF extraction job has run, whether or not data was found —
    // distinguishes "never processed" from "processed, nothing to extract" (otherwise a JPEG
    // with no EXIF would be reprocessed indefinitely on every job run).
    public DateTime? ExifProcessedAt { get; set; }

    // Result of the reverse geocoding (Nominatim) of the coordinates above, via GeoLocationCache.
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? County { get; set; }
    public string? City { get; set; }
    public DateTime? GeoProcessedAt { get; set; }
}
