using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace RPhotoAlbum.Api.PCloud;

// Wraps the pCloud API calls — see ARCHITECTURE.md §9.3.
public class PCloudClient(HttpClient httpClient, IOptions<PCloudOptions> options, PCloudTokenStore tokenStore, IMemoryCache cache) : IPCloudClient
{
    // Caching duration for resolved links (thumbnail/file), deliberately much
    // shorter than the actual validity duration of pCloud links (not precisely documented,
    // but far longer than this in practice) — reduces repeated calls to getthumblink/
    // getfilelink (expensive, and pCloud can be slow to generate a thumbnail never requested
    // before) without risking serving an expired link. See user feedback from 08/16: a load
    // time of several minutes on a batch of ~45k photos never thumbnailed by pCloud before.
    private static readonly TimeSpan LinkCacheDuration = TimeSpan.FromMinutes(20);
    private const string AuthorizeUrl = "https://my.pcloud.com/oauth2/authorize";

    public string BuildAuthorizeUrl(string state)
    {
        var opts = options.Value;
        return QueryHelpers.AddQueryString(AuthorizeUrl, new Dictionary<string, string?>
        {
            ["client_id"] = opts.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = opts.RedirectUri,
            ["state"] = state,
        });
    }

    public async Task<PCloudTokenResponse> ExchangeCodeAsync(string code, string hostname)
    {
        var opts = options.Value;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
            ["code"] = code,
        };

        using var response = await httpClient.PostAsync(
            $"https://{hostname}/oauth2_token", new FormUrlEncodedContent(form));
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<PCloudTokenResponse>()
            ?? throw new InvalidOperationException("Réponse pCloud invalide (oauth2_token).");

        if (payload.Result != 0)
        {
            throw new InvalidOperationException($"Échec de l'échange du code pCloud (result={payload.Result}: {payload.Error}).");
        }

        return payload;
    }

    public async Task<PCloudFolderListing> ListFolderAsync(long folderId, bool recursive = false, bool nofiles = false)
    {
        var connection = await RequireConnectionAsync();
        var query = new Dictionary<string, string?>
        {
            ["folderid"] = folderId.ToString(),
            ["access_token"] = connection.AccessToken,
        };
        if (recursive) query["recursive"] = "1";
        if (nofiles) query["nofiles"] = "1";

        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/listfolder", query);

        var listing = await httpClient.GetFromJsonAsync<PCloudFolderListing>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (listfolder).");

        if (listing.Result != 0)
        {
            throw new InvalidOperationException($"Erreur pCloud listfolder (result={listing.Result}: {listing.Error}).");
        }

        return listing;
    }

    public async Task<string> GetThumbLinkAsync(long fileId, int width, int height, bool crop = false)
    {
        var cacheKey = $"thumb:{fileId}:{width}x{height}:{crop}";
        if (cache.TryGetValue(cacheKey, out string? cached))
        {
            return cached!;
        }

        var resolved = await ResolveThumbLinkAsync(fileId, width, height, crop);
        cache.Set(cacheKey, resolved, LinkCacheDuration);
        return resolved;
    }

    private async Task<string> ResolveThumbLinkAsync(long fileId, int width, int height, bool crop)
    {
        var connection = await RequireConnectionAsync();
        var query = new Dictionary<string, string?>
        {
            ["fileid"] = fileId.ToString(),
            ["size"] = $"{width}x{height}",
            ["access_token"] = connection.AccessToken,
        };
        if (crop) query["crop"] = "1";

        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/getthumblink", query);

        var thumb = await httpClient.GetFromJsonAsync<PCloudThumbLinkResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (getthumblink).");

        if (thumb.Result != 0 || thumb.Hosts is not { Length: > 0 } || string.IsNullOrEmpty(thumb.Path))
        {
            throw new InvalidOperationException($"Erreur pCloud getthumblink (result={thumb.Result}: {thumb.Error}).");
        }

        return $"https://{thumb.Hosts[0]}{thumb.Path}";
    }

    public async Task<(long FolderId, string Path)> CreateFolderAsync(long parentFolderId, string name)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/createfolder", new Dictionary<string, string?>
        {
            ["folderid"] = parentFolderId.ToString(),
            ["name"] = name,
            ["access_token"] = connection.AccessToken,
        });

        var response = await httpClient.GetFromJsonAsync<PCloudFolderOperationResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (createfolder).");

        if (response.Result != 0 || response.Metadata?.FolderId is not { } folderId)
        {
            throw new InvalidOperationException($"Erreur pCloud createfolder (result={response.Result}: {response.Error}).");
        }

        return (folderId, response.Metadata.Path ?? "");
    }

    public async Task DeleteFolderRecursiveAsync(long folderId)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/deletefolderrecursive", new Dictionary<string, string?>
        {
            ["folderid"] = folderId.ToString(),
            ["access_token"] = connection.AccessToken,
        });

        var response = await httpClient.GetFromJsonAsync<PCloudFileOperationResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (deletefolderrecursive).");

        if (response.Result != 0)
        {
            throw new InvalidOperationException($"Erreur pCloud deletefolderrecursive (result={response.Result}: {response.Error}).");
        }
    }

    public async Task<long> CopyFileAsync(long fileId, long toFolderId, string toName)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/copyfile", new Dictionary<string, string?>
        {
            ["fileid"] = fileId.ToString(),
            ["tofolderid"] = toFolderId.ToString(),
            ["toname"] = toName,
            ["access_token"] = connection.AccessToken,
        });

        var response = await httpClient.GetFromJsonAsync<PCloudFileOperationResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (copyfile).");

        if (response.Result != 0 || response.Metadata is null)
        {
            throw new InvalidOperationException($"Erreur pCloud copyfile (result={response.Result}: {response.Error}).");
        }

        return response.Metadata.FileId;
    }

    public async Task DeleteFileAsync(long fileId)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/deletefile", new Dictionary<string, string?>
        {
            ["fileid"] = fileId.ToString(),
            ["access_token"] = connection.AccessToken,
        });

        var response = await httpClient.GetFromJsonAsync<PCloudFileOperationResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (deletefile).");

        if (response.Result != 0)
        {
            throw new InvalidOperationException($"Erreur pCloud deletefile (result={response.Result}: {response.Error}).");
        }
    }

    public async Task<long> UploadTextFileAsync(long folderId, string filename, string content)
    {
        var connection = await RequireConnectionAsync();

        // access_token stays in the query string (like every other call — pCloud auth
        // is only recognized there). Multipart body built by hand, as raw bytes:
        // MultipartFormDataContent produces a body that pCloud's parser accepts
        // (result=0) without ever processing the file (metadata/fileids empty) — exact cause
        // not identified, this manual construction strictly follows RFC 7578 and avoids
        // any variation generated by .NET (boundary quoting, implicit headers…).
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/uploadfile", new Dictionary<string, string?>
        {
            ["access_token"] = connection.AccessToken,
        });

        var boundary = "----RPhotoAlbum" + Guid.NewGuid().ToString("N");
        var body = new StringBuilder();
        AppendField(body, boundary, "folderid", folderId.ToString());
        AppendField(body, boundary, "renameifexists", "0");
        body.Append($"--{boundary}\r\n");
        body.Append($"Content-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n");
        body.Append("Content-Type: application/octet-stream\r\n\r\n");
        body.Append(content);
        body.Append("\r\n");
        body.Append($"--{boundary}--\r\n");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToString())),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));

        using var httpResponse = await httpClient.SendAsync(request);
        httpResponse.EnsureSuccessStatusCode();

        var response = await httpResponse.Content.ReadFromJsonAsync<PCloudUploadResponse>()
            ?? throw new InvalidOperationException("Réponse pCloud invalide (uploadfile).");

        if (response.Result != 0 || response.FileIds is not { Length: > 0 })
        {
            throw new InvalidOperationException($"Erreur pCloud uploadfile (result={response.Result}: {response.Error}).");
        }

        return response.FileIds[0];
    }

    // Direct link to the original file (not a thumbnail) — used for video
    // playback. Cached: a media file, once uploaded, is immutable, so a link
    // that's a few minutes stale is not a problem.
    public async Task<string> GetFileLinkAsync(long fileId)
    {
        var cacheKey = $"file:{fileId}";
        if (cache.TryGetValue(cacheKey, out string? cached))
        {
            return cached!;
        }

        var resolved = await ResolveFileLinkAsync(fileId);
        cache.Set(cacheKey, resolved, LinkCacheDuration);
        return resolved;
    }

    // Unlike GetFileLinkAsync, NO cache here: album.json is rewritten IN PLACE on
    // every album mutation (same fileid kept — see renameifexists=0 in
    // UploadTextFileAsync), so a cached link would point to stale content
    // for the entire cache duration (up to 20 min) after a write — observed in
    // practice: a text block that was added would disappear on the next read for as long as the
    // cache hadn't expired. album.json is small and rarely read (once per album
    // opening): no need for the same optimization as thumbnails/videos.
    public async Task<string> DownloadTextFileAsync(long fileId)
    {
        var url = await ResolveFileLinkAsync(fileId);
        return await httpClient.GetStringAsync(url);
    }

    // Partial download (HTTP Range header) — the EXIF (and the TIFF/IFD that RAW
    // formats like CR2 derive from) sits at the start of the file, no need for the tens of MB
    // of the complete file to extract it (step 9). Reuses GetFileLinkAsync (cached,
    // immutable file once uploaded).
    public async Task<byte[]> DownloadPartialAsync(long fileId, int maxBytes, CancellationToken ct = default)
    {
        var url = await GetFileLinkAsync(fileId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, maxBytes - 1);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // Suffix Range request (bytes=-N, the last N bytes) — see issue #21: fallback for when
    // the "moov" atom of a QuickTime/MP4 video isn't at the start of the file (no "faststart").
    // RangeHeaderValue(null, maxBytes) is the standard .NET representation of a suffix-range.
    public async Task<byte[]> DownloadTailAsync(long fileId, int maxBytes, CancellationToken ct = default)
    {
        var url = await GetFileLinkAsync(fileId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(null, maxBytes);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // Full download of the original file (issue #1 — download from the fullscreen
    // view): unlike DownloadPartialAsync, the entire file is buffered here before
    // returning it, so the pCloud response can be released immediately (see MediaController.
    // Download) rather than keeping a network stream open for the time it takes MVC to write the
    // response — a rare, explicit usage (a user click), the memory buffering stays acceptable
    // even for a video.
    public async Task<(byte[] Bytes, string? ContentType)> DownloadAsync(long fileId, CancellationToken ct = default)
    {
        var url = await GetFileLinkAsync(fileId);
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return (bytes, response.Content.Headers.ContentType?.ToString());
    }

    // Resolves a FRESH link (not GetThumbLinkAsync/its 20 min memory cache) before
    // consuming it immediately — bug observed in practice while developing issue #26:
    // a pCloud link returned by getthumblink appears to be single-use. Reusing it from the
    // memory cache (originally designed for the old redirection flow, where the browser only
    // consumed the link once) caused a "410 Gone" as soon as a second actual byte
    // download landed on the same cached link (page reload, a failed disk write
    // followed by a retry, etc.) — silently swallowed by the controller's catch,
    // which manifested as thumbnails that never load rather than a visible
    // error. A link resolved on every call is never shared between two consumptions.
    public async Task<(byte[] Bytes, string? ContentType)> GetThumbnailAsync(
        long fileId, int width, int height, bool crop = false, CancellationToken ct = default)
    {
        var url = await ResolveThumbLinkAsync(fileId, width, height, crop);
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return (bytes, response.Content.Headers.ContentType?.ToString());
    }

    // File name directly from pCloud (not from MediaIndex, which only indexes the
    // configured source folders) — needed for an album's media, copied into the
    // album's folder and therefore absent from MediaIndex (see issue #1, download from
    // the Album's fullscreen view).
    public async Task<string> GetFileNameAsync(long fileId)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/stat", new Dictionary<string, string?>
        {
            ["fileid"] = fileId.ToString(),
            ["access_token"] = connection.AccessToken,
        });

        var response = await httpClient.GetFromJsonAsync<PCloudFileOperationResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (stat).");

        if (response.Result != 0 || response.Metadata is null)
        {
            throw new InvalidOperationException($"Erreur pCloud stat (result={response.Result}: {response.Error}).");
        }

        return response.Metadata.Name;
    }

    public async Task<(long UsedBytes, long TotalBytes)> GetQuotaAsync()
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/userinfo", new Dictionary<string, string?>
        {
            ["access_token"] = connection.AccessToken,
        });

        var response = await httpClient.GetFromJsonAsync<PCloudUserInfoResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (userinfo).");

        if (response.Result != 0)
        {
            throw new InvalidOperationException($"Erreur pCloud userinfo (result={response.Result}: {response.Error}).");
        }

        return (response.UsedQuota, response.Quota);
    }

    private async Task<string> ResolveFileLinkAsync(long fileId)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/getfilelink", new Dictionary<string, string?>
        {
            ["fileid"] = fileId.ToString(),
            ["access_token"] = connection.AccessToken,
        });

        var link = await httpClient.GetFromJsonAsync<PCloudThumbLinkResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (getfilelink).");

        if (link.Result != 0 || link.Hosts is not { Length: > 0 } || string.IsNullOrEmpty(link.Path))
        {
            throw new InvalidOperationException($"Erreur pCloud getfilelink (result={link.Result}: {link.Error}).");
        }

        return $"https://{link.Hosts[0]}{link.Path}";
    }

    private async Task<PCloudConnectionInfo> RequireConnectionAsync()
    {
        return await tokenStore.GetAsync()
            ?? throw new InvalidOperationException("pCloud non connecté.");
    }

    private static void AppendField(StringBuilder body, string boundary, string name, string value)
    {
        body.Append($"--{boundary}\r\n");
        body.Append($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
        body.Append(value);
        body.Append("\r\n");
    }
}
