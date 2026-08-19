using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RPhotoAlbum.Api.PCloud;

namespace RPhotoAlbum.Api.Controllers;

[ApiController]
public class PCloudController(
    IPCloudClient client,
    PCloudTokenStore tokenStore,
    IOptions<PCloudOptions> options,
    ILogger<PCloudController> logger) : ControllerBase
{
    private const string StateCookieName = "pcloud_oauth_state";

    // Démarre le flux OAuth 2.0 (code flow) — voir ARCHITECTURE.md §5.1.
    [HttpGet("api/auth/pcloud/start")]
    public IActionResult Start()
    {
        if (string.IsNullOrEmpty(options.Value.ClientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Intégration pCloud non configurée (PCloud__ClientId)." });
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(StateCookieName, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10),
        });

        return Redirect(client.BuildAuthorizeUrl(state));
    }

    [HttpGet("api/auth/pcloud/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? hostname,
        [FromQuery] int? locationid,
        [FromQuery] string? error)
    {
        var expectedState = Request.Cookies[StateCookieName];
        Response.Cookies.Delete(StateCookieName);

        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("Autorisation pCloud refusée : {Error}", error);
            return Redirect("/?pcloud=error");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) ||
            expectedState is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(expectedState)))
        {
            logger.LogWarning("Callback pCloud rejeté : code ou state invalide.");
            return Redirect("/?pcloud=error");
        }

        var resolvedHostname = PCloudHostnameResolver.Resolve(hostname, locationid);

        try
        {
            var token = await client.ExchangeCodeAsync(code, resolvedHostname);
            await tokenStore.SaveAsync(resolvedHostname, token.AccessToken);
            return Redirect("/?pcloud=connected");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec de l'échange du code pCloud.");
            return Redirect("/?pcloud=error");
        }
    }

    [HttpGet("api/pcloud/status")]
    public async Task<IActionResult> Status()
    {
        var connection = await tokenStore.GetAsync();
        return Ok(new { connected = connection is not null, hostname = connection?.Hostname });
    }

    [HttpPost("api/pcloud/disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await tokenStore.ClearAsync();
        return Ok();
    }

    // Navigation de dossiers pour le sélecteur (dossier des albums / dossiers sources) — voir ARCHITECTURE.md §11.1.
    [HttpGet("api/pcloud/folders/{folderId:long}")]
    public async Task<IActionResult> Folders(long folderId)
    {
        try
        {
            var listing = await client.ListFolderAsync(folderId, nofiles: true);
            var metadata = listing.Metadata!;

            var subfolders = (metadata.Contents ?? [])
                .Where(c => c.IsFolder && c.FolderId is not null)
                .Select(c => new { id = c.FolderId, name = c.Name })
                .ToList();

            return Ok(new
            {
                id = metadata.FolderId ?? folderId,
                name = metadata.Name,
                path = metadata.Path ?? "/",
                subfolders,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la lecture du dossier pCloud {FolderId}.", folderId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Impossible de lire ce dossier pCloud : {ex.Message}" });
        }
    }
}
