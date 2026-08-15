using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Media;

namespace RPhotoAlbum.Api.Controllers;

[ApiController]
[Route("api/media")]
public class MediaController(MediaIndexService indexService, CacheDbContext db) : ControllerBase
{
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken ct)
    {
        var count = await indexService.ReindexAsync(ct);
        if (count < 0)
        {
            return Conflict(new { error = "Une indexation est déjà en cours." });
        }

        return Ok(new { indexed = count });
    }

    [HttpGet("source")]
    public async Task<IActionResult> Source([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = db.MediaIndex.AsNoTracking()
            .OrderByDescending(m => m.ModifiedAt ?? m.CreatedAt ?? m.IndexedAt);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }
}
