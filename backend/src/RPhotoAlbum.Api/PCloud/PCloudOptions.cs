namespace RPhotoAlbum.Api.PCloud;

// Application OAuth 2.0 pCloud — voir ARCHITECTURE.md §5.1.
// Renseigné via PCloud__ClientId / PCloud__ClientSecret / PCloud__RedirectUri.
public class PCloudOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}
