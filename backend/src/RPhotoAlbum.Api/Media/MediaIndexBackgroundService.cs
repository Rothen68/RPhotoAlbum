using Microsoft.Extensions.Options;

namespace RPhotoAlbum.Api.Media;

// Réindexation périodique des dossiers source — voir ARCHITECTURE.md §9.4, "Pipeline média".
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
                var result = await indexService.ReindexAsync(stoppingToken);
                if (!result.IsAlreadyRunning)
                {
                    logger.LogInformation(
                        "Indexation périodique pCloud terminée : {Count} médias ({FailedCount} dossier(s) en échec).",
                        result.Indexed, result.FailedFolders.Count);
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
