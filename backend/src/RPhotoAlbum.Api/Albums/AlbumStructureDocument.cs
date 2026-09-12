namespace RPhotoAlbum.Api.Albums;

// "album-structure.json" manifest file, written at the root of the albums parent folder
// (AppConfiguration.AlbumParentFolderId) — not inside an album subfolder like album.json.
// Source of truth for the organization of the album list (sections + order) — see GitHub
// issue #6. Follows the same pattern as AlbumDocument: serialized to JSON, rewritten in place
// via IPCloudClient.UploadTextFileAsync, fileId tracked in AppConfiguration.AlbumStructureFileId.
public class AlbumStructureDocument
{
    public List<AlbumSectionDocument> Sections { get; set; } = [];
    public List<string> UnsectionedAlbumIds { get; set; } = [];
}

public class AlbumSectionDocument
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public List<string> AlbumIds { get; set; } = [];
}
