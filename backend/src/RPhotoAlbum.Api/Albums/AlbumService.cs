using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RPhotoAlbum.Api.Data;
using RPhotoAlbum.Api.Models;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Albums;

public record AlbumMembership(string AlbumId, string Name, bool ContainsAll);

// Création, lecture/écriture de album.json, ajout/retrait de médias et blocs texte,
// réorganisation — voir ARCHITECTURE.md §9.5.
public class AlbumService(CacheDbContext db, PCloudClient client, ILogger<AlbumService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<AlbumSummary>> ListAsync(CancellationToken ct = default)
        => await db.AlbumSummaries.AsNoTracking().OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);

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

        foreach (var fileId in fileIds.Distinct())
        {
            if (existing.Contains(fileId))
            {
                continue;
            }

            var media = await db.MediaIndex.AsNoTracking().FirstOrDefaultAsync(m => m.PCloudFileId == fileId, ct);
            if (media is null)
            {
                logger.LogWarning("Média {FileId} introuvable dans le cache, ignoré.", fileId);
                continue;
            }

            var copyFileId = await client.CopyFileAsync(fileId, summary.AlbumFolderId, media.Name);

            doc.Items.Add(new AlbumItemDocument
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
            });
        }

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    public async Task<AlbumDocument> RemoveMediaAsync(string id, List<long> fileIds, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var toRemove = doc.Items
            .Where(i => i.Type == "media" && i.Source is not null && fileIds.Contains(i.Source.FileId))
            .ToList();

        foreach (var item in toRemove)
        {
            await TryDeleteAlbumCopyAsync(item);
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
            await TryDeleteAlbumCopyAsync(item);
        }
        doc.Items.Remove(item);

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    public async Task<AlbumDocument> ReorderAsync(string id, List<string> itemIds, CancellationToken ct = default)
    {
        var (summary, doc) = await LoadAsync(id, ct);
        var byId = doc.Items.ToDictionary(i => i.Id);
        var reordered = itemIds.Where(byId.ContainsKey).Select(iid => byId[iid]).ToList();
        var missing = doc.Items.Where(i => !itemIds.Contains(i.Id));
        doc.Items = reordered.Concat(missing).ToList();

        await PersistAsync(summary, doc, ct);
        return doc;
    }

    // Pour le bottom sheet "Add to Album" : dans quels albums TOUS les médias donnés sont-ils déjà présents ?
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

    private async Task TryDeleteAlbumCopyAsync(AlbumItemDocument item)
    {
        if (item.AlbumCopy is null)
        {
            return;
        }

        try
        {
            await client.DeleteFileAsync(item.AlbumCopy.FileId);
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
