using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Albums;

public record AlbumMembership(string AlbumId, string Name, bool ContainsAll);

// Grouping/ordering of albums (issue #6) — Sections preserves the order of sections as
// persisted, Unsectioned groups the albums not yet organized (including new albums).
public record AlbumSection(string Id, string Name, List<AlbumSummary> Albums);
public record AlbumListResult(List<AlbumSection> Sections, List<AlbumSummary> Unsectioned);
public record AlbumSectionInput(string? Id, string Name, List<string> AlbumIds);

// Creation, reading/writing of album.json, adding/removing media and text blocks,
// reordering — see ARCHITECTURE.md §9.5.
public class AlbumService(
    CacheDbContext db, IPCloudClient client, IServiceScopeFactory scopeFactory, ILogger<AlbumService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Bounded-concurrency pCloud copies (see MediaExifService.MaxConcurrency, same logic) —
    // issue #13: a large selection copied serially (one pCloud round trip per file,
    // with no visible feedback in the UI) made creating an album perceptibly "frozen".
    private const int AddMediaConcurrency = 4;
    // Written at the root of the albums parent folder (not inside an album subfolder like
    // album.json) — see AlbumStructureDocument.
    private const string StructureFileName = "album-structure.json";

    public async Task<List<AlbumSummary>> ListAsync(CancellationToken ct = default)
        => await db.AlbumSummaries.AsNoTracking().OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);

    // List grouped by sections (issue #6) — combines the AlbumSummary (SQLite cache, like
    // ListAsync) with the album-structure.json manifest (root of the albums parent folder).
    // An album never placed in the manifest (new, or never organized) automatically appears
    // in Unsectioned, without requiring any pCloud write.
    public async Task<AlbumListResult> ListGroupedAsync(CancellationToken ct = default)
    {
        var summaries = await ListAsync(ct);
        var structure = await LoadStructureAsync(ct);
        return Project(summaries, structure);
    }

    // Replaces the manifest in its entirety (same philosophy as ReorderAsync, which replaces
    // the full list of an album's items) — drops unknown/deleted album ids and
    // duplicates, reinjects into Unsectioned any album that is known but absent from the payload
    // rather than silently dropping it from the list (same defense as NormalizeRowSpans).
    public async Task<AlbumListResult> SaveStructureAsync(
        List<AlbumSectionInput> sections, List<string> unsectionedAlbumIds, CancellationToken ct = default)
    {
        var config = await db.AppConfigurations.FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (config?.AlbumParentFolderId is not { } parentFolderId)
        {
            throw new InvalidOperationException("Dossier des albums non configuré (voir la page Configuration).");
        }

        var summaries = await ListAsync(ct);
        var knownIds = summaries.Select(a => a.Id).ToHashSet();
        var seen = new HashSet<string>();
        var doc = new AlbumStructureDocument();

        foreach (var section in sections)
        {
            var albumIds = new List<string>();
            foreach (var albumId in section.AlbumIds)
            {
                if (knownIds.Contains(albumId) && seen.Add(albumId))
                {
                    albumIds.Add(albumId);
                }
            }

            var sectionId = string.IsNullOrWhiteSpace(section.Id) ? $"sec_{Guid.NewGuid().ToString("N")[..8]}" : section.Id;
            doc.Sections.Add(new AlbumSectionDocument { Id = sectionId, Name = section.Name.Trim(), AlbumIds = albumIds });
        }

        foreach (var albumId in unsectionedAlbumIds)
        {
            if (knownIds.Contains(albumId) && seen.Add(albumId))
            {
                doc.UnsectionedAlbumIds.Add(albumId);
            }
        }

        foreach (var albumId in knownIds)
        {
            if (seen.Add(albumId))
            {
                doc.UnsectionedAlbumIds.Add(albumId);
            }
        }

        var fileId = await client.UploadTextFileAsync(parentFolderId, StructureFileName, JsonSerializer.Serialize(doc, JsonOptions));
        config.AlbumStructureFileId = fileId;
        await db.SaveChangesAsync(ct);

        return Project(summaries, doc);
    }

    private async Task<AlbumStructureDocument> LoadStructureAsync(CancellationToken ct)
    {
        var config = await db.AppConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (config?.AlbumStructureFileId is not { } fileId)
        {
            return new AlbumStructureDocument();
        }

        var json = await client.DownloadTextFileAsync(fileId);
        return JsonSerializer.Deserialize<AlbumStructureDocument>(json, JsonOptions) ?? new AlbumStructureDocument();
    }

    private static AlbumListResult Project(List<AlbumSummary> summaries, AlbumStructureDocument structure)
    {
        var byId = summaries.ToDictionary(a => a.Id);
        var placed = new HashSet<string>();
        var sections = new List<AlbumSection>();

        foreach (var sectionDoc in structure.Sections)
        {
            var albums = new List<AlbumSummary>();
            foreach (var albumId in sectionDoc.AlbumIds)
            {
                if (byId.TryGetValue(albumId, out var summary) && placed.Add(albumId))
                {
                    albums.Add(summary);
                }
            }

            sections.Add(new AlbumSection(sectionDoc.Id, sectionDoc.Name, albums));
        }

        var unsectioned = new List<AlbumSummary>();
        foreach (var albumId in structure.UnsectionedAlbumIds)
        {
            if (byId.TryGetValue(albumId, out var summary) && placed.Add(albumId))
            {
                unsectioned.Add(summary);
            }
        }

        // Albums never placed anywhere in the manifest (new, or never organized) —
        // appended to the end of the "unsectioned" list (usual order, most recent first).
        foreach (var summary in summaries)
        {
            if (placed.Add(summary.Id))
            {
                unsectioned.Add(summary);
            }
        }

        return new AlbumListResult(sections, unsectioned);
    }

    // Rediscovers the albums already present on pCloud (album.json in each subfolder of the
    // parent folder) and rebuilds AlbumSummaries accordingly — needed when the local
    // cache is empty while albums already exist on pCloud (e.g. migrating to a new
    // deployment pointed at the same folder), see ARCHITECTURE.md §3 (rebuildable cache).
    public async Task<int> ReindexAsync(CancellationToken ct = default)
    {
        var config = await db.AppConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (config?.AlbumParentFolderId is not { } parentFolderId)
        {
            throw new InvalidOperationException("Dossier des albums non configuré (voir la page Configuration).");
        }

        var listing = await client.ListFolderAsync(parentFolderId, recursive: true);
        var discovered = new List<AlbumSummary>();

        foreach (var folder in listing.Metadata?.Contents ?? [])
        {
            if (!folder.IsFolder || folder.FolderId is not { } folderId)
            {
                continue;
            }

            var jsonEntry = folder.Contents?.FirstOrDefault(c => !c.IsFolder && c.Name == "album.json");
            if (jsonEntry?.FileId is not { } jsonFileId)
            {
                continue;
            }

            AlbumDocument? doc;
            try
            {
                var json = await client.DownloadTextFileAsync(jsonFileId);
                doc = JsonSerializer.Deserialize<AlbumDocument>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Échec de lecture de album.json dans le dossier {FolderId}, ignoré.", folderId);
                continue;
            }

            if (doc is null)
            {
                continue;
            }

            discovered.Add(new AlbumSummary
            {
                Id = doc.Id,
                Slug = doc.Slug,
                Name = doc.Name,
                AlbumFolderId = folderId,
                AlbumFolderPath = folder.Path ?? doc.AlbumFolder.Path,
                AlbumJsonFileId = jsonFileId,
                ItemCount = doc.Items.Count,
                CoverFileId = doc.Items.FirstOrDefault(i => i.Type == "media")?.AlbumCopy?.FileId,
                UpdatedAt = doc.UpdatedAt,
            });
        }

        var existing = await db.AlbumSummaries.ToListAsync(ct);
        var discoveredIds = discovered.Select(d => d.Id).ToHashSet();

        db.AlbumSummaries.RemoveRange(existing.Where(e => !discoveredIds.Contains(e.Id)));

        foreach (var found in discovered)
        {
            var current = existing.FirstOrDefault(e => e.Id == found.Id);
            if (current is null)
            {
                db.AlbumSummaries.Add(found);
            }
            else
            {
                current.Slug = found.Slug;
                current.Name = found.Name;
                current.AlbumFolderId = found.AlbumFolderId;
                current.AlbumFolderPath = found.AlbumFolderPath;
                current.AlbumJsonFileId = found.AlbumJsonFileId;
                current.ItemCount = found.ItemCount;
                current.CoverFileId = found.CoverFileId;
                current.UpdatedAt = found.UpdatedAt;
            }
        }

        await db.SaveChangesAsync(ct);
        return discovered.Count;
    }

    public async Task<AlbumDocument> CreateAsync(string name, List<long>? initialMediaFileIds, CancellationToken ct = default)
    {
        var config = await db.AppConfigurations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (config?.AlbumParentFolderId is not { } parentFolderId)
        {
            throw new InvalidOperationException("Dossier des albums non configuré (voir la page Configuration).");
        }

        var id = $"alb_{DateTime.UtcNow:yyyyMMdd}_{Guid.NewGuid().ToString("N")[..6]}";
        var slug = Slugify(name);
        var (folderId, path) = await client.CreateFolderAsync(parentFolderId, $"{id}_{slug}");

        var doc = new AlbumDocument
        {
            Id = id,
            Slug = slug,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            AlbumFolder = new AlbumFolderRef { FolderId = folderId, Path = path },
            Items = [],
        };

        var jsonFileId = await client.UploadTextFileAsync(folderId, "album.json", JsonSerializer.Serialize(doc, JsonOptions));

        var summary = new AlbumSummary
        {
            Id = id,
            Slug = slug,
            Name = name,
            AlbumFolderId = folderId,
            AlbumFolderPath = path,
            AlbumJsonFileId = jsonFileId,
            ItemCount = 0,
            UpdatedAt = doc.UpdatedAt,
        };
        db.AlbumSummaries.Add(summary);
        await db.SaveChangesAsync(ct);

        if (initialMediaFileIds is { Count: > 0 })
        {
            doc = await AddMediaAsync(id, initialMediaFileIds, ct);
        }

        return doc;
    }

    public async Task<AlbumDocument> GetAsync(string id, CancellationToken ct = default)
    {
        var (_, doc) = await LoadAsync(id, ct);
        return doc;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var summary = await db.AlbumSummaries.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Album {id} introuvable.");

        await client.DeleteFolderRecursiveAsync(summary.AlbumFolderId);
        db.AlbumSummaries.Remove(summary);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AlbumDocument> AddMediaAsync(string id, List<long> fileIds, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var existing = doc.Items.Where(i => i.Type == "media" && i.Source is not null)
            .Select(i => i.Source!.FileId).ToHashSet();

        var toAdd = fileIds.Distinct().Where(fileId => !existing.Contains(fileId)).ToList();
        if (toAdd.Count == 0)
        {
            return doc;
        }

        var mediaByFileId = await db.MediaIndex.AsNoTracking()
            .Where(m => toAdd.Contains(m.PCloudFileId))
            .ToDictionaryAsync(m => m.PCloudFileId, ct);

        using var throttle = new SemaphoreSlim(AddMediaConcurrency, AddMediaConcurrency);
        var copyTasks = toAdd.Select(async fileId =>
        {
            if (!mediaByFileId.TryGetValue(fileId, out var media))
            {
                logger.LogWarning("Média {FileId} introuvable dans le cache, ignoré.", fileId);
                return null;
            }

            await throttle.WaitAsync(ct);
            try
            {
                // Dedicated scope: PCloudClient depends on PCloudTokenStore, which queries
                // CacheDbContext on every call — an EF Core DbContext is not thread-safe,
                // sharing it across these concurrent copies would reproduce the same bug as #12.
                using var scope = scopeFactory.CreateScope();
                var scopedClient = scope.ServiceProvider.GetRequiredService<IPCloudClient>();
                var copyFileId = await scopedClient.CopyFileAsync(fileId, summary.AlbumFolderId, media.Name);

                return new AlbumItemDocument
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Type = "media",
                    MediaType = media.MediaType,
                    Date = media.ModifiedAt ?? media.CreatedAt ?? media.IndexedAt,
                    Source = new AlbumMediaRef { FileId = media.PCloudFileId, Path = media.Path, Hash = media.Hash, Name = media.Name },
                    AlbumCopy = new AlbumMediaRef
                    {
                        FileId = copyFileId,
                        Path = $"{summary.AlbumFolderPath}/{media.Name}",
                        Name = media.Name,
                    },
                    Width = media.Width,
                    Height = media.Height,
                    DateTaken = media.DateTaken,
                    Country = media.Country,
                    Region = media.Region,
                    City = media.City,
                };
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(copyTasks);
        doc.Items.AddRange(results.Where(item => item is not null)!);

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    public async Task<AlbumDocument> RemoveMediaAsync(string id, List<long> fileIds, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var toRemove = doc.Items
            .Where(i => i.Type == "media" && i.Source is not null && fileIds.Contains(i.Source.FileId))
            .ToList();

        if (toRemove.Count == 0)
        {
            return doc;
        }

        // Bounded-concurrency pCloud deletions — same fix as AddMediaAsync (#13),
        // for the same reason (a large selection deleted serially could make the
        // request perceptibly "frozen", with no visible feedback).
        using var throttle = new SemaphoreSlim(AddMediaConcurrency, AddMediaConcurrency);
        var deleteTasks = toRemove.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var scopedClient = scope.ServiceProvider.GetRequiredService<IPCloudClient>();
                await TryDeleteAlbumCopyAsync(item, scopedClient);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(deleteTasks);
        foreach (var item in toRemove)
        {
            doc.Items.Remove(item);
        }

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    public async Task<AlbumDocument> AddTextAsync(string id, string? afterItemId, string markdown, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var item = new AlbumItemDocument
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Type = "text",
            Markdown = markdown,
            Date = DateTime.UtcNow,
        };

        if (afterItemId is null)
        {
            doc.Items.Insert(0, item);
        }
        else
        {
            var index = doc.Items.FindIndex(i => i.Id == afterItemId);
            doc.Items.Insert(index < 0 ? doc.Items.Count : index + 1, item);
        }

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    public async Task<AlbumDocument> UpdateTextAsync(string id, string itemId, string markdown, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var item = doc.Items.FirstOrDefault(i => i.Id == itemId && i.Type == "text")
            ?? throw new KeyNotFoundException("Bloc texte introuvable.");

        item.Markdown = markdown;

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    public async Task<AlbumDocument> RemoveItemAsync(string id, string itemId, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var item = doc.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw new KeyNotFoundException("Bloc introuvable.");

        if (item.Type == "media")
        {
            await TryDeleteAlbumCopyAsync(item, client);
        }
        doc.Items.Remove(item);

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    // rowSpans: new RowSpan value per anchor item id (optional) — carried alongside
    // the order so there is only a single call / single album.json rewrite, including
    // for a simple "Group with next" that doesn't change the order (see Album Edit
    // action step 7).
    public async Task<AlbumDocument> ReorderAsync(
        string id, List<string> itemIds, Dictionary<string, int>? rowSpans = null, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var byId = doc.Items.ToDictionary(i => i.Id);
        var reordered = itemIds.Where(byId.ContainsKey).Select(iid => byId[iid]).ToList();
        var missing = doc.Items.Where(i => !itemIds.Contains(i.Id));
        doc.Items = reordered.Concat(missing).ToList();

        if (rowSpans is not null)
        {
            foreach (var (itemId, span) in rowSpans)
            {
                if (byId.TryGetValue(itemId, out var item))
                {
                    item.RowSpan = span;
                }
            }
        }

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    // For the "Add to Album" bottom sheet: in which albums are ALL the given media already present?
    public async Task<List<AlbumMembership>> GetMembershipAsync(List<long> fileIds, CancellationToken ct = default)
    {
        var summaries = await db.AlbumSummaries.AsNoTracking().ToListAsync(ct);
        var results = new List<AlbumMembership>();

        foreach (var summary in summaries)
        {
            try
            {
                var json = await client.DownloadTextFileAsync(summary.AlbumJsonFileId);
                var doc = JsonSerializer.Deserialize<AlbumDocument>(json, JsonOptions);
                var present = (doc?.Items ?? [])
                    .Where(i => i.Type == "media" && i.Source is not null)
                    .Select(i => i.Source!.FileId)
                    .ToHashSet();
                var containsAll = fileIds.Count > 0 && fileIds.All(present.Contains);
                results.Add(new AlbumMembership(summary.Id, summary.Name, containsAll));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Échec de lecture de l'album {AlbumId} pour le calcul d'appartenance.", summary.Id);
                results.Add(new AlbumMembership(summary.Id, summary.Name, false));
            }
        }

        return results;
    }

    private async Task<(AlbumSummary Summary, AlbumDocument Doc)> LoadAsync(string id, CancellationToken ct)
    {
        var summary = await db.AlbumSummaries.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Album {id} introuvable.");

        var json = await client.DownloadTextFileAsync(summary.AlbumJsonFileId);
        var doc = JsonSerializer.Deserialize<AlbumDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("album.json invalide.");

        return (summary, doc);
    }

    private async Task PersistAsync(AlbumSummary summary, AlbumDocument doc, CancellationToken ct)
    {
        NormalizeRowSpans(doc);
        doc.UpdatedAt = DateTime.UtcNow;
        var jsonFileId = await client.UploadTextFileAsync(
            summary.AlbumFolderId, "album.json", JsonSerializer.Serialize(doc, JsonOptions));

        summary.AlbumJsonFileId = jsonFileId;
        summary.Name = doc.Name;
        summary.ItemCount = doc.Items.Count;
        summary.CoverFileId = doc.Items.FirstOrDefault(i => i.Type == "media")?.AlbumCopy?.FileId;
        summary.UpdatedAt = doc.UpdatedAt;

        await db.SaveChangesAsync(ct);
    }

    // Server-side validation before persisting: clamps RowSpan to [1,3] and reduces it to the
    // actual number of consecutive media items available behind the anchor — silently corrects
    // inconsistencies rather than failing, because album.json is the real source of truth
    // (not a rebuildable cache) and any mutation (inserting text in the middle of a
    // row, removing the anchor or an item from the row, a reorder that breaks
    // contiguity) can make it inconsistent. Called on every write, regardless of the
    // entry point (AddMedia/RemoveMedia/AddText/RemoveItem/Reorder).
    // internal (not private) + InternalsVisibleTo(RPhotoAlbum.Api.Tests): pure logic, no
    // I/O, directly testable without having to instantiate AlbumService (so without mocking
    // PCloudClient/CacheDbContext) — see GitHub issue #17.
    internal static void NormalizeRowSpans(AlbumDocument doc)
    {
        var items = doc.Items;
        var i = 0;
        while (i < items.Count)
        {
            var anchor = items[i];
            if (anchor.Type != "media")
            {
                anchor.RowSpan = 1;
                i++;
                continue;
            }

            var available = 1;
            while (i + available < items.Count && items[i + available].Type == "media" && available < 3)
            {
                available++;
            }

            var span = Math.Clamp(anchor.RowSpan, 1, available);
            anchor.RowSpan = span;

            for (var k = 1; k < span; k++)
            {
                items[i + k].RowSpan = 1;
            }

            i += span;
        }
    }

    // Explicit targetClient (not the `client` injected in the constructor): called both
    // from a simple context (RemoveItemAsync, a single item) and from concurrent
    // deletions (RemoveMediaAsync), where each task needs its own scope-isolated
    // PCloudClient — see the comment on AddMediaAsync.
    private async Task TryDeleteAlbumCopyAsync(AlbumItemDocument item, IPCloudClient targetClient)
    {
        if (item.AlbumCopy is null)
        {
            return;
        }

        try
        {
            await targetClient.DeleteFileAsync(item.AlbumCopy.FileId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la suppression de la copie album pour le bloc {ItemId}.", item.Id);
        }
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "album" : slug;
    }
}
