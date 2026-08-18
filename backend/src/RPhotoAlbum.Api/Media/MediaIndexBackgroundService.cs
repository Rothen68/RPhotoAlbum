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
                        "Indexation périodique pCloud terminée : {Count} médias ({NewCount} nouveaux, {FailedCount} dossier(s) en échec).",
                        result.Indexed, result.NewlyIndexed, result.FailedFolders.Count);

                    // Issue #11 : déclenche automatiquement l'extraction EXIF (qui enchaîne
                    // elle-même sur la géolocalisation à sa fin, voir MediaExifService.RunAsync)
                    // uniquement si du contenu réellement nouveau a été trouvé — inutile de
                    // relancer ces jobs (potentiellement longs) à chaque cycle périodique si rien
                    // n'a changé côté pCloud.
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
