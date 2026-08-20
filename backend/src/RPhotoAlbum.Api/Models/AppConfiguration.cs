namespace RPhotoAlbum.Api.Models;

// Configuration applicative (ligne unique) — dossier parent des albums.
// Voir ARCHITECTURE.md §6.2, §9.2.
public class AppConfiguration
{
    public int Id { get; set; }
    public long? AlbumParentFolderId { get; set; }
    public string? AlbumParentFolderPath { get; set; }

    // fileId de album-structure.json (racine du dossier parent des albums) — permet de réécrire
    // en place plutôt que de redécouvrir par un listing, comme AlbumSummary.AlbumJsonFileId pour
    // album.json. Null tant que la structure (sections) n'a jamais été sauvegardée — voir issue #6.
    public long? AlbumStructureFileId { get; set; }
}
