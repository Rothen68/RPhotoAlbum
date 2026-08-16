using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Media;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Controllers;

public record RejectMediaRequest(List<long> FileIds);

[ApiController]
[Route("api/media")]
public class MediaController(
    MediaIndexService indexService,
    MediaExifService exifService,
    GeoLookupService geoService,
    CacheDbContext db,
    PCloudClient client,
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

        return Ok(new { indexed = result.Indexed, failedFolders = result.FailedFolders });
    }

    // Gallery : uniquement les médias non rejetés (§6.4, §11.4).
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

    // Comptage par jour des médias non rejetés (mêmes filtres et ordre que Source) — utilisé
    // par la Gallery pour le regroupement par date et la barre de défilement par date.
    // Volontairement léger (pas de fileId par média) : chargé une fois pour toute la
    // bibliothèque (filtrée), pas paginé.
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

    // Valeurs distinctes de localisation déjà résolues (étape 9), pour peupler les filtres de
    // la Gallery sans texte libre — un pays/région/ville mal orthographié dans un champ libre
    // ne retournerait simplement rien.
    [HttpGet("locations")]
    public async Task<IActionResult> Locations()
    {
        var query = db.MediaIndex.AsNoTracking().Where(m => !m.IsRejected);

        var countries = await query.Where(m => m.Country != null).Select(m => m.Country!).Distinct().OrderBy(c => c).ToListAsync();
        var regions = await query.Where(m => m.Region != null).Select(m => m.Region!).Distinct().OrderBy(c => c).ToListAsync();
        var cities = await query.Where(m => m.City != null).Select(m => m.City!).Distinct().OrderBy(c => c).ToListAsync();

        return Ok(new { countries, regions, cities });
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

    // --- Extraction EXIF + géolocalisation (étape 9) ---

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

    // Rejet global depuis le mode sélection de la Gallery — voir ARCHITECTURE.md §11.4.
    [HttpPost("reject")]
    public async Task<IActionResult> Reject(RejectMediaRequest request)
    {
        var rejected = 0;
        // Découpage par lots : une sélection nombreuse dépasserait la limite de paramètres
        // SQLite ("too many SQL variables") sur une clause IN unique.
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

    // Redirige vers la miniature pCloud sans exposer le jeton d'accès au frontend — voir ARCHITECTURE.md §5.4.
    // Cache-Control sur la redirection elle-même (en plus du cache mémoire côté PCloudClient) :
    // évite de repasser par le backend pour un média déjà vu dans la session (scroll aller-retour).
    [HttpGet("{fileId:long}/thumbnail")]
    public async Task<IActionResult> Thumbnail(long fileId, [FromQuery] int width = 300, [FromQuery] int height = 300, [FromQuery] bool crop = true)
    {
        try
        {
            var url = await client.GetThumbLinkAsync(fileId, width, height, crop);
            Response.Headers.CacheControl = "private, max-age=1200";
            return Redirect(url);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la récupération de la miniature pour le fichier {FileId}.", fileId);
            return NotFound();
        }
    }

    // Redirige vers le fichier original pour la lecture vidéo — voir ARCHITECTURE.md §5.4.
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
