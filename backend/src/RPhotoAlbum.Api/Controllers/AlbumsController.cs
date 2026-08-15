using Microsoft.AspNetCore.Mvc;
using RPhotoAlbum.Api.Albums;

namespace RPhotoAlbum.Api.Controllers;

public record CreateAlbumRequest(string Name, List<long>? InitialMediaFileIds);
public record MediaFileIdsRequest(List<long> FileIds);
public record AddTextRequest(string? AfterItemId, string Markdown);
public record UpdateTextRequest(string Markdown);
public record ReorderRequest(List<string> ItemIds);

public record AlbumSummaryDto(string Id, string Name, int ItemCount, long? CoverFileId, DateTime UpdatedAt);
public record AlbumMediaRefDto(long FileId, string Name);
public record AlbumItemDto(string Id, string Type, string? MediaType, DateTime? Date, AlbumMediaRefDto? Source, AlbumMediaRefDto? AlbumCopy, string? Markdown);
public record AlbumDetailDto(string Id, string Name, DateTime UpdatedAt, List<AlbumItemDto> Items);
public record AlbumMembershipDto(string AlbumId, string Name, bool ContainsAll);

[ApiController]
[Route("api/albums")]
public class AlbumsController(AlbumService albums) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var summaries = await albums.ListAsync(ct);
        return Ok(summaries.Select(ToDto));
    }

    // Redécouvre les albums déjà présents sur pCloud — voir AlbumService.ReindexAsync.
    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken ct)
    {
        try
        {
            var found = await albums.ReindexAsync(ct);
            return Ok(new { found });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateAlbumRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Le nom de l'album est requis." });
        }

        try
        {
            var doc = await albums.CreateAsync(request.Name.Trim(), request.InitialMediaFileIds, ct);
            return Ok(ToDto(doc));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        try
        {
            var doc = await albums.GetAsync(id, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        try
        {
            await albums.DeleteAsync(id, ct);
            return Ok();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("membership")]
    public async Task<IActionResult> Membership(MediaFileIdsRequest request, CancellationToken ct)
    {
        var result = await albums.GetMembershipAsync(request.FileIds, ct);
        return Ok(result.Select(m => new AlbumMembershipDto(m.AlbumId, m.Name, m.ContainsAll)));
    }

    [HttpPost("{id}/media/add")]
    public async Task<IActionResult> AddMedia(string id, MediaFileIdsRequest request, CancellationToken ct)
    {
        try
        {
            var doc = await albums.AddMediaAsync(id, request.FileIds, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/media/remove")]
    public async Task<IActionResult> RemoveMedia(string id, MediaFileIdsRequest request, CancellationToken ct)
    {
        try
        {
            var doc = await albums.RemoveMediaAsync(id, request.FileIds, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/text")]
    public async Task<IActionResult> AddText(string id, AddTextRequest request, CancellationToken ct)
    {
        try
        {
            var doc = await albums.AddTextAsync(id, request.AfterItemId, request.Markdown, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<IActionResult> UpdateText(string id, string itemId, UpdateTextRequest request, CancellationToken ct)
    {
        try
        {
            var doc = await albums.UpdateTextAsync(id, itemId, request.Markdown, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<IActionResult> RemoveItem(string id, string itemId, CancellationToken ct)
    {
        try
        {
            var doc = await albums.RemoveItemAsync(id, itemId, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/order")]
    public async Task<IActionResult> Reorder(string id, ReorderRequest request, CancellationToken ct)
    {
        try
        {
            var doc = await albums.ReorderAsync(id, request.ItemIds, ct);
            return Ok(ToDto(doc));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private static AlbumSummaryDto ToDto(Models.AlbumSummary summary)
        => new(summary.Id, summary.Name, summary.ItemCount, summary.CoverFileId, summary.UpdatedAt);

    private static AlbumDetailDto ToDto(AlbumDocument doc)
        => new(doc.Id, doc.Name, doc.UpdatedAt, doc.Items.Select(ToDto).ToList());

    private static AlbumItemDto ToDto(AlbumItemDocument item)
        => new(
            item.Id,
            item.Type,
            item.MediaType,
            item.Date,
            item.Source is { } s ? new AlbumMediaRefDto(s.FileId, s.Name) : null,
            item.AlbumCopy is { } c ? new AlbumMediaRefDto(c.FileId, c.Name) : null,
            item.Markdown);
}
