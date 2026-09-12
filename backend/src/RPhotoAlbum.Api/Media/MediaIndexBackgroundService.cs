using Microsoft.Extensions.Options;

namespace RPhotoAlbum.Api.Media;

// Periodic reindexing of the source folders — see ARCHITECTURE.md §9.4, "Media pipeline".
public class MediaIndexBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<IndexingOptions> options,
    ILogger<MediaIndexBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var indexService = scope.ServiceProvider.GetRequiredService<MediaIndexService>();
                // autoOnly: ignores source folders marked "not auto-indexed" (issue #28) —
                // this periodic pass must stay lightweight, unlike "Reindex now"
                // (an explicit manual trigger, which checks everything).
                var result = await indexService.ReindexAsync(stoppingToken, autoOnly: true);
                if (!result.IsAlreadyRunning)
                {
                    logger.LogInformation(
                        "Indexation périodique pCloud terminée : {Count} médias ({NewCount} nouveaux, {FailedCount} dossier(s) en échec).",
                        result.Indexed, result.NewlyIndexed, result.FailedFolders.Count);

                    // Issue #11: automatically triggers EXIF extraction (which itself chains
                    // into geolocation at its end, see MediaExifService.RunAsync)
                    // only if genuinely new content was found — no point
                    // relaunching these (potentially long) jobs on every periodic cycle if
                    // nothing changed on the pCloud side.
                    if (result.NewlyIndexed > 0)
                    {
                        var exifService = scope.ServiceProvider.GetRequiredService<MediaExifService>();
                        await exifService.StartAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Échec du cycle d'indexation périodique pCloud.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
