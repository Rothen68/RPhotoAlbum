namespace RPhotoAlbum.Api.Models;

// Dossier pCloud surveillé pour la recherche de médias — voir ARCHITECTURE.md §6.2, §9.2.
public class SourceFolder
{
    public int Id { get; set; }
    public long PCloudFolderId { get; set; }
    public required string Label { get; set; }
    public required string Path { get; set; }

    // Inclus dans la réindexation périodique automatique (MediaIndexBackgroundService) ou
    // seulement lors d'un "Réindexer maintenant" manuel — utile pour un dossier d'archive figé
    // (n'évolue jamais) par opposition à un dossier actif (upload automatique du téléphone,
    // classement en cours), pour réduire la charge périodique sur pCloud et le serveur sur les
    // gros dossiers qui ne bougent plus — voir issue GitHub #28. Vrai par défaut : préserve le
    // comportement actuel pour les dossiers déjà configurés.
    public bool AutoIndex { get; set; } = true;
}
