namespace RPhotoAlbum.Api.Albums;

// Fichier manifeste "album-structure.json", écrit à la racine du dossier parent des albums
// (AppConfiguration.AlbumParentFolderId) — pas dans un sous-dossier d'album comme album.json.
// Source de vérité de l'organisation de la liste des albums (sections + ordre) — voir issue
// GitHub #6. Suit le même pattern que AlbumDocument : sérialisé en JSON, réécrit en place via
// IPCloudClient.UploadTextFileAsync, fileId suivi dans AppConfiguration.AlbumStructureFileId.
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
