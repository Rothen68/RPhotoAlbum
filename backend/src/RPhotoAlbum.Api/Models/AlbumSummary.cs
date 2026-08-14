namespace RPhotoAlbum.Api.Models;

// Résumé léger d'un album dans le cache local, pour afficher la liste d'albums
// sans télécharger chaque album.json depuis pCloud — voir ARCHITECTURE.md §6.1.
public class AlbumSummary
{
    public required string Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public required string AlbumJsonPath { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
