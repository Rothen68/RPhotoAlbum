namespace RPhotoAlbum.Api.Albums;

// Forme persistée de album.json sur pCloud — source de vérité, voir ARCHITECTURE.md §6.3.
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

// type = "media" | "text". Pour "media" : MediaType/Date/Source/AlbumCopy renseignés.
// Pour "text" : seul Markdown est renseigné.
public class AlbumItemDocument
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public string? MediaType { get; set; }
    public DateTime? Date { get; set; }
    public AlbumMediaRef? Source { get; set; }
    public AlbumMediaRef? AlbumCopy { get; set; }
    public string? Markdown { get; set; }
}

public class AlbumMediaRef
{
    public long FileId { get; set; }
    public required string Path { get; set; }
    public string? Hash { get; set; }
    public required string Name { get; set; }
}
