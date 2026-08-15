using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

// Scanne les dossiers source configurés et met à jour le cache local — voir ARCHITECTURE.md §9.4.
public class MediaIndexService(
    CacheDbContext db,
    PCloudClient client,
    PCloudTokenStore tokenStore,
    ILogger<MediaIndexService> logger)
{
    // Statique : une seule indexation à la fois, tous appelants confondus (job périodique + déclenchement manuel).
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public async Task<int> ReindexAsync(CancellationToken ct = default)
    {
        if (!await Lock.WaitAsync(0, ct))
        {
            return -1;
        }

        try
        {
            if (await tokenStore.GetAsync() is null)
            {
                logger.LogDebug("Indexation ignorée : pCloud non connecté.");
                return 0;
            }

            var sourceFolders = await db.SourceFolders.AsNoTracking().ToListAsync(ct);
            if (sourceFolders.Count == 0)
            {
                return 0;
            }

            var seenFileIds = new HashSet<long>();
            var anyFailures = false;

            foreach (var folder in sourceFolders)
            {
                PCloudFolderListing listing;
                try
                {
                    listing = await client.ListFolderAsync(folder.PCloudFolderId, recursive: true);
                }
                catch (Exception ex)
                {
                    anyFailures = true;
                    logger.LogWarning(ex, "Échec de l'indexation du dossier source {FolderId} ({Label}).",
                        folder.PCloudFolderId, folder.Label);
                    continue;
                }

                foreach (var item in FlattenFiles(listing.Metadata?.Contents))
                {
                    if (item.FileId is not { } fileId)
                    {
                        continue;
                    }

                    var mediaType = ResolveMediaType(item.ContentType);
                    if (mediaType is null)
                    {
                        continue;
                    }

                    seenFileIds.Add(fileId);
                    await UpsertAsync(fileId, item, mediaType, ct);
                }
            }

            // Purge des entrées disparues des dossiers source — sautée si un dossier n'a pas pu être lu,
            // pour ne pas confondre une panne pCloud transitoire avec une suppression réelle.
            if (!anyFailures)
            {
                var stale = await db.MediaIndex.Where(m => !seenFileIds.Contains(m.PCloudFileId)).ToListAsync(ct);
                db.MediaIndex.RemoveRange(stale);
            }

            await db.SaveChangesAsync(ct);
            return seenFileIds.Count;
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task UpsertAsync(long fileId, PCloudItem item, string mediaType, CancellationToken ct)
    {
        var entry = await db.MediaIndex.FirstOrDefaultAsync(m => m.PCloudFileId == fileId, ct);
        if (entry is null)
        {
            db.MediaIndex.Add(new MediaIndexEntry
            {
                PCloudFileId = fileId,
                Name = item.Name,
                Path = item.Path ?? "",
                Hash = item.Hash?.ToString() ?? "",
                MediaType = mediaType,
                CreatedAt = ParseDate(item.Created),
                ModifiedAt = ParseDate(item.Modified),
                Size = item.Size ?? 0,
                IndexedAt = DateTime.UtcNow,
            });
        }
        else
        {
            entry.Name = item.Name;
            entry.Path = item.Path ?? "";
            entry.Hash = item.Hash?.ToString() ?? "";
            entry.ModifiedAt = ParseDate(item.Modified);
            entry.Size = item.Size ?? 0;
            entry.IndexedAt = DateTime.UtcNow;
        }
    }

    private static IEnumerable<PCloudItem> FlattenFiles(IEnumerable<PCloudItem>? items)
    {
        if (items is null)
        {
            yield break;
        }

        foreach (var item in items)
        {
            if (item.IsFolder)
            {
                foreach (var nested in FlattenFiles(item.Contents))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    private static string? ResolveMediaType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return null;
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "image";
        }

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "video";
        }

        return null;
    }

    private static DateTime? ParseDate(string? raw)
        => DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.UtcDateTime
            : null;
}
