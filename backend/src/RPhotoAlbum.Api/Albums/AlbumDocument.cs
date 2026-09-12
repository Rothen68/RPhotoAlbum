namespace RPhotoAlbum.Api.Albums;

// Persisted form of album.json on pCloud — source of truth, see ARCHITECTURE.md §6.3.
public class AlbumDocument
{
    public required string Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public required AlbumFolderRef AlbumFolder { get; set; }
    public List<AlbumItemDocument> Items { get; set; } = [];
}

public class AlbumFolderRef
{
    public long FolderId { get; set; }
    public required string Path { get; set; }
}

// type = "media" | "text". For "media": MediaType/Date/Source/AlbumCopy are populated.
// For "text": only Markdown is populated.
public class AlbumItemDocument
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public string? MediaType { get; set; }
    public DateTime? Date { get; set; }
    public AlbumMediaRef? Source { get; set; }
    public AlbumMediaRef? AlbumCopy { get; set; }
    public string? Markdown { get; set; }

    // Dimensions of the original image (from EXIF extraction, see MediaIndexEntry) —
    // lets the frontend precompute the display height without measuring after render
    // (Album Detail virtualization, issue #20). Absent (null) if the media hasn't yet
    // been processed by the EXIF job, or for videos.
    public int? Width { get; set; }
    public int? Height { get; set; }

    // Date taken and location (from the EXIF/geo jobs, see MediaIndexEntry) —
    // for display in the fullscreen viewer (issue #22). Copied from
    // MediaIndexEntry at the time of AddMediaAsync, like Width/Height above: frozen at that
    // moment, not updated retroactively if the source media is processed later by the
    // EXIF/geo jobs (same accepted limitation as for #20).
    public DateTime? DateTaken { get; set; }
    public string? Country { get; set; }
    public string? Region { get; set; }
    public string? City { get; set; }

    // Number of consecutive media items (1 to 3) forming a single row in the album grid,
    // carried ONLY by the first item of the row (the following ones have a
    // non-significant value, reset to 1 by AlbumService.NormalizeRowSpans). An "anchored"
    // model rather than a relative one: see ARCHITECTURE.md / V2 plan step 7.
    public int RowSpan { get; set; } = 1;
}

public class AlbumMediaRef
{
    public long FileId { get; set; }
    public required string Path { get; set; }
    public string? Hash { get; set; }
    public required string Name { get; set; }
}
