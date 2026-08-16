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
public class MediaController(MediaIndexService indexService, CacheDbContext db, PCloudClient client, ILogger<MediaController> logger) : ControllerBase
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
        [FromQuery] long? maxSize = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = BuildFilteredQuery(search, mediaType, minSize, maxSize);

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
        [FromQuery] long? maxSize = null)
    {
        var dates = await BuildFilteredQuery(search, mediaType, minSize, maxSize)
            .Select(m => m.ModifiedAt ?? m.CreatedAt ?? m.IndexedAt)
            .ToListAsync();

        var groups = dates
            .GroupBy(d => d.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), count = g.Count() });

        return Ok(groups);
    }

    // Pas de filtre localisation : aucune donnée GPS/EXIF disponible sans télécharger chaque
    // fichier — voir ARCHITECTURE.md (V2, décision utilisateur, hors scope).
    private IOrderedQueryable<MediaIndexEntry> BuildFilteredQuery(
        string? search, string? mediaType, long? minSize, long? maxSize)
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

        return query.OrderByDescending(m => m.ModifiedAt ?? m.CreatedAt ?? m.IndexedAt);
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
