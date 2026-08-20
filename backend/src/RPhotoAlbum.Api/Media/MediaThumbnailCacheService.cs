using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

// Cache disque des octets de miniature (issue #26) — évite un aller-retour pCloud (getthumblink
// + CDN) à chaque affichage. Pas de TTL : un fileId pCloud référence un contenu immuable, seule
// la pression de taille déclenche une éviction (voir MediaCacheEvictionBackgroundService).
public class MediaThumbnailCacheService(
    MediaCacheDirectory cacheDir, IPCloudClient client, ILogger<MediaThumbnailCacheService> logger)
{
    // Toutes les miniatures passent maintenant par une seule origine (notre backend, proxifié)
    // au lieu de se répartir sur plusieurs hôtes CDN pCloud comme avec l'ancienne redirection —
    // le navigateur limite le nombre de connexions concurrentes par origine (~6), donc un seul
    // fichier "à froid" (pCloud peut mettre plusieurs dizaines de secondes à générer sa
    // miniature, voir MediaExifService) peut désormais monopoliser un de ces créneaux et ralentir
    // toute la page. Un délai borné laisse échouer proprement (404, icône cassée) plutôt que de
    // bloquer indéfiniment — la relecture ultérieure profite du cache disque de toute façon.
    // 20s puis 90s se sont avérés trop courts en conditions réelles (déploiement serveur, issue
    // #26) : le vrai plafond était en fait celui de nginx devant nous (proxy_read_timeout, 60s
    // par défaut — voir reverse-proxy/nginx.conf), qui coupait la connexion bien avant que ce
    // délai applicatif n'ait sa chance de s'appliquer. nginx est maintenant réglé à 180s ; 150s
    // ici reste sous ce plafond pour que ce soit toujours CE délai qui tranche en premier (échec
    // propre, 404) plutôt qu'nginx qui coupe brutalement.
    private static readonly TimeSpan ThumbnailFetchTimeout = TimeSpan.FromSeconds(150);

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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ThumbnailFetchTimeout);

        // pCloud génère toujours ses miniatures en JPEG, quel que soit le format source (y
        // compris RAW/HEIC) — content-type codé en dur plutôt qu'un fichier compagnon par entrée.
        var (bytes, _) = await client.GetThumbnailAsync(fileId, width, height, crop, timeoutCts.Token);

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
