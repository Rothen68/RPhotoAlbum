using Microsoft.Extensions.Options;

namespace RPhotoAlbum.Api.Media;

// Periodic LRU eviction of the thumbnail disk cache (issue #26) — same structure as
// MediaIndexBackgroundService (PeriodicTimer, one pass per tick, a failed pass doesn't stop
// the service).
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

    // internal (not private) + InternalsVisibleTo(RPhotoAlbum.Api.Tests), same pattern as
    // AlbumService.NormalizeRowSpans — pure logic testable without running the whole BackgroundService.
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

        // The least recently served (LastWriteTimeUtc, updated on every cache hit by
        // MediaThumbnailCacheService) go first.
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
                // Locked by a concurrent read — retried on the next pass.
            }
        }
    }

    // Current cache size — reused by MediaController to display usage
    // (issue #27), same counting logic as Evict above so the two always stay consistent.
    internal static long ComputeUsedBytes(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.bin").Sum(f => f.Length) : 0;
}
