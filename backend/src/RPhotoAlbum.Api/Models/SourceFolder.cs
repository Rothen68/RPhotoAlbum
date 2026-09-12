namespace RPhotoAlbum.Api.Models;

// pCloud folder watched for media discovery — see ARCHITECTURE.md §6.2, §9.2.
public class SourceFolder
{
    public int Id { get; set; }
    public long PCloudFolderId { get; set; }
    public required string Label { get; set; }
    public required string Path { get; set; }

    // Included in the automatic periodic reindexing (MediaIndexBackgroundService) or
    // only on a manual "Reindex now" — useful for a frozen archive folder
    // (never changes) as opposed to an active folder (automatic upload from the phone,
    // ongoing sorting), to reduce the periodic load on pCloud and the server on
    // large folders that no longer change — see GitHub issue #28. True by default: preserves
    // current behavior for already-configured folders.
    public bool AutoIndex { get; set; } = true;
}
