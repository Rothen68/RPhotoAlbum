namespace RPhotoAlbum.Api.PCloud;

// Extrait de PCloudClient pour permettre un faux fait main dans les tests (RPhotoAlbum.Api.Tests
// /Fakes/FakePCloudClient.cs) — voir issue GitHub #17. Signatures identiques à PCloudClient ; pas
// de logique ici, uniquement le contrat.
public interface IPCloudClient
{
    string BuildAuthorizeUrl(string state);
    Task<PCloudTokenResponse> ExchangeCodeAsync(string code, string hostname);
    Task<PCloudFolderListing> ListFolderAsync(long folderId, bool recursive = false, bool nofiles = false);
    Task<string> GetThumbLinkAsync(long fileId, int width, int height, bool crop = false);
    Task<(long FolderId, string Path)> CreateFolderAsync(long parentFolderId, string name);
    Task DeleteFolderRecursiveAsync(long folderId);
    Task<long> CopyFileAsync(long fileId, long toFolderId, string toName);
    Task DeleteFileAsync(long fileId);
    Task<long> UploadTextFileAsync(long folderId, string filename, string content);
    Task<string> GetFileLinkAsync(long fileId);
    Task<string> DownloadTextFileAsync(long fileId);
    Task<byte[]> DownloadPartialAsync(long fileId, int maxBytes, CancellationToken ct = default);
    Task<(byte[] Bytes, string? ContentType)> DownloadAsync(long fileId, CancellationToken ct = default);
    Task<string> GetFileNameAsync(long fileId);
    // Octets de la miniature (pas juste le lien, contrairement à GetThumbLinkAsync) — voir issue
    // #26 (cache disque des miniatures).
    Task<(byte[] Bytes, string? ContentType)> GetThumbnailAsync(
        long fileId, int width, int height, bool crop = false, CancellationToken ct = default);
}
