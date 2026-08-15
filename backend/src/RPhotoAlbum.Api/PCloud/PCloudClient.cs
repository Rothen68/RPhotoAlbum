using System.Net.Http.Json;
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

    public async Task<string> GetThumbLinkAsync(long fileId, int width, int height)
    {
        var connection = await RequireConnectionAsync();
        var url = QueryHelpers.AddQueryString($"https://{connection.Hostname}/getthumblink", new Dictionary<string, string?>
        {
            ["fileid"] = fileId.ToString(),
            ["size"] = $"{width}x{height}",
            ["access_token"] = connection.AccessToken,
        });

        var thumb = await httpClient.GetFromJsonAsync<PCloudThumbLinkResponse>(url)
            ?? throw new InvalidOperationException("Réponse pCloud invalide (getthumblink).");

        if (thumb.Result != 0 || thumb.Hosts is not { Length: > 0 } || string.IsNullOrEmpty(thumb.Path))
        {
            throw new InvalidOperationException($"Erreur pCloud getthumblink (result={thumb.Result}: {thumb.Error}).");
        }

        return $"https://{thumb.Hosts[0]}{thumb.Path}";
    }

    private async Task<PCloudConnectionInfo> RequireConnectionAsync()
    {
        return await tokenStore.GetAsync()
            ?? throw new InvalidOperationException("pCloud non connecté.");
    }
}
