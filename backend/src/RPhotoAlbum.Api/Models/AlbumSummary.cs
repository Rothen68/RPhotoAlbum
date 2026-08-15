namespace RPhotoAlbum.Api.Models;

// Résumé léger d'un album dans le cache local, pour afficher la liste d'albums
// sans télécharger chaque album.json depuis pCloud — voir ARCHITECTURE.md §6.1.
// N'est pas la source de vérité (album.json sur pCloud l'est) mais évite un aller-retour
// pCloud par album affiché ; reconstruit à chaque écriture d'album (AlbumService.PersistAsync).
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
