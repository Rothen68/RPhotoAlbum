using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;

namespace RPhotoAlbum.Api.Media;

public record GeoJobStatus(bool Running, int Processed, int Total, DateTime? StartedAt, string? LastError);

// Job manuel : résout pays/région/département/ville pour les coordonnées GPS extraites par
// MediaExifService, via géocodage inverse Nominatim (OpenStreetMap) — voir plan V2 étape 9.
//
// Contrainte dure de la politique d'usage Nominatim (operations.osmfoundation.org/policies/
// nominatim) pour un script récurrent comme celui-ci : 4 requêtes/minute, User-Agent
// identifiant l'application, mise en cache obligatoire des résultats côté client. D'où le
// GeoLocationCache (coordonnées arrondies à ~100 m) consulté AVANT tout appel réseau : des
// dizaines de photos prises au même endroit ne déclenchent qu'une seule requête Nominatim,
// ce qui rend la limite de débit largement suffisante en pratique pour une photothèque
// personnelle (quelques dizaines/centaines de lieux distincts, pas un appel par photo).
public class GeoLookupService(IServiceScopeFactory scopeFactory, HttpClient httpClient, ILogger<GeoLookupService> logger)
{
    private const int CoordinatePrecision = 3; // ~111 m à l'équateur
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(15); // 4 req/min

    private static readonly SemaphoreSlim RunLock = new(1, 1);
    private static volatile bool _running;
    private static DateTime? _startedAt;
    private static string? _lastError;
    private static CancellationTokenSource? _cts;

    public async Task<GeoJobStatus> GetStatusAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();
        var total = await db.MediaIndex.CountAsync(m => m.Latitude != null, ct);
        var processed = await db.MediaIndex.CountAsync(m => m.Latitude != null && m.GeoProcessedAt != null, ct);
        return new GeoJobStatus(_running, processed, total, _startedAt, _lastError);
    }

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

    // Séquentiel (pas de concurrence comme MediaExifService) : Nominatim impose 1 seul thread,
    // jamais de requêtes en parallèle.
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CacheDbContext>();

        try
        {
            var pending = await db.MediaIndex
                .Where(m => m.Latitude != null && m.GeoProcessedAt == null)
                .ToListAsync(ct);

            var sinceLastSave = 0;

            foreach (var entry in pending)
            {
                ct.ThrowIfCancellationRequested();

                var roundedLat = Math.Round(entry.Latitude!.Value, CoordinatePrecision);
                var roundedLon = Math.Round(entry.Longitude!.Value, CoordinatePrecision);

                var cached = await db.GeoLocationCache.FirstOrDefaultAsync(
                    g => g.RoundedLatitude == roundedLat && g.RoundedLongitude == roundedLon, ct);

                if (cached is null)
                {
                    cached = await ResolveAndCacheAsync(db, roundedLat, roundedLon, ct);
                    if (cached is null)
                    {
                        // Échec (réseau, Nominatim indisponible…) : laissé non traité, retenté au
                        // prochain lancement plutôt que marqué "traité" avec un résultat vide.
                        continue;
                    }
                    await Task.Delay(RequestInterval, ct);
                }

                entry.Country = cached.Country;
                entry.Region = cached.Region;
                entry.County = cached.County;
                entry.City = cached.City;
                entry.GeoProcessedAt = DateTime.UtcNow;

                sinceLastSave++;
                if (sinceLastSave >= 20)
                {
                    await db.SaveChangesAsync(ct);
                    sinceLastSave = 0;
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await db.SaveChangesAsync(CancellationToken.None); // conserve ce qui a déjà été résolu
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Échec du job de géolocalisation.");
        }
        finally
        {
            _running = false;
            RunLock.Release();
        }
    }

    private async Task<GeoLocationCache?> ResolveAndCacheAsync(CacheDbContext db, double lat, double lon, CancellationToken ct)
    {
        try
        {
            var url = QueryHelpers.AddQueryString("https://nominatim.openstreetmap.org/reverse", new Dictionary<string, string?>
            {
                ["lat"] = lat.ToString(CultureInfo.InvariantCulture),
                ["lon"] = lon.ToString(CultureInfo.InvariantCulture),
                ["format"] = "jsonv2",
                ["addressdetails"] = "1",
                ["zoom"] = "18",
            });

            var payload = await httpClient.GetFromJsonAsync<NominatimReverseResponse>(url, ct);
            var address = payload?.Address;

            var cached = new GeoLocationCache
            {
                RoundedLatitude = lat,
                RoundedLongitude = lon,
                Country = address?.Country,
                Region = address?.State,
                County = address?.County,
                City = address?.City ?? address?.Town ?? address?.Village,
                ResolvedAt = DateTime.UtcNow,
            };
            db.GeoLocationCache.Add(cached);
            await db.SaveChangesAsync(ct);
            return cached;
        }
        // Voir le commentaire équivalent dans MediaExifService.ExtractAsync : un timeout HTTP lève
        // un TaskCanceledException (dérive d'OperationCanceledException) qu'un filtre sur le type
        // d'exception laisserait remonter jusqu'au catch (OperationCanceledException) de RunAsync,
        // arrêtant tout le job en silence sans erreur journalisée.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Échec de géocodage inverse pour {Lat},{Lon}.", lat, lon);
            return null;
        }
    }
}
