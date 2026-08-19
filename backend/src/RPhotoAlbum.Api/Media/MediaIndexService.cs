using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Media;

// NewlyIndexed (nouveaux médias jamais vus, par opposition aux médias déjà connus et simplement
// reconfirmés à ce passage) — sert de déclencheur à l'extraction EXIF/géo automatique après
// indexation (issue #11) : inutile de relancer ces jobs si l'indexation n'a rien trouvé de neuf.
public record MediaIndexResult(int Indexed, int NewlyIndexed, IReadOnlyList<string> FailedFolders)
{
    public bool IsAlreadyRunning { get; private init; }

    public static MediaIndexResult AlreadyRunning { get; } = new(0, 0, []) { IsAlreadyRunning = true };
}

// Scanne les dossiers source configurés et met à jour le cache local — voir ARCHITECTURE.md §9.4.
public class MediaIndexService(
    CacheDbContext db,
    IPCloudClient client,
    PCloudTokenStore tokenStore,
    ILogger<MediaIndexService> logger)
{
    // Statique : une seule indexation à la fois, tous appelants confondus (job périodique + déclenchement manuel).
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public async Task<MediaIndexResult> ReindexAsync(CancellationToken ct = default)
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

            var sourceFolders = await db.SourceFolders.AsNoTracking().ToListAsync(ct);
            if (sourceFolders.Count == 0)
            {
                return new MediaIndexResult(0, 0, []);
            }

            var seenFileIds = new HashSet<long>();
            var failedFolders = new List<string>();
            // Chargé une fois et tenu à jour en mémoire (pas re-requêté par fichier) : évite
            // d'ajouter deux fois le même PCloudFileId (index unique) quand des dossiers source
            // se chevauchent (un dossier imbriqué dans un autre, ou un fichier partagé entre deux),
            // puisque SaveChangesAsync n'est appelé qu'une fois à la toute fin de l'indexation.
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

            // Purge des entrées disparues des dossiers source — sautée si un dossier n'a pas pu être lu,
            // pour ne pas confondre une panne pCloud transitoire avec une suppression réelle.
            // Filtrage en mémoire sur existingByFileId (déjà chargé intégralement) plutôt qu'une
            // clause SQL "NOT IN" sur seenFileIds : avec un grand dossier source, cette liste peut
            // dépasser la limite de paramètres de SQLite ("too many SQL variables").
            if (failedFolders.Count == 0)
            {
                var stale = existingByFileId.Values.Where(e => !seenFileIds.Contains(e.PCloudFileId));
                db.MediaIndex.RemoveRange(stale);
            }

            // Compté AVANT SaveChangesAsync : ChangeTracker distingue encore les entités "Added"
            // (nouveau média, jamais vu) des "Modified" (média déjà connu, juste reconfirmé).
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
