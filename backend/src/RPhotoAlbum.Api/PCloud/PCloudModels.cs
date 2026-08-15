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

// created/modified restent des chaînes brutes (format pCloud, ex. "Wed, 12 Jun 2013 12:15:41 +0000") —
// le calcul de la date de tri (ARCHITECTURE.md §7) fera le parsing lors de l'indexation.
// Contents n'est peuplé que lors d'un appel listfolder avec recursive=1 (sous-dossiers imbriqués).
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
