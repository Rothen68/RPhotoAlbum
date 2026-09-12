using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

// Disk cache of thumbnail bytes (issue #26) — avoids a pCloud round trip (getthumblink
// + CDN) on every display. No TTL: a pCloud fileId references immutable content, only
// size pressure triggers an eviction (see MediaCacheEvictionBackgroundService).
public class MediaThumbnailCacheService(
    MediaCacheDirectory cacheDir, IPCloudClient client, ILogger<MediaThumbnailCacheService> logger)
{
    // All thumbnails now go through a single origin (our backend, proxied)
    // instead of being spread across several pCloud CDN hosts as with the old redirection —
    // the browser limits the number of concurrent connections per origin (~6), so a single
    // "cold" file (pCloud can take several dozen seconds to generate its
    // thumbnail, see MediaExifService) can now monopolize one of those slots and slow down
    // the whole page. A bounded delay lets it fail cleanly (404, broken icon) rather than
    // blocking indefinitely — a later reload benefits from the disk cache anyway.
    // 20s then 90s proved too short under real-world conditions (server deployment, issue
    // #26): the real ceiling was actually the one from nginx in front of us (proxy_read_timeout,
    // 60s by default — see reverse-proxy/nginx.conf), which cut the connection well before this
    // application-level delay got a chance to apply. nginx is now set to 180s; 150s
    // here stays under that ceiling so it's always THIS delay that decides first (clean
    // failure, 404) rather than nginx cutting the connection abruptly.
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
                // Marks the entry as recently used — this is what LRU eviction reads
                // (DirectoryInfo.LastWriteTimeUtc) to decide what to delete first.
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

        // pCloud always generates its thumbnails in JPEG, regardless of the source format
        // (including RAW/HEIC) — content-type hardcoded rather than a companion file per entry.
        var (bytes, _) = await client.GetThumbnailAsync(fileId, width, height, crop, timeoutCts.Token);

        try
        {
            // Atomic write (temp file + rename): avoids a truncated/corrupted .bin
            // if the process is interrupted mid-write.
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
