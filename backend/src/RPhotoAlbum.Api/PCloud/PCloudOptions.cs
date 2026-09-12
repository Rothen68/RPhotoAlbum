namespace RPhotoAlbum.Api.PCloud;

// pCloud OAuth 2.0 application — see ARCHITECTURE.md §5.1.
// Set via PCloud__ClientId / PCloud__ClientSecret / PCloud__RedirectUri.
public class PCloudOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
}
