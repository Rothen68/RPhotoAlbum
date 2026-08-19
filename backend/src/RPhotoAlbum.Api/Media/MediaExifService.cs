using Microsoft.EntityFrameworkCore;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

public record ExifJobStatus(bool Running, int Processed, int Total, DateTime? StartedAt, string? LastError);

internal record ExifResult(long Id, DateTime? DateTaken, double? Latitude, double? Longitude, int? Width, int? Height);

// Job manuel (pas périodique comme MediaIndexBackgroundService) : extrait la date de prise de
// vue réelle (EXIF DateTimeOriginal) et les coordonnées GPS des images du cache, en ne
// téléchargeant qu'un petit en-tête de chaque fichier (voir PCloudClient.DownloadPartialAsync)
// — voir plan V2 étape 9.
public class MediaExifService(IServiceScopeFactory scopeFactory, GeoLookupService geoService, ILogger<MediaExifService> logger)
{
    // Lus en tête de fichier : suffisant pour l'IFD EXIF/GPS de la quasi-totalité des JPEG et
    // RAW (TIFF-based, ex. CR2) — bien plus petit qu'un fichier RAW complet (dizaines de Mo).
    private const int ExifReadBytes = 512 * 1024;
    // Concurrence volontairement limitée (voir V2 étape 4 : pCloud peut être lent à générer un
    // lien pour un fichier jamais consulté) — ce job ne doit pas aggraver la latence perçue
    // pendant un usage normal de l'app en parallèle.
    private const int MaxConcurrency = 3;
    private const int SaveBatchSize = 50;

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static volatile bool _running;
    private static DateTime? _startedAt;
    private static string? _lastError;
    private static CancellationTokenSource? _cts;

    public async Task<ExifJobStatus> GetStatusAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
        var total = await db.MediaIndex.CountAsync(m => m.MediaType == "image", ct);
        var processed = await db.MediaIndex.CountAsync(m => m.MediaType == "image" && m.ExifProcessedAt != null, ct);
        return new ExifJobStatus(_running, processed, total, _startedAt, _lastError);
    }

    // Démarre en arrière-plan sans bloquer la requête HTTP appelante — idempotent (no-op si déjà
    // en cours). La progression elle-même est recalculée depuis la base à chaque GetStatusAsync
    // (pas de compteur en mémoire à part _running) : un redémarrage du conteneur ne perd donc
    // aucune progression déjà écrite, seul l'indicateur "en cours" repasse à faux (reprise
    // naturelle en relançant, qui re-filtre sur ExifProcessedAt == null).
    public async Task StartAsync()
    {
        if (!await RunLock.WaitAsync(0))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _running = true;
        _startedAt = DateTime.UtcNow;
        _lastError = null;

        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    // Deux phases délibérément séparées PAR LOT (SaveBatchSize) : le téléchargement + l'extraction
    // EXIF d'un lot (I/O réseau, concurrent, borné par MaxConcurrency) NE TOUCHENT JAMAIS le
    // DbContext ; l'écriture en base de ce lot se fait ensuite séquentiellement sur UN SEUL
    // DbContext, avant de passer au lot suivant — la progression (recalculée depuis la base à
    // chaque appel de statut) avance donc régulièrement au fil du job, pas seulement à la toute
    // fin.
    //
    // db est résolu ICI, depuis un scope créé pour (et qui dure) toute la durée du job — PAS
    // injecté dans le constructeur de MediaExifService. Cause racine d'un bug observé en pratique
    // (job tournant sans erreur journalisée, mais aucune date jamais persistée) : MediaExifService
    // est lui-même Scoped, donc un service injecté dans son constructeur reste lié au scope de la
    // requête HTTP /exif/start — laquelle se termine (et dispose son scope) presque immédiatement
    // après le démarrage du Task.Run en arrière-plan. Les téléchargements échouaient alors
    // silencieusement (ObjectDisposedException avalée par le catch générique d'ExtractAsync).
    //
    // PCloudClient, en revanche, N'EST PAS résolu ici mais individuellement dans chaque appel
    // concurrent d'ExtractAsync (voir plus bas) : PCloudClient dépend de PCloudTokenStore, qui
    // interroge CacheDbContext à chaque appel pCloud (le jeton n'est pas mis en cache). Un
    // DbContext EF Core n'est PAS thread-safe — le partager entre les tâches concurrentes
    // (MaxConcurrency) provoquait des erreurs aléatoires "Cannot access a disposed object:
    // SQLitePCL.sqlite3" (issue #12), le DbContext étant heurté par plusieurs threads à la fois.
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
        using var throttle = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

        try
        {
            var pendingIds = await db.MediaIndex.AsNoTracking()
                .Where(m => m.MediaType == "image" && m.ExifProcessedAt == null)
                .Select(m => new { m.Id, m.PCloudFileId })
                .ToListAsync(ct);

            foreach (var batch in pendingIds.Chunk(SaveBatchSize))
            {
                var extractTasks = batch.Select(async item =>
                {
                    await throttle.WaitAsync(ct);
                    try
                    {
                        return await ExtractAsync(item.Id, item.PCloudFileId, ct);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });

                var results = await Task.WhenAll(extractTasks);

                // Suivi (pas AsNoTracking) : on va modifier ces entités — même pattern que
                // MediaIndexService.ReindexAsync (déjà éprouvé sur ~64k entrées), mais chargées
                // lot par lot ici plutôt que toutes d'un coup (63k entités trackées en
                // permanence serait inutilement lourd vu l'écriture par lot).
                var batchIds = batch.Select(b => b.Id).ToList();
                var entries = await db.MediaIndex.Where(m => batchIds.Contains(m.Id)).ToDictionaryAsync(e => e.Id, ct);

                foreach (var result in results)
                {
                    var entry = entries[result.Id];
                    entry.DateTaken = result.DateTaken;
                    entry.Latitude = result.Latitude;
                    entry.Longitude = result.Longitude;
                    entry.Width = result.Width;
                    entry.Height = result.Height;
                    entry.ExifProcessedAt = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear(); // évite d'accumuler les entités des lots précédents en mémoire
            }

            // Issue #11 : enchaîne automatiquement sur la géolocalisation des coordonnées GPS
            // qui viennent d'être extraites — uniquement ici (fin normale de la boucle), pas
            // dans le finally ci-dessous, pour ne jamais déclencher geo après un Stop() manuel
            // ou une erreur réelle. StartAsync() est idempotent (no-op si déjà en cours).
            await geoService.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé (Stop()) — chaque lot déjà complet a été sauvegardé au fil de l'eau,
            // rien à rattraper ici ; le lot en cours au moment de l'arrêt est simplement perdu
            // (repris naturellement au prochain lancement, ExifProcessedAt y est resté nul).
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Échec du job d'extraction EXIF.");
        }
        finally
        {
            _running = false;
            RunLock.Release();
        }
    }

    private async Task<ExifResult> ExtractAsync(long id, long pCloudFileId, CancellationToken ct)
    {
        try
        {
            // Scope dédié à CET appel concurrent (voir commentaire sur RunAsync) : isole le
            // CacheDbContext utilisé par PCloudTokenStore de celui des autres extractions en
            // cours en parallèle.
            using var scope = scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<PCloudClient>();

            var bytes = await client.DownloadPartialAsync(pCloudFileId, ExifReadBytes, ct);
            using var stream = new MemoryStream(bytes);
            var directories = ImageMetadataReader.ReadMetadata(stream);

            DateTime? dateTaken = null;
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) == true)
            {
                dateTaken = dt;
            }

            double? latitude = null;
            double? longitude = null;
            var gpsDirectory = directories.OfType<GpsDirectory>().FirstOrDefault();
            // GeoLocation est une struct (GetGeoLocation() renvoie donc un GeoLocation? au sens
            // Nullable<T>) — le pattern de capture est nécessaire pour obtenir une variable non
            // nullable exploitable.
            if (gpsDirectory?.GetGeoLocation() is { IsZero: false } location)
            {
                latitude = location.Latitude;
                longitude = location.Longitude;
            }

            // Dimensions de l'image — nécessaires pour précalculer la hauteur des rangées dans la
            // virtualisation d'Album Detail (issue #20) sans avoir à mesurer chaque image après
            // rendu. PixelXDimension/PixelYDimension (EXIF SubIFD) d'abord — c'est la dimension
            // réelle telle qu'enregistrée par l'appareil, fiable aussi pour les RAW (TIFF-based) —
            // avec repli sur les marqueurs JPEG SOF si l'EXIF ne les porte pas (certains éditeurs
            // ne réécrivent pas ces tags).
            int? width = null;
            int? height = null;
            if (subIfd?.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var exifWidth) == true &&
                subIfd.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var exifHeight) == true)
            {
                width = exifWidth;
                height = exifHeight;
            }
            else if (directories.OfType<JpegDirectory>().FirstOrDefault() is { } jpegDirectory &&
                jpegDirectory.TryGetInt32(JpegDirectory.TagImageWidth, out var jpegWidth) &&
                jpegDirectory.TryGetInt32(JpegDirectory.TagImageHeight, out var jpegHeight))
            {
                width = jpegWidth;
                height = jpegHeight;
            }

            return new ExifResult(id, dateTaken, latitude, longitude, width, height);
        }
        // Ne PAS exclure OperationCanceledException ici : HttpClient lève un TaskCanceledException
        // (qui EN DÉRIVE) sur un simple timeout de requête (défaut 100s — déjà observé des liens
        // pCloud à froid mettant 30s+ à se générer, voir étape 4). Un filtre sur le type
        // d'exception laissait ce timeout se propager jusqu'au catch (OperationCanceledException)
        // de RunAsync, qui l'interprétait à tort comme un Stop() volontaire et arrêtait tout le
        // job en silence (aucune erreur journalisée) après seulement quelques dizaines
        // d'éléments — bug observé en pratique sur deux lancements successifs. On ne distingue
        // désormais l'arrêt volontaire que via l'état réel de notre propre token.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Pas d'EXIF exploitable (format non supporté, fichier corrompu, en-tête incomplet,
            // timeout réseau…) — normal pour une bonne partie de la bibliothèque, pas une erreur
            // à remonter.
            logger.LogDebug(ex, "Pas d'EXIF exploitable pour le média {Id}.", id);
            return new ExifResult(id, null, null, null, null, null);
        }
    }
}
