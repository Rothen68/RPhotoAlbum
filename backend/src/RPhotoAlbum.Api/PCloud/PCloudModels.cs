using System.Text.Json.Serialization;

namespace RPhotoAlbum.Api.PCloud;

public record PCloudTokenResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("uid")] long Uid);

public record PCloudFolderListing(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("metadata")] PCloudFolderMetadata? Metadata);

public record PCloudFolderMetadata(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("folderid")] long? FolderId,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("contents")] List<PCloudItem>? Contents);

// created/modified stay as raw strings (pCloud format, e.g. "Wed, 12 Jun 2013 12:15:41 +0000") —
// the sort-date computation (ARCHITECTURE.md §7) will do the parsing during indexing.
// Contents is only populated by a listfolder call with recursive=1 (nested subfolders).
public record PCloudItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("isfolder")] bool IsFolder,
    [property: JsonPropertyName("fileid")] long? FileId,
    [property: JsonPropertyName("folderid")] long? FolderId,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("hash")] ulong? Hash,
    [property: JsonPropertyName("contenttype")] string? ContentType,
    [property: JsonPropertyName("created")] string? Created,
    [property: JsonPropertyName("modified")] string? Modified,
    [property: JsonPropertyName("thumb")] bool? Thumb,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("contents")] List<PCloudItem>? Contents);

public record PCloudThumbLinkResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("hosts")] string[]? Hosts);

public record PCloudFolderOperationResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("metadata")] PCloudFolderMetadata? Metadata);

public record PCloudFileMetadata(
    [property: JsonPropertyName("fileid")] long FileId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long? Size);

public record PCloudFileOperationResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("metadata")] PCloudFileMetadata? Metadata);

public record PCloudUploadResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("fileids")] long[]? FileIds);

// quota/usedquota in bytes — see the pCloud `userinfo` method. Storage quota alert (not
// the local thumbnail cache, already bounded/monitored separately — see MediaController.CacheStatus).
public record PCloudUserInfoResponse(
    [property: JsonPropertyName("result")] int Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("quota")] long Quota,
    [property: JsonPropertyName("usedquota")] long UsedQuota);
