using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

// Cache disque des octets de miniature (issue #26) — évite un aller-retour pCloud (getthumblink
// + CDN) à chaque affichage. Pas de TTL : un fileId pCloud référence un contenu immuable, seule
// la pression de taille déclenche une éviction (voir MediaCacheEvictionBackgroundService).
public class MediaThumbnailCacheService(
    MediaCacheDirectory cacheDir, IPCloudClient client, ILogger<MediaThumbnailCacheService> logger)
{
    public async Task<(byte[] Bytes, string ContentType)> GetAsync(
        long fileId, int width, int height, bool crop, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir.Path);
        var key = $"{fileId}_{width}x{height}_{(crop ? "c" : "n")}";
        var bytesPath = Path.Combine(cacheDir.Path, key + ".bin");

        if (File.Exists(bytesPath))
        {
            try
            {
                var cached = await File.ReadAllBytesAsync(bytesPath, ct);
                // Marque l'entrée comme récemment utilisée — c'est ce que l'éviction LRU lit
                // (DirectoryInfo.LastWriteTimeUtc) pour décider quoi supprimer en premier.
                File.SetLastWriteTimeUtc(bytesPath, DateTime.UtcNow);
                return (cached, "image/jpeg");
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Cache miniature illisible pour {FileId}, re-téléchargement depuis pCloud.", fileId);
            }
        }

        // pCloud génère toujours ses miniatures en JPEG, quel que soit le format source (y
        // compris RAW/HEIC) — content-type codé en dur plutôt qu'un fichier compagnon par entrée.
        var (bytes, _) = await client.GetThumbnailAsync(fileId, width, height, crop, ct);

        try
        {
            // Écriture atomique (fichier temporaire + rename) : évite un .bin tronqué/corrompu
            // si le process est interrompu en plein milieu de l'écriture.
            var tmpPath = bytesPath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, bytes, ct);
            File.Move(tmpPath, bytesPath, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Échec d'écriture du cache miniature pour {FileId} — servi sans mise en cache.", fileId);
        }

        return (bytes, "image/jpeg");
    }
}
