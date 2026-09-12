using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Tests.Fakes;

// Hand-written fake (no mocking library) — see GitHub issue #17. Covers ListFolderAsync
// (MediaIndexService) and Upload/DownloadTextFileAsync (AlbumService: album.json and, since
// issue #6, album-structure.json); the other members of IPCloudClient throw
// NotSupportedException if ever called by mistake.
public class FakePCloudClient : IPCloudClient
{
    private readonly Dictionary<long, PCloudFolderListing> _listings = new();
    private readonly Dictionary<long, Exception> _failures = new();
    private readonly Dictionary<long, string> _textFiles = new();
    private readonly Dictionary<string, byte[]> _thumbnails = new();
    private long _nextFileId = 1;

    // Counts the actual calls to the fake pCloud per thumbnail key — lets
    // MediaThumbnailCacheService tests verify that a disk cache hit does NOT call pCloud a
    // second time (see issue #26).
    public Dictionary<string, int> ThumbnailCallCounts { get; } = new();

    // Blocks the call until the task is completed — lets the critical section of
    // MediaIndexService.ReindexAsync (static lock) be tested deterministically,
    // without relying on an arbitrary Task.Delay that could be flaky in CI.
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

    // Each call produces a new fileId (like real pCloud with renameifexists=0
    // overwriting in place, but nothing in AlbumService assumes a stable fileId between two
    // writes — PersistAsync/SaveStructureAsync always reassign the one returned).
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

    public Task<byte[]> DownloadTailAsync(long fileId, int maxBytes, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<(byte[] Bytes, string? ContentType)> DownloadAsync(long fileId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> GetFileNameAsync(long fileId) => throw new NotSupportedException();

    public Task<(long UsedBytes, long TotalBytes)> GetQuotaAsync() => throw new NotSupportedException();

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
