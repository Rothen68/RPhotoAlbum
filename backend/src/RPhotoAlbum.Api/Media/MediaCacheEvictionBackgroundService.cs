using Microsoft.Extensions.Options;

namespace RPhotoAlbum.Api.Media;

// Éviction LRU périodique du cache disque des miniatures (issue #26) — même charpente que
// MediaIndexBackgroundService (PeriodicTimer, un passage par tick, échec d'un passage n'arrête
// pas le service).
public class MediaCacheEvictionBackgroundService(
    MediaCacheDirectory cacheDir, IOptions<MediaCacheOptions> options,
    ILogger<MediaCacheEvictionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.EvictionIntervalMinutes));
        using var timer = new PeriodicTimer(interval);
        var maxBytes = (long)Math.Max(1, options.Value.MaxSizeMb) * 1024 * 1024;

        do
        {
            try
            {
                Evict(cacheDir.Path, maxBytes);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du passage d'éviction du cache miniatures.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // internal (pas private) + InternalsVisibleTo(RPhotoAlbum.Api.Tests), même pattern que
    // AlbumService.NormalizeRowSpans — logique pure testable sans lancer tout le BackgroundService.
    internal static void Evict(string dir, long maxBytes)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        var files = new DirectoryInfo(dir).GetFiles("*.bin");
        var totalSize = files.Sum(f => f.Length);
        if (totalSize <= maxBytes)
        {
            return;
        }

        // Les moins récemment servies (LastWriteTimeUtc, mis à jour à chaque hit de cache par
        // MediaThumbnailCacheService) partent en premier.
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (totalSize <= maxBytes)
            {
                break;
            }

            totalSize -= file.Length;
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // Verrouillée par une lecture concurrente — retentée au prochain passage.
            }
        }
    }
}
