using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;

namespace RPhotoAlbum.Api.Media;

public record GeoJobStatus(bool Running, int Processed, int Total, DateTime? StartedAt, string? LastError);

// Manual job: resolves country/region/county/city for the GPS coordinates extracted by
// MediaExifService, via Nominatim (OpenStreetMap) reverse geocoding — see V2 plan step 9.
//
// Hard constraint from the Nominatim usage policy (operations.osmfoundation.org/policies/
// nominatim) for a recurring script like this one: 4 requests/minute, a User-Agent
// identifying the application, mandatory client-side caching of results. Hence the
// GeoLocationCache (coordinates rounded to ~100 m) consulted BEFORE any network call: dozens
// of photos taken at the same spot trigger only a single Nominatim request, which makes the
// rate limit plenty sufficient in practice for a personal photo library (a few dozen/hundred
// distinct places, not one call per photo).
public class GeoLookupService(IServiceScopeFactory scopeFactory, HttpClient httpClient, ILogger<GeoLookupService> logger)
{
    private const int CoordinatePrecision = 3; // ~111 m at the equator
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

    // Sequential (no concurrency like MediaExifService): Nominatim requires a single thread,
    // never parallel requests.
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
                        // Failure (network, Nominatim unavailable…): left unprocessed, retried on
                        // the next run rather than marked "processed" with an empty result.
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
            await db.SaveChangesAsync(CancellationToken.None); // keeps what has already been resolved
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
        // See the equivalent comment in MediaExifService.ExtractAsync: an HTTP timeout throws a
        // TaskCanceledException (derives from OperationCanceledException) that a filter on the
        // exception type would let bubble up to RunAsync's catch (OperationCanceledException),
        // silently stopping the whole job with no logged error.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Échec de géocodage inverse pour {Lat},{Lon}.", lat, lon);
            return null;
        }
    }
}
