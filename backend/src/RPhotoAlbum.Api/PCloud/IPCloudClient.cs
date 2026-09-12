namespace RPhotoAlbum.Api.PCloud;

// Extracted from PCloudClient to allow a hand-written fake in tests (RPhotoAlbum.Api.Tests
// /Fakes/FakePCloudClient.cs) — see GitHub issue #17. Signatures identical to PCloudClient; no
// logic here, only the contract.
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
    // Last bytes of the file (suffix Range request) — see issue #21: the "moov" atom
    // of a QuickTime/MP4 container can be at the end of the file rather than the start, depending on the encoder.
    Task<byte[]> DownloadTailAsync(long fileId, int maxBytes, CancellationToken ct = default);
    Task<(byte[] Bytes, string? ContentType)> DownloadAsync(long fileId, CancellationToken ct = default);
    Task<string> GetFileNameAsync(long fileId);
    // Storage quota of the pCloud account (bytes used / total) — for the Configuration
    // page's alert, distinct from the local thumbnail cache already monitored elsewhere.
    Task<(long UsedBytes, long TotalBytes)> GetQuotaAsync();
    // Thumbnail bytes (not just the link, unlike GetThumbLinkAsync) — see issue
    // #26 (thumbnail disk cache).
    Task<(byte[] Bytes, string? ContentType)> GetThumbnailAsync(
        long fileId, int width, int height, bool crop = false, CancellationToken ct = default);
}
