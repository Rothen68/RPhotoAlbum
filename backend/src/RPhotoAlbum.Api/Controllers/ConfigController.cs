using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Controllers;

public record AlbumFolderDto(long FolderId, string Path);
public record SourceFolderDto(long FolderId, string Label, string Path);
public record AppConfigurationDto(AlbumFolderDto? AlbumParentFolder, List<SourceFolderDto> SourceFolders);

// Enregistrement des dossiers source et du dossier parent des albums — voir ARCHITECTURE.md §9.2, §11.1.
[ApiController]
[Route("api/config")]
public class ConfigController(CacheDbContext db, IPCloudClient client, ILogger<ConfigController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AppConfigurationDto>> Get() => Ok(await LoadAsync());

    [HttpPut]
    public async Task<ActionResult<AppConfigurationDto>> Put(AppConfigurationDto dto)
    {
        if (dto.AlbumParentFolder is { } album && !await FolderIsAccessibleAsync(album.FolderId))
        {
            return BadRequest(new { error = $"Dossier des albums inaccessible (id {album.FolderId})." });
        }

        foreach (var folder in dto.SourceFolders)
        {
            if (!await FolderIsAccessibleAsync(folder.FolderId))
            {
                return BadRequest(new { error = $"Dossier source inaccessible : {folder.Label} (id {folder.FolderId})." });
            }
        }

        var config = await db.AppConfigurations.FirstOrDefaultAsync(c => c.Id == 1);
        if (config is null)
        {
            config = new AppConfiguration { Id = 1 };
            db.AppConfigurations.Add(config);
        }

        config.AlbumParentFolderId = dto.AlbumParentFolder?.FolderId;
        config.AlbumParentFolderPath = dto.AlbumParentFolder?.Path;

        var existingSourceFolders = await db.SourceFolders.ToListAsync();
        db.SourceFolders.RemoveRange(existingSourceFolders);
        foreach (var folder in dto.SourceFolders)
        {
            db.SourceFolders.Add(new SourceFolder
            {
                PCloudFolderId = folder.FolderId,
                Label = folder.Label,
                Path = folder.Path,
            });
        }

        await db.SaveChangesAsync();

        return Ok(await LoadAsync());
    }

    private async Task<AppConfigurationDto> LoadAsync()
    {
        var config = await db.AppConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1);
        var sourceFolders = await db.SourceFolders.AsNoTracking().ToListAsync();

        var albumFolder = config?.AlbumParentFolderId is { } id
            ? new AlbumFolderDto(id, config.AlbumParentFolderPath ?? "")
            : null;

        return new AppConfigurationDto(
            albumFolder,
            sourceFolders.Select(f => new SourceFolderDto(f.PCloudFolderId, f.Label, f.Path)).ToList());
    }

    private async Task<bool> FolderIsAccessibleAsync(long folderId)
    {
        try
        {
            await client.ListFolderAsync(folderId, nofiles: true);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dossier pCloud {FolderId} inaccessible.", folderId);
            return false;
        }
    }
}
