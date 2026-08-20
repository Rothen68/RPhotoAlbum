namespace RPhotoAlbum.Api.Media;

// Voir issue GitHub #26 — cache disque des miniatures, éviction LRU bornée en taille (pas de TTL :
// un fileId pCloud référence un contenu immuable).
public class MediaCacheOptions
{
    public int MaxSizeMb { get; set; } = 1024;
    public int EvictionIntervalMinutes { get; set; } = 30;
}
