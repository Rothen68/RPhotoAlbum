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
public class AlbumService(
    CacheDbContext db, PCloudClient client, IServiceScopeFactory scopeFactory, ILogger<AlbumService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Copies pCloud en concurrence bornée (voir MediaExifService.MaxConcurrency, même logique) —
    // issue #13 : une sélection nombreuse copiée en série (un aller-retour pCloud par fichier,
    // sans aucun retour visible côté UI) rendait la création d'un album perceptiblement "gelée".
    private const int AddMediaConcurrency = 4;

    public async Task<List<AlbumSummary>> ListAsync(CancellationToken ct = default)
        => await db.AlbumSummaries.AsNoTracking().OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);

    // Redécouvre les albums déjà présents sur pCloud (album.json dans chaque sous-dossier du
    // dossier parent) et reconstruit AlbumSummaries en conséquence — nécessaire quand le cache
    // local est vide alors que des albums existent déjà sur pCloud (ex. migration vers un nouveau
    // déploiement pointé sur le même dossier), voir ARCHITECTURE.md §3 (cache reconstructible).
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
                // Scope dédié : PCloudClient dépend de PCloudTokenStore, qui interroge
                // CacheDbContext à chaque appel — un DbContext EF Core n'est pas thread-safe, le
                // partager entre ces copies concurrentes reproduirait le même bug que #12.
                using var scope = scopeFactory.CreateScope();
                var scopedClient = scope.ServiceProvider.GetRequiredService<PCloudClient>();
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

        // Suppressions pCloud en concurrence bornée — même correctif que AddMediaAsync (#13),
        // pour la même raison (une sélection nombreuse supprimée en série pouvait rendre la
        // requête perceptiblement "gelée", sans aucun retour visible).
        using var throttle = new SemaphoreSlim(AddMediaConcurrency, AddMediaConcurrency);
        var deleteTasks = toRemove.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var scopedClient = scope.ServiceProvider.GetRequiredService<PCloudClient>();
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

    // rowSpans : nouvelle valeur de RowSpan par id d'item ancre (facultatif) — transportée en
    // plus de l'ordre pour n'avoir qu'un seul appel / une seule réécriture d'album.json, y
    // compris pour un simple "Grouper avec le suivant" qui ne change pas l'ordre (voir action
    // Edit d'album étape 7).
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

    // Validation serveur avant persistance : plafonne RowSpan à [1,3] et le réduit au nombre
    // réel d'items média consécutifs disponibles derrière l'ancre — corrige silencieusement
    // les incohérences plutôt que d'échouer, car album.json est la source de vérité réelle
    // (pas un cache reconstructible) et toute mutation (insertion de texte au milieu d'une
    // rangée, suppression de l'ancre ou d'un item de la rangée, reorder qui casse la
    // contiguïté) peut la rendre incohérente. Appelé à chaque écriture, quel que soit le
    // point d'entrée (AddMedia/RemoveMedia/AddText/RemoveItem/Reorder).
    // internal (pas private) + InternalsVisibleTo(RPhotoAlbum.Api.Tests) : logique pure, sans
    // I/O, testable directement sans avoir à instancier AlbumService (donc sans mocker
    // PCloudClient/CacheDbContext) — voir issue GitHub #17.
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

    // targetClient explicite (pas le `client` injecté au constructeur) : appelé aussi bien
    // depuis un contexte simple (RemoveItemAsync, un seul item) que depuis des suppressions
    // concurrentes (RemoveMediaAsync), où chaque tâche a besoin de son propre PCloudClient
    // scope-isolé — voir commentaire sur AddMediaAsync.
    private async Task TryDeleteAlbumCopyAsync(AlbumItemDocument item, PCloudClient targetClient)
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
