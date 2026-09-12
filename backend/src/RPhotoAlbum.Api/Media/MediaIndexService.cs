using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

// NewlyIndexed (new media never seen before, as opposed to media already known and simply
// reconfirmed on this pass) — used as the trigger for automatic EXIF/geo extraction after
// indexing (issue #11): no point relaunching these jobs if indexing found nothing new.
public record MediaIndexResult(int Indexed, int NewlyIndexed, IReadOnlyList<string> FailedFolders)
{
    public bool IsAlreadyRunning { get; private init; }

    public static MediaIndexResult AlreadyRunning { get; } = new(0, 0, []) { IsAlreadyRunning = true };
}

// Scans the configured source folders and updates the local cache — see ARCHITECTURE.md §9.4.
public class MediaIndexService(
    CacheDbContext db,
    IPCloudClient client,
    PCloudTokenStore tokenStore,
    ILogger<MediaIndexService> logger)
{
    // Static: only one indexing run at a time, across all callers (periodic job + manual trigger).
    private static readonly SemaphoreSlim Lock = new(1, 1);

    // autoOnly: reserved for the automatic periodic pass (MediaIndexBackgroundService) — only
    // walks the folders marked SourceFolder.AutoIndex (issue #28), to spare pCloud
    // and the server on large archive folders that no longer change. "Reindex now"
    // (manual trigger, autoOnly=false by default) still checks everything, including
    // non-auto-indexed folders — it's an explicit user action, not a recurring cost.
    public async Task<MediaIndexResult> ReindexAsync(CancellationToken ct = default, bool autoOnly = false)
    {
        if (!await Lock.WaitAsync(0, ct))
        {
            return MediaIndexResult.AlreadyRunning;
        }

        try
        {
            if (await tokenStore.GetAsync() is null)
            {
                logger.LogDebug("Indexation ignorée : pCloud non connecté.");
                return new MediaIndexResult(0, 0, []);
            }

            var allSourceFolders = await db.SourceFolders.AsNoTracking().ToListAsync(ct);
            var sourceFolders = autoOnly ? allSourceFolders.Where(f => f.AutoIndex).ToList() : allSourceFolders;
            if (sourceFolders.Count == 0)
            {
                return new MediaIndexResult(0, 0, []);
            }

            var seenFileIds = new HashSet<long>();
            var failedFolders = new List<string>();
            // Loaded once and kept up to date in memory (not re-queried per file): avoids
            // adding the same PCloudFileId twice (unique index) when source folders
            // overlap (a folder nested inside another, or a file shared between two),
            // since SaveChangesAsync is only called once at the very end of indexing.
            var existingByFileId = await db.MediaIndex.ToDictionaryAsync(m => m.PCloudFileId, ct);

            foreach (var folder in sourceFolders)
            {
                PCloudFolderListing listing;
                try
                {
                    listing = await client.ListFolderAsync(folder.PCloudFolderId, recursive: true);
                }
                catch (Exception ex)
                {
                    failedFolders.Add(folder.Label);
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
                    Upsert(existingByFileId, fileId, item, mediaType);
                }
            }

            // Purge of entries that disappeared from the source folders — skipped if a folder
            // couldn't be read, so as not to confuse a transient pCloud outage with an actual
            // deletion. Filtered in memory over existingByFileId (already loaded in full) rather
            // than a SQL "NOT IN" clause on seenFileIds: with a large source folder, that list
            // could exceed SQLite's parameter limit ("too many SQL variables").
            //
            // IMPORTANT (issue #28): only considers an entry "disappeared" if its path
            // belongs to a folder ACTUALLY revisited during THIS pass (sourceFolders, not
            // allSourceFolders). Without this filtering, an automatic pass limited to
            // active folders (autoOnly=true) would wrongly purge the entire content of the
            // archive folders skipped that cycle, since they would never appear in seenFileIds.
            // A full "Reindex now" (autoOnly=false) revisits all folders, so this
            // filtering changes nothing about its current behavior.
            if (failedFolders.Count == 0)
            {
                var processedPathPrefixes = sourceFolders.Select(f => f.Path.TrimEnd('/') + "/").ToList();
                var stale = existingByFileId.Values.Where(e =>
                    !seenFileIds.Contains(e.PCloudFileId) &&
                    processedPathPrefixes.Any(prefix => e.Path.StartsWith(prefix, StringComparison.Ordinal)));
                db.MediaIndex.RemoveRange(stale);
            }

            // Counted BEFORE SaveChangesAsync: ChangeTracker still distinguishes "Added" entities
            // (new media, never seen) from "Modified" ones (media already known, just reconfirmed).
            var newlyIndexed = db.ChangeTracker.Entries<MediaIndexEntry>().Count(e => e.State == EntityState.Added);

            await db.SaveChangesAsync(ct);
            return new MediaIndexResult(seenFileIds.Count, newlyIndexed, failedFolders);
        }
        finally
        {
            Lock.Release();
        }
    }

    private void Upsert(Dictionary<long, MediaIndexEntry> existingByFileId, long fileId, PCloudItem item, string mediaType)
    {
        if (existingByFileId.TryGetValue(fileId, out var entry))
        {
            entry.Name = item.Name;
            entry.Path = item.Path ?? "";
            entry.Hash = item.Hash?.ToString() ?? "";
            entry.ModifiedAt = ParseDate(item.Modified);
            entry.Size = item.Size ?? 0;
            entry.IndexedAt = DateTime.UtcNow;
        }
        else
        {
            entry = new MediaIndexEntry
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
            };
            db.MediaIndex.Add(entry);
            existingByFileId[fileId] = entry;
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
