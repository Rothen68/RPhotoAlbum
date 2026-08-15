using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace RPhotoAlbum.Api.PCloud;

// Encapsule les appels API pCloud — voir ARCHITECTURE.md §9.3.
public class PCloudClient(HttpClient httpClient, IOptions<PCloudOptions> options, PCloudTokenStore tokenStore)
{
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

        // access_token reste en query string (comme tous les autres appels — l'auth pCloud
        // n'est reconnue que là). Corps multipart construit à la main, en octets bruts :
        // MultipartFormDataContent produit un corps que le parseur de pCloud accepte
        // (result=0) sans jamais traiter le fichier (metadata/fileids vides) — cause exacte
        // non identifiée, cette construction manuelle suit strictement la RFC 7578 et évite
        // toute variation générée par .NET (guillemets de boundary, en-têtes implicites…).
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

    public async Task<string> DownloadTextFileAsync(long fileId)
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

        return await httpClient.GetStringAsync($"https://{link.Hosts[0]}{link.Path}");
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
