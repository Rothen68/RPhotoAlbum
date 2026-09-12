using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Media;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Controllers;

public record RejectMediaRequest(List<long> FileIds);
public record MediaCacheStatusDto(long UsedBytes, long MaxBytes);

[ApiController]
[Route("api/media")]
public class MediaController(
    MediaIndexService indexService,
    MediaExifService exifService,
    GeoLookupService geoService,
    CacheDbContext db,
    IPCloudClient client,
    MediaThumbnailCacheService thumbnailCache,
    MediaCacheDirectory cacheDir,
    IOptions<MediaCacheOptions> cacheOptions,
    ILogger<MediaController> logger) : ControllerBase
{
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken ct)
    {
        var result = await indexService.ReindexAsync(ct);
        if (result.IsAlreadyRunning)
        {
            return Conflict(new { error = "Une indexation est déjà en cours." });
        }

        // Issue #11: same automatic trigger as the periodic reindex (MediaIndexBackgroundService)
        // — otherwise a manual reindex from Configuration would behave differently from the
        // periodic one, which makes no sense from the user's point of view (both call the same
        // MediaIndexService.ReindexAsync).
        if (result.NewlyIndexed > 0)
        {
            await exifService.StartAsync();
        }

        return Ok(new { indexed = result.Indexed, newlyIndexed = result.NewlyIndexed, failedFolders = result.FailedFolders });
    }

    // Thumbnail disk cache usage (issue #27) — displayed under the Indexing section of the
    // Configuration page, next to the manual reindex button.
    [HttpGet("cache-status")]
    public IActionResult CacheStatus()
    {
        var usedBytes = MediaCacheEvictionBackgroundService.ComputeUsedBytes(cacheDir.Path);
        var maxBytes = (long)Math.Max(1, cacheOptions.Value.MaxSizeMb) * 1024 * 1024;
        return Ok(new MediaCacheStatusDto(usedBytes, maxBytes));
    }

    // Gallery: only non-rejected media (§6.4, §11.4).
    [HttpGet("source")]
    public async Task<IActionResult> Source(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? mediaType = null,
        [FromQuery] long? minSize = null,
        [FromQuery] long? maxSize = null,
        [FromQuery] string? country = null,
        [FromQuery] string? region = null,
        [FromQuery] string? city = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = BuildFilteredQuery(search, mediaType, minSize, maxSize, country, region, city);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    // Per-day count of non-rejected media (same filters and order as Source) — used by the
    // Gallery for date grouping and the date scrollbar.
    // Deliberately lightweight (no fileId per media item): loaded once for the entire
    // (filtered) library, not paginated.
    [HttpGet("date-groups")]
    public async Task<IActionResult> DateGroups(
        [FromQuery] string? search = null,
        [FromQuery] string? mediaType = null,
        [FromQuery] long? minSize = null,
        [FromQuery] long? maxSize = null,
        [FromQuery] string? country = null,
        [FromQuery] string? region = null,
        [FromQuery] string? city = null)
    {
        var dates = await BuildFilteredQuery(search, mediaType, minSize, maxSize, country, region, city)
            .Select(m => m.DateTaken ?? m.ModifiedAt ?? m.CreatedAt ?? m.IndexedAt)
            .ToListAsync();

        var groups = dates
            .GroupBy(d => d.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), count = g.Count() });

        return Ok(groups);
    }

    // Distinct location values already resolved (step 9), to populate the Gallery filters
    // without free text — a misspelled country/region/city in a free-text field would simply
    // return nothing. Distinct combinations (not three separate lists): the frontend needs to
    // know which regions/cities belong to which country in order to offer dependent filters
    // (selecting a country restricts the regions/cities offered to that country, rather than
    // allowing inconsistent combinations — issue #10).
    [HttpGet("locations")]
    public async Task<IActionResult> Locations()
    {
        var combos = await db.MediaIndex.AsNoTracking()
            .Where(m => !m.IsRejected && m.Country != null)
            .Select(m => new { m.Country, m.Region, m.City })
            .Distinct()
            .ToListAsync();

        return Ok(combos.Select(c => new { country = c.Country, region = c.Region, city = c.City }));
    }

    private IOrderedQueryable<MediaIndexEntry> BuildFilteredQuery(
        string? search, string? mediaType, long? minSize, long? maxSize, string? country, string? region, string? city)
    {
        var query = db.MediaIndex.AsNoTracking().Where(m => !m.IsRejected);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(m => m.Name.Contains(search));
        }
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            query = query.Where(m => m.MediaType == mediaType);
        }
        if (minSize.HasValue)
        {
            query = query.Where(m => m.Size >= minSize.Value);
        }
        if (maxSize.HasValue)
        {
            query = query.Where(m => m.Size <= maxSize.Value);
        }
        if (!string.IsNullOrWhiteSpace(country))
        {
            query = query.Where(m => m.Country == country);
        }
        if (!string.IsNullOrWhiteSpace(region))
        {
            query = query.Where(m => m.Region == region);
        }
        if (!string.IsNullOrWhiteSpace(city))
        {
            query = query.Where(m => m.City == city);
        }

        return query.OrderByDescending(m => m.DateTaken ?? m.ModifiedAt ?? m.CreatedAt ?? m.IndexedAt);
    }

    // --- EXIF extraction + geolocation (step 9) ---

    [HttpPost("exif/start")]
    public async Task<IActionResult> StartExif()
    {
        await exifService.StartAsync();
        return Ok();
    }

    [HttpGet("exif/status")]
    public async Task<IActionResult> ExifStatus(CancellationToken ct) => Ok(await exifService.GetStatusAsync(ct));

    [HttpPost("exif/stop")]
    public IActionResult StopExif()
    {
        exifService.Stop();
        return Ok();
    }

    [HttpPost("geo/start")]
    public async Task<IActionResult> StartGeo()
    {
        await geoService.StartAsync();
        return Ok();
    }

    [HttpGet("geo/status")]
    public async Task<IActionResult> GeoStatus(CancellationToken ct) => Ok(await geoService.GetStatusAsync(ct));

    [HttpPost("geo/stop")]
    public IActionResult StopGeo()
    {
        geoService.Stop();
        return Ok();
    }

    // Bulk rejection from the Gallery's selection mode — see ARCHITECTURE.md §11.4.
    [HttpPost("reject")]
    public async Task<IActionResult> Reject(RejectMediaRequest request)
    {
        var rejected = 0;
        // Chunked processing: a large selection would exceed the SQLite parameter limit
        // ("too many SQL variables") on a single IN clause.
        foreach (var chunk in request.FileIds.Distinct().Chunk(500))
        {
            var entries = await db.MediaIndex.Where(m => chunk.Contains(m.PCloudFileId)).ToListAsync();
            foreach (var entry in entries)
            {
                entry.IsRejected = true;
            }
            rejected += entries.Count;
        }

        await db.SaveChangesAsync();

        return Ok(new { rejected });
    }

    // Proxies the thumbnail bytes through our own origin, with disk caching
    // (MediaThumbnailCacheService) — no longer a simple redirect to pCloud on every request, see
    // issue #26. Cache-Control in addition to the disk cache: avoids even going back through the
    // backend for media already seen in the session (scrolling back and forth).
    [HttpGet("{fileId:long}/thumbnail")]
    public async Task<IActionResult> Thumbnail(
        long fileId, [FromQuery] int width = 300, [FromQuery] int height = 300, [FromQuery] bool crop = true, CancellationToken ct = default)
    {
        try
        {
            var (bytes, contentType) = await thumbnailCache.GetAsync(fileId, width, height, crop, ct);
            Response.Headers.CacheControl = "private, max-age=1200";
            return File(bytes, contentType);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la récupération de la miniature pour le fichier {FileId}.", fileId);
            return NotFound();
        }
    }

    // Download of the original file from the fullscreen view (Gallery and Album) — issue #1.
    // Unlike /stream (redirect), here we proxy the content: a redirect to a cross-origin pCloud
    // link ignores the `download` attribute of an <a> and simply opens/displays the file instead
    // of downloading it. By going through our own origin with an explicit
    // Content-Disposition: attachment, the browser always triggers a download, regardless of the
    // file type.
    [HttpGet("{fileId:long}/download")]
    public async Task<IActionResult> Download(long fileId, CancellationToken ct)
    {
        try
        {
            // MediaIndex only covers the configured source folders: media displayed from an
            // album (copied into the album folder, see AlbumService) is not present there — in
            // that case we fall back directly to pCloud for the file name.
            var entry = await db.MediaIndex.AsNoTracking().FirstOrDefaultAsync(m => m.PCloudFileId == fileId, ct);
            var name = entry?.Name ?? await client.GetFileNameAsync(fileId);

            var (bytes, contentType) = await client.DownloadAsync(fileId, ct);
            return File(bytes, contentType ?? "application/octet-stream", name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec du téléchargement du fichier {FileId}.", fileId);
            return NotFound();
        }
    }

    // Redirects to the original file for video playback — see ARCHITECTURE.md §5.4.
    [HttpGet("{fileId:long}/stream")]
    public async Task<IActionResult> Stream(long fileId)
    {
        try
        {
            var url = await client.GetFileLinkAsync(fileId);
            Response.Headers.CacheControl = "private, max-age=1200";
            return Redirect(url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la récupération du flux pour le fichier {FileId}.", fileId);
            return NotFound();
        }
    }
}
