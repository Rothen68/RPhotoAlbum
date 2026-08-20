using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Tests.Fakes;

// Faux fait main (pas de librairie de mock) — voir issue GitHub #17. Couvre ListFolderAsync
// (MediaIndexService) et Upload/DownloadTextFileAsync (AlbumService : album.json et, depuis
// l'issue #6, album-structure.json) ; les autres membres de IPCloudClient lèvent
// NotSupportedException si jamais appelés par erreur.
public class FakePCloudClient : IPCloudClient
{
    private readonly Dictionary<long, PCloudFolderListing> _listings = new();
    private readonly Dictionary<long, Exception> _failures = new();
    private readonly Dictionary<long, string> _textFiles = new();
    private readonly Dictionary<string, byte[]> _thumbnails = new();
    private long _nextFileId = 1;

    // Compte les appels réels vers le faux pCloud par clé de miniature — permet aux tests de
    // MediaThumbnailCacheService de vérifier qu'un hit de cache disque n'appelle PAS pCloud une
    // deuxième fois (voir issue #26).
    public Dictionary<string, int> ThumbnailCallCounts { get; } = new();

    // Bloque l'appel tant que la tâche n'est pas complétée — permet de tester la section
    // critique de MediaIndexService.ReindexAsync (verrou statique) de façon déterministe,
    // sans dépendre d'un Task.Delay arbitraire et donc potentiellement instable en CI.
    public TaskCompletionSource<bool>? Gate { get; set; }

    public void SetFolderListing(long folderId, PCloudFolderListing listing) => _listings[folderId] = listing;

    public void SetFolderFailure(long folderId, Exception exception) => _failures[folderId] = exception;

    public async Task<PCloudFolderListing> ListFolderAsync(long folderId, bool recursive = false, bool nofiles = false)
    {
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        if (_failures.TryGetValue(folderId, out var ex))
        {
            throw ex;
        }

        if (_listings.TryGetValue(folderId, out var listing))
        {
            return listing;
        }

        throw new InvalidOperationException($"Aucun contenu simulé pour le dossier pCloud {folderId}.");
    }

    public string BuildAuthorizeUrl(string state) => throw new NotSupportedException();

    public Task<PCloudTokenResponse> ExchangeCodeAsync(string code, string hostname) => throw new NotSupportedException();

    public Task<string> GetThumbLinkAsync(long fileId, int width, int height, bool crop = false) => throw new NotSupportedException();

    public Task<(long FolderId, string Path)> CreateFolderAsync(long parentFolderId, string name) => throw new NotSupportedException();

    public Task DeleteFolderRecursiveAsync(long folderId) => throw new NotSupportedException();

    public Task<long> CopyFileAsync(long fileId, long toFolderId, string toName) => throw new NotSupportedException();

    public Task DeleteFileAsync(long fileId) => throw new NotSupportedException();

    // Chaque appel produit un nouveau fileId (comme le pCloud réel avec renameifexists=0
    // écrasant en place, mais rien dans AlbumService ne suppose un fileId stable entre deux
    // écritures — PersistAsync/SaveStructureAsync réassignent toujours celui retourné).
    public Task<long> UploadTextFileAsync(long folderId, string filename, string content)
    {
        var fileId = _nextFileId++;
        _textFiles[fileId] = content;
        return Task.FromResult(fileId);
    }

    public Task<string> GetFileLinkAsync(long fileId) => throw new NotSupportedException();

    public Task<string> DownloadTextFileAsync(long fileId) =>
        _textFiles.TryGetValue(fileId, out var content)
            ? Task.FromResult(content)
            : throw new InvalidOperationException($"Fichier texte {fileId} introuvable dans le faux client.");

    public Task<byte[]> DownloadPartialAsync(long fileId, int maxBytes, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<(byte[] Bytes, string? ContentType)> DownloadAsync(long fileId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> GetFileNameAsync(long fileId) => throw new NotSupportedException();

    public void SetThumbnail(long fileId, int width, int height, bool crop, byte[] bytes) =>
        _thumbnails[ThumbnailKey(fileId, width, height, crop)] = bytes;

    public Task<(byte[] Bytes, string? ContentType)> GetThumbnailAsync(
        long fileId, int width, int height, bool crop = false, CancellationToken ct = default)
    {
        var key = ThumbnailKey(fileId, width, height, crop);
        ThumbnailCallCounts[key] = ThumbnailCallCounts.GetValueOrDefault(key) + 1;

        return _thumbnails.TryGetValue(key, out var bytes)
            ? Task.FromResult<(byte[], string?)>((bytes, "image/jpeg"))
            : throw new InvalidOperationException($"Aucune miniature simulée pour {key}.");
    }

    private static string ThumbnailKey(long fileId, int width, int height, bool crop) =>
        $"{fileId}_{width}x{height}_{(crop ? "c" : "n")}";
}
