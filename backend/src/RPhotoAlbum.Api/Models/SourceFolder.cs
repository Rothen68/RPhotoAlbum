namespace RPhotoAlbum.Api.Models;

// Dossier pCloud surveillé pour la recherche de médias — voir ARCHITECTURE.md §6.2, §9.2.
public class SourceFolder
{
    public int Id { get; set; }
    public long PCloudFolderId { get; set; }
    public required string Label { get; set; }
    public required string Path { get; set; }
}
